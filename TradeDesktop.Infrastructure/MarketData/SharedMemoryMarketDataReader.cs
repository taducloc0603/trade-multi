using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Infrastructure.MarketData;

public sealed class SharedMemoryMarketDataReader : ISharedMemoryReader
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly decimal MaxReasonableLatencyMs = 86_400_000m; // 24h

    private const int ExpectedVersion = 1;

    private const long VersionOffset = 0;
    private const long TimestampMsOffset = 4;
    private const long BidOffset = 16;
    private const long AskOffset = 24;
    private const long SpreadOffset = 32;
    private const long TickTimeMscOffset = 40;
    private const long SymbolOffset = 48;
    private const int MaxSymbolBytesToRead = 64;

    private readonly object _syncRoot = new();
    private readonly IRuntimeConfigProvider _runtimeConfigProvider;
    private readonly Dictionary<string, MapReaderHandle> _mapReaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LatencyAccumulator> _latencyStatsByMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TpsAccumulator> _tpsStatsByMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastTimestampMsByMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal?> _lastLatencyMsByMap = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public SharedMemoryMarketDataReader(IRuntimeConfigProvider runtimeConfigProvider)
    {
        _runtimeConfigProvider = runtimeConfigProvider;
    }

    public event EventHandler<SharedMemorySnapshot>? SnapshotReceived;

    public bool IsRunning { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _worker = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);
            IsRunning = true;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? worker;

        lock (_syncRoot)
        {
            if (!IsRunning)
            {
                return;
            }

            _cts?.Cancel();
            worker = _worker;
            _worker = null;
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }

        if (worker is null)
        {
            return;
        }

        try
        {
            await worker.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        finally
        {
            DisposeAllMapReaders();
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var mapName1 = _runtimeConfigProvider.CurrentMapName1;
            var mapName2 = _runtimeConfigProvider.CurrentMapName2;

            RefreshMapReaders(mapName1, mapName2);

            var sanA = ReadExchangeMetrics(mapName1, "SanA");
            var sanB = ReadExchangeMetrics(mapName2, "SanB");

            SnapshotReceived?.Invoke(this, new SharedMemorySnapshot(sanA, sanB, DateTime.UtcNow));
        }
    }

    private ExchangeMetrics ReadExchangeMetrics(string? mapName, string fallbackSymbol)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            return Disconnected(fallbackSymbol, "Map name rỗng");
        }

        var normalizedMapName = mapName.Trim();

        try
        {
            var handle = GetOrCreateMapReader(normalizedMapName);
            if (!TryReadTickRecord(handle.Accessor, out var tickRecord, out var error))
            {
                return Disconnected(fallbackSymbol, error ?? "Tick data không hợp lệ");
            }

            var symbol = string.IsNullOrWhiteSpace(tickRecord.Symbol)
                ? fallbackSymbol
                : tickRecord.Symbol;

            var bidDecimal = (decimal)tickRecord.Bid;
            var askDecimal = (decimal)tickRecord.Ask;
            var spreadDecimal = (decimal)tickRecord.Spread;

            Debug.WriteLine(
                $"[SHM:{normalizedMapName}] version={tickRecord.Version} symbol={symbol} tsMs={tickRecord.TimestampMs} tickTimeMsc={tickRecord.TickTimeMsc} bid={tickRecord.Bid} ask={tickRecord.Ask} spread={tickRecord.Spread}");

            var isNewTick = MarkAndCheckNewTick(normalizedMapName, tickRecord.TimestampMs);
            decimal? latencyMs;
            decimal? maxLatMs;
            decimal? avgLatMs;
            float? tps;

            if (isNewTick)
            {
                var receivedTickCountMs = Environment.TickCount64;
                latencyMs = TryComputeLatencyMs(receivedTickCountMs, tickRecord.TimestampMs);
                SetLastLatency(normalizedMapName, latencyMs);
                (maxLatMs, avgLatMs) = UpdateLatencyStats(normalizedMapName, latencyMs);
                tps = UpdateTpsStats(normalizedMapName);
            }
            else
            {
                latencyMs = GetLastLatency(normalizedMapName);
                (maxLatMs, avgLatMs) = GetLatencyStats(normalizedMapName);
                tps = GetLastTps(normalizedMapName);
            }

            return new ExchangeMetrics(
                Symbol: symbol,
                Bid: bidDecimal,
                Ask: askDecimal,
                Spread: spreadDecimal,
                LatencyMs: latencyMs,
                Tps: tps,
                Time: FormatTickTime(tickRecord.TickTimeMsc),
                MaxLatMs: maxLatMs,
                AvgLatMs: avgLatMs,
                IsConnected: true,
                Error: null);
        }
        catch (FileNotFoundException)
        {
            RemoveMapReader(normalizedMapName);
            return Disconnected(fallbackSymbol, $"Map không tồn tại: {mapName}");
        }
        catch (Exception ex)
        {
            RemoveMapReader(normalizedMapName);
            return Disconnected(fallbackSymbol, ex.Message);
        }
    }

    private MapReaderHandle GetOrCreateMapReader(string mapName)
    {
        lock (_syncRoot)
        {
            if (_mapReaders.TryGetValue(mapName, out var existing))
            {
                return existing;
            }

            var mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
            var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var created = new MapReaderHandle(mmf, accessor);
            _mapReaders[mapName] = created;
            return created;
        }
    }

    private void RefreshMapReaders(string? mapName1, string? mapName2)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(mapName1))
        {
            keep.Add(mapName1.Trim());
        }

        if (!string.IsNullOrWhiteSpace(mapName2))
        {
            keep.Add(mapName2.Trim());
        }

        lock (_syncRoot)
        {
            var stale = _mapReaders.Keys.Where(k => !keep.Contains(k)).ToList();
            foreach (var key in stale)
            {
                _mapReaders[key].Dispose();
                _mapReaders.Remove(key);
                _latencyStatsByMap.Remove(key);
                _tpsStatsByMap.Remove(key);
                _lastTimestampMsByMap.Remove(key);
                _lastLatencyMsByMap.Remove(key);
            }
        }
    }

    private void RemoveMapReader(string mapName)
    {
        lock (_syncRoot)
        {
            if (!_mapReaders.TryGetValue(mapName, out var handle))
            {
                return;
            }

            handle.Dispose();
            _mapReaders.Remove(mapName);
            _latencyStatsByMap.Remove(mapName);
            _tpsStatsByMap.Remove(mapName);
            _lastTimestampMsByMap.Remove(mapName);
            _lastLatencyMsByMap.Remove(mapName);
        }
    }

    private void DisposeAllMapReaders()
    {
        lock (_syncRoot)
        {
            foreach (var handle in _mapReaders.Values)
            {
                handle.Dispose();
            }

            _mapReaders.Clear();
            _latencyStatsByMap.Clear();
            _tpsStatsByMap.Clear();
            _lastTimestampMsByMap.Clear();
            _lastLatencyMsByMap.Clear();
        }
    }

    private bool MarkAndCheckNewTick(string mapName, long timestampMs)
    {
        lock (_syncRoot)
        {
            if (_lastTimestampMsByMap.TryGetValue(mapName, out var lastTimestamp) &&
                lastTimestamp == timestampMs)
            {
                return false;
            }

            _lastTimestampMsByMap[mapName] = timestampMs;
            return true;
        }
    }

    private void SetLastLatency(string mapName, decimal? latencyMs)
    {
        lock (_syncRoot)
        {
            _lastLatencyMsByMap[mapName] = latencyMs;
        }
    }

    private decimal? GetLastLatency(string mapName)
    {
        lock (_syncRoot)
        {
            return _lastLatencyMsByMap.TryGetValue(mapName, out var latencyMs)
                ? latencyMs
                : null;
        }
    }

    private (decimal? MaxLatMs, decimal? AvgLatMs) GetLatencyStats(string mapName)
    {
        lock (_syncRoot)
        {
            if (!_latencyStatsByMap.TryGetValue(mapName, out var accumulator))
            {
                return (null, null);
            }

            return (accumulator.Max, accumulator.Avg);
        }
    }

    private (decimal? MaxLatMs, decimal? AvgLatMs) UpdateLatencyStats(string mapName, decimal? latencyMs)
    {
        if (!latencyMs.HasValue)
        {
            return (null, null);
        }

        lock (_syncRoot)
        {
            if (!_latencyStatsByMap.TryGetValue(mapName, out var accumulator))
            {
                accumulator = new LatencyAccumulator();
                _latencyStatsByMap[mapName] = accumulator;
            }

            accumulator.Add(latencyMs.Value);
            return (accumulator.Max, accumulator.Avg);
        }
    }

    private float? UpdateTpsStats(string mapName)
    {
        var currentSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (_syncRoot)
        {
            if (!_tpsStatsByMap.TryGetValue(mapName, out var accumulator))
            {
                accumulator = new TpsAccumulator();
                _tpsStatsByMap[mapName] = accumulator;
            }

            return accumulator.Add(currentSecond);
        }
    }

    private float? GetLastTps(string mapName)
    {
        lock (_syncRoot)
        {
            if (!_tpsStatsByMap.TryGetValue(mapName, out var accumulator))
            {
                return null;
            }

            return accumulator.LastTps;
        }
    }

    private static bool TryReadTickRecord(
        MemoryMappedViewAccessor accessor,
        out SharedMemoryTickRecord tickRecord,
        out string? error)
    {
        tickRecord = new SharedMemoryTickRecord(0, 0, 0, 0, 0, 0, string.Empty);
        error = null;

        var capacity = accessor.Capacity;
        if (capacity <= SymbolOffset)
        {
            error = $"Buffer quá ngắn: {capacity} bytes";
            return false;
        }

        var version = accessor.ReadInt32(VersionOffset);
        if (version != ExpectedVersion)
        {
            error = $"Version không đúng kỳ vọng: {version}";
            return false;
        }

        var timestampMs = accessor.ReadInt64(TimestampMsOffset);
        if (timestampMs <= 0)
        {
            error = "TimestampMs không hợp lệ";
            return false;
        }

        var bid = accessor.ReadDouble(BidOffset);
        var ask = accessor.ReadDouble(AskOffset);
        var spread = accessor.ReadDouble(SpreadOffset);
        var tickTimeMsc = accessor.ReadInt64(TickTimeMscOffset);

        if (double.IsNaN(bid) || double.IsInfinity(bid) || bid <= 0 ||
            double.IsNaN(ask) || double.IsInfinity(ask) || ask <= 0 ||
            double.IsNaN(spread) || double.IsInfinity(spread))
        {
            error = "Bid/Ask/Spread không hợp lệ";
            return false;
        }

        var symbol = ReadNullTerminatedAscii(accessor, SymbolOffset, capacity);
        if (string.IsNullOrWhiteSpace(symbol))
        {
            error = "Symbol rỗng";
            return false;
        }

        tickRecord = new SharedMemoryTickRecord(
            Version: version,
            TimestampMs: timestampMs,
            Bid: bid,
            Ask: ask,
            Spread: spread,
            TickTimeMsc: tickTimeMsc,
            Symbol: symbol);

        return true;
    }

    private static string ReadNullTerminatedAscii(MemoryMappedViewAccessor accessor, long offset, long capacity)
    {
        var available = capacity - offset;
        if (available <= 0)
        {
            return string.Empty;
        }

        var lengthToRead = (int)Math.Min(available, MaxSymbolBytesToRead);
        var bytes = new byte[lengthToRead];
        accessor.ReadArray(offset, bytes, 0, lengthToRead);

        var nullIndex = Array.IndexOf(bytes, (byte)0);
        var symbolLength = nullIndex >= 0 ? nullIndex : bytes.Length;
        if (symbolLength <= 0)
        {
            return string.Empty;
        }

        return Encoding.ASCII.GetString(bytes, 0, symbolLength).Trim();
    }

    private static string FormatTickTime(long timestampMs)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(timestampMs)
                .ToLocalTime()
                .ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
        catch
        {
            return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }

    private static decimal? TryComputeLatencyMs(long nowTickCountMs, long tickCountMs)
    {
        try
        {
            var diff = nowTickCountMs - tickCountMs;
            if (diff < 0)
            {
                return 0;
            }

            var latency = (decimal)diff;
            return latency > MaxReasonableLatencyMs ? null : latency;
        }
        catch
        {
            return null;
        }
    }

    private static ExchangeMetrics Disconnected(string symbol, string error)
        => new(
            Symbol: symbol,
            Bid: null,
            Ask: null,
            Spread: null,
            LatencyMs: null,
            Tps: null,
            Time: DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            MaxLatMs: null,
            AvgLatMs: null,
            IsConnected: false,
            Error: error);

    private sealed class MapReaderHandle(MemoryMappedFile mmf, MemoryMappedViewAccessor accessor) : IDisposable
    {
        public MemoryMappedFile Mmf { get; } = mmf;
        public MemoryMappedViewAccessor Accessor { get; } = accessor;

        public void Dispose()
        {
            Accessor.Dispose();
            Mmf.Dispose();
        }
    }

    private sealed class LatencyAccumulator
    {
        private decimal _sum;
        private long _count;
        private decimal _max;

        public decimal? Max => _count > 0 ? _max : null;
        public decimal? Avg => _count > 0 ? _sum / _count : null;

        public void Add(decimal latency)
        {
            if (_count == 0 || latency > _max)
            {
                _max = latency;
            }

            _sum += latency;
            _count++;
        }
    }

    private sealed class TpsAccumulator
    {
        private long? _currentSecond;
        private int _tickCountInCurrentSecond;
        private float _lastTps;
        private bool _hasValue;

        public float? LastTps => _hasValue ? _lastTps : null;

        public float Add(long second)
        {
            if (!_currentSecond.HasValue)
            {
                _currentSecond = second;
                _tickCountInCurrentSecond = 1;
                _lastTps = 1f;
                _hasValue = true;
                return _lastTps;
            }

            if (second == _currentSecond.Value)
            {
                _tickCountInCurrentSecond++;
                _lastTps = _tickCountInCurrentSecond;
                return _lastTps;
            }

            _lastTps = _tickCountInCurrentSecond;
            _currentSecond = second;
            _tickCountInCurrentSecond = 1;
            return _lastTps;
        }
    }
}