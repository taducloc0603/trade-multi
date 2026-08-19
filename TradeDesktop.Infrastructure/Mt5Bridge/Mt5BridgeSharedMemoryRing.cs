using System.IO.MemoryMappedFiles;
using System.Text;

namespace TradeDesktop.Infrastructure.Mt5Bridge;

internal interface IMt5BridgeMemory : IDisposable
{
    uint ReadUInt32(long offset);
    ulong ReadUInt64(long offset);
    void WriteUInt32(long offset, uint value);
    void WriteUInt64(long offset, ulong value);
    void ReadBytes(long offset, byte[] destination, int count);
    void WriteBytes(long offset, byte[] source, int count);
}

internal sealed class Mt5BridgeMappedMemory : IMt5BridgeMemory
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;

    private Mt5BridgeMappedMemory(MemoryMappedFile file, MemoryMappedViewAccessor view)
    {
        _file = file;
        _view = view;
    }

    public static Mt5BridgeMappedMemory CreateOrOpen(string roomId)
    {
        var file = MemoryMappedFile.CreateOrOpen(
            Mt5BridgeLayout.GetRegionName(roomId),
            Mt5BridgeLayout.RegionSize,
            MemoryMappedFileAccess.ReadWrite);
        return new Mt5BridgeMappedMemory(
            file,
            file.CreateViewAccessor(0, Mt5BridgeLayout.RegionSize, MemoryMappedFileAccess.ReadWrite));
    }

    public uint ReadUInt32(long offset) => _view.ReadUInt32(offset);
    public ulong ReadUInt64(long offset) => _view.ReadUInt64(offset);
    public void WriteUInt32(long offset, uint value) => _view.Write(offset, value);
    public void WriteUInt64(long offset, ulong value) => _view.Write(offset, value);
    public void ReadBytes(long offset, byte[] destination, int count) => _view.ReadArray(offset, destination, 0, count);
    public void WriteBytes(long offset, byte[] source, int count) => _view.WriteArray(offset, source, 0, count);

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }
}

internal sealed class Mt5BridgeSharedMemoryRing : IDisposable
{
    private readonly IMt5BridgeMemory _memory;
    private readonly object _writeSync = new();
    private readonly ulong[] _cursors = new ulong[Mt5BridgeLayout.MaxLanes];
    private readonly uint _writerUid;
    private readonly ulong _generation;
    private int _writerLane = -1;

    public Mt5BridgeSharedMemoryRing(IMt5BridgeMemory memory, uint writerUid, ulong generation)
    {
        _memory = memory;
        _writerUid = writerUid == 0 ? throw new ArgumentOutOfRangeException(nameof(writerUid)) : writerUid;
        _generation = generation == 0 ? throw new ArgumentOutOfRangeException(nameof(generation)) : generation;
        InitializeOrValidateHeader();
        _writerLane = ClaimLane();
        for (var lane = 0; lane < Mt5BridgeLayout.MaxLanes; lane++)
        {
            _cursors[lane] = ReadWriteSequence(lane);
        }
        TouchHeartbeat();
    }

    public long GapCount { get; private set; }
    public int WriterLane => _writerLane;

    public bool TryWrite(string message)
    {
        var payload = Encoding.UTF8.GetBytes(message);
        if (payload.Length is 0 or > Mt5BridgeLayout.MaxPayloadSize)
        {
            return false;
        }

        lock (_writeSync)
        {
            var registry = Mt5BridgeLayout.GetRegistryOffset(_writerLane);
            var sequence = _memory.ReadUInt64(registry + Mt5BridgeLayout.RegistryWriteSequenceOffset);
            var slot = Mt5BridgeLayout.GetSlotOffset(_writerLane, sequence);

            _memory.WriteUInt64(slot + Mt5BridgeLayout.SlotSequenceBeginOffset, sequence);
            _memory.WriteUInt32(slot + Mt5BridgeLayout.SlotLengthOffset, (uint)payload.Length);
            _memory.WriteBytes(slot + Mt5BridgeLayout.SlotPayloadOffset, payload, payload.Length);
            _memory.WriteUInt64(slot + Mt5BridgeLayout.SlotSequenceEndOffset, sequence);
            _memory.WriteUInt64(registry + Mt5BridgeLayout.RegistryWriteSequenceOffset, sequence + 1);
            TouchHeartbeat();
            return true;
        }
    }

    public IReadOnlyList<string> Drain()
    {
        TouchHeartbeat();
        var messages = new List<string>();
        for (var lane = 0; lane < Mt5BridgeLayout.MaxLanes; lane++)
        {
            if (lane == _writerLane)
            {
                continue;
            }

            DrainLane(lane, messages);
        }

        return messages;
    }

    public void TouchHeartbeat()
    {
        if (_writerLane < 0)
        {
            return;
        }

        var registry = Mt5BridgeLayout.GetRegistryOffset(_writerLane);
        _memory.WriteUInt64(registry + Mt5BridgeLayout.RegistryGenerationOffset, _generation);
        _memory.WriteUInt64(registry + Mt5BridgeLayout.RegistryHeartbeatOffset, (ulong)Math.Max(1, Environment.TickCount64));
    }

    private void DrainLane(int lane, List<string> messages)
    {
        var head = ReadWriteSequence(lane);
        var cursor = _cursors[lane];
        if (head < cursor)
        {
            _cursors[lane] = head;
            return;
        }

        if (head - cursor > Mt5BridgeLayout.Capacity)
        {
            GapCount += (long)((head - cursor) - Mt5BridgeLayout.Capacity);
            cursor = head - Mt5BridgeLayout.Capacity;
        }

        while (cursor < head)
        {
            var slot = Mt5BridgeLayout.GetSlotOffset(lane, cursor);
            if (_memory.ReadUInt64(slot + Mt5BridgeLayout.SlotSequenceEndOffset) != cursor)
            {
                break;
            }

            var length = _memory.ReadUInt32(slot + Mt5BridgeLayout.SlotLengthOffset);
            if (length is 0 or > Mt5BridgeLayout.MaxPayloadSize)
            {
                cursor++;
                continue;
            }

            var bytes = new byte[(int)length];
            _memory.ReadBytes(slot + Mt5BridgeLayout.SlotPayloadOffset, bytes, bytes.Length);
            if (_memory.ReadUInt64(slot + Mt5BridgeLayout.SlotSequenceBeginOffset) != cursor)
            {
                GapCount++;
                cursor++;
                continue;
            }

            messages.Add(Encoding.UTF8.GetString(bytes));
            cursor++;
        }

        _cursors[lane] = cursor;
    }

    private ulong ReadWriteSequence(int lane)
    {
        var registry = Mt5BridgeLayout.GetRegistryOffset(lane);
        return _memory.ReadUInt64(registry + Mt5BridgeLayout.RegistryWriteSequenceOffset);
    }

    private void InitializeOrValidateHeader()
    {
        var magic = _memory.ReadUInt32(Mt5BridgeLayout.HeaderMagicOffset);
        if (magic == 0)
        {
            _memory.WriteUInt32(Mt5BridgeLayout.HeaderCapacityOffset, Mt5BridgeLayout.Capacity);
            _memory.WriteUInt32(Mt5BridgeLayout.HeaderSlotSizeOffset, Mt5BridgeLayout.SlotSize);
            _memory.WriteUInt32(Mt5BridgeLayout.HeaderMaxLanesOffset, Mt5BridgeLayout.MaxLanes);
            _memory.WriteUInt64(Mt5BridgeLayout.HeaderCreateTickOffset, (ulong)Math.Max(1, Environment.TickCount64));
            _memory.WriteUInt32(Mt5BridgeLayout.HeaderVersionOffset, Mt5BridgeLayout.Version);
            _memory.WriteUInt32(Mt5BridgeLayout.HeaderMagicOffset, Mt5BridgeLayout.Magic);
            return;
        }

        if (magic != Mt5BridgeLayout.Magic ||
            _memory.ReadUInt32(Mt5BridgeLayout.HeaderVersionOffset) != Mt5BridgeLayout.Version ||
            _memory.ReadUInt32(Mt5BridgeLayout.HeaderCapacityOffset) != Mt5BridgeLayout.Capacity ||
            _memory.ReadUInt32(Mt5BridgeLayout.HeaderSlotSizeOffset) != Mt5BridgeLayout.SlotSize ||
            _memory.ReadUInt32(Mt5BridgeLayout.HeaderMaxLanesOffset) != Mt5BridgeLayout.MaxLanes)
        {
            throw new InvalidOperationException("MT5 Bridge shared-memory layout is incompatible.");
        }
    }

    private int ClaimLane()
    {
        for (var lane = 0; lane < Mt5BridgeLayout.MaxLanes; lane++)
        {
            var registry = Mt5BridgeLayout.GetRegistryOffset(lane);
            if (_memory.ReadUInt32(registry + Mt5BridgeLayout.RegistryClaimedOffset) == 1 &&
                _memory.ReadUInt32(registry + Mt5BridgeLayout.RegistryWriterUidOffset) == _writerUid)
            {
                _memory.WriteUInt64(registry + Mt5BridgeLayout.RegistryGenerationOffset, _generation);
                return lane;
            }
        }

        for (var lane = 0; lane < Mt5BridgeLayout.MaxLanes; lane++)
        {
            var registry = Mt5BridgeLayout.GetRegistryOffset(lane);
            if (_memory.ReadUInt32(registry + Mt5BridgeLayout.RegistryClaimedOffset) != 0)
            {
                continue;
            }

            _memory.WriteUInt32(registry + Mt5BridgeLayout.RegistryClaimedOffset, 1);
            _memory.WriteUInt32(registry + Mt5BridgeLayout.RegistryWriterUidOffset, _writerUid);
            _memory.WriteUInt64(registry + Mt5BridgeLayout.RegistryGenerationOffset, _generation);
            if (_memory.ReadUInt32(registry + Mt5BridgeLayout.RegistryWriterUidOffset) == _writerUid)
            {
                return lane;
            }
        }

        throw new InvalidOperationException("MT5 Bridge room has no free writer lane.");
    }

    public void Dispose()
    {
        if (_writerLane >= 0)
        {
            var registry = Mt5BridgeLayout.GetRegistryOffset(_writerLane);
            if (_memory.ReadUInt64(registry + Mt5BridgeLayout.RegistryGenerationOffset) == _generation)
            {
                _memory.WriteUInt64(registry + Mt5BridgeLayout.RegistryHeartbeatOffset, 0);
                _memory.WriteUInt64(registry + Mt5BridgeLayout.RegistryGenerationOffset, 0);
                _memory.WriteUInt32(registry + Mt5BridgeLayout.RegistryWriterUidOffset, 0);
                _memory.WriteUInt32(registry + Mt5BridgeLayout.RegistryClaimedOffset, 0);
            }
            _writerLane = -1;
        }

        _memory.Dispose();
    }
}
