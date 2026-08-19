namespace TradeDesktop.Infrastructure.Mt5Bridge;

/// <summary>
/// Shared-memory layout v4. Every value must remain synchronized with
/// DataExporter/MQL5/SharedMem.mqh.
/// </summary>
public static class Mt5BridgeLayout
{
    public const uint Magic = 0x43505452; // 'CPTR'
    public const uint Version = 4;

    public const int HeaderSize = 64;
    public const int MaxLanes = 8;
    public const int RegistryEntrySize = 32;
    public const int Capacity = 256;
    public const int SlotSize = 1024;

    public const int HeaderMagicOffset = 0;
    public const int HeaderVersionOffset = 4;
    public const int HeaderCapacityOffset = 8;
    public const int HeaderSlotSizeOffset = 12;
    public const int HeaderMaxLanesOffset = 16;
    public const int HeaderCreateTickOffset = 24;

    public const int RegistryOffset = HeaderSize;
    public const int RegistryClaimedOffset = 0;
    public const int RegistryWriterUidOffset = 4;
    public const int RegistryHeartbeatOffset = 8;
    public const int RegistryWriteSequenceOffset = 16;
    public const int RegistryGenerationOffset = 24;

    public const int SlotSequenceBeginOffset = 0;
    public const int SlotLengthOffset = 8;
    public const int SlotPayloadOffset = 16;
    public const int SlotSequenceEndOffset = SlotSize - sizeof(ulong);
    public const int MaxPayloadSize = SlotSequenceEndOffset - SlotPayloadOffset;

    public const int RegistrySize = MaxLanes * RegistryEntrySize;
    public const int LanesOffset = RegistryOffset + RegistrySize;
    public const int LaneSize = Capacity * SlotSize;
    public const int RegionSize = LanesOffset + (MaxLanes * LaneSize);

    public static string GetRegionName(string roomId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        return $"Local\\CopyTradeRoom_{roomId.Trim()}";
    }

    public static int GetRegistryOffset(int lane)
    {
        ValidateLane(lane);
        return RegistryOffset + (lane * RegistryEntrySize);
    }

    public static int GetSlotOffset(int lane, ulong sequence)
    {
        ValidateLane(lane);
        var slot = (int)(sequence & (Capacity - 1));
        return LanesOffset + (lane * LaneSize) + (slot * SlotSize);
    }

    private static void ValidateLane(int lane)
    {
        if ((uint)lane >= MaxLanes)
        {
            throw new ArgumentOutOfRangeException(nameof(lane), lane, $"Lane must be between 0 and {MaxLanes - 1}.");
        }
    }
}
