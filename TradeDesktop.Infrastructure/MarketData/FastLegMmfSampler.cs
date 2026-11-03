using System.IO.MemoryMappedFiles;
using System.Text;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.FastLeg;

namespace TradeDesktop.Infrastructure.MarketData;

/// <summary>
/// Đọc giá sàn C từ MMF của EA DataExporter kênh "C" trên một thread riêng. CHỈ đọc (MemoryMappedFileRights.Read).
/// Cố ý KHÔNG dùng lại SharedMemoryMarketDataReader: vòng 50 ms đó là luồng dữ liệu chính của A/B và RefreshMapReaders
/// dispose mọi map khác map1/map2.
/// </summary>
public sealed class FastLegMmfSampler(
    IRuntimeConfigProvider runtimeConfigProvider,
    FastLegTickBuffer buffer,
    Func<long>? tickCount = null) : IFastLegSampler, IDisposable
{
    // PeriodicTimer thực tế nhảy theo độ phân giải timer của Windows (~15.6 ms); đủ với cửa sổ đo 200 ms.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);
    private const int MapRetryIntervalMs = 1000;
    private const int MaxReadAttempts = 3;
    // Bỏ mẫu time_msc lùi là để chống torn read; quá ngưỡng này thì coi là đồng hồ server đổi thật và chấp nhận.
    private const int MaxConsecutiveBackwardTicks = 20;

    // Layout TickMemory.mqh: header 16 B + body 48 B. Symbol nằm ở byte 48..63 nên phải đọc đủ 64 B.
    private const int TickViewSize = 64;
    private const int VersionOffset = 0;
    private const int TimestampOffset = 4;
    private const int BidOffset = 16;
    private const int AskOffset = 24;
    private const int SpreadOffset = 32;
    private const int TimeMscOffset = 40;
    private const int SymbolOffset = 48;
    private const int SymbolSize = 16;
    private const int SupportedVersion = 1;
    private const double SpreadChecksumTolerance = 1e-9;
    private const double MaxReasonablePrice = 1e12;

    // Layout TradesMemory.mqh: count@0, GetTickCount64@4, TERMINAL_CONNECTED@12. Ghi cả trong OnTimer nên vẫn tươi khi không có tick.
    private const int HeartbeatViewSize = 16;
    private const int HeartbeatTimestampOffset = 4;
    private const int HeartbeatConnectedOffset = 12;

    private readonly Func<long> _tickCount = tickCount ?? (() => Environment.TickCount64);
    private readonly byte[] _symbolBuffer = new byte[SymbolSize];
    private readonly object _lifecycleSync = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private MapHandle? _tickMap;
    private MapHandle? _heartbeatMap;
    private string _tickMapName = string.Empty;
    private string _heartbeatMapName = string.Empty;
    private long _nextTickMapAttemptMs;
    private long _nextHeartbeatMapAttemptMs;

    private bool _hasLast;
    private long _lastTimestamp;
    private long _lastTimeMsc;
    private double _lastBid;
    private double _lastAsk;
    private int _consecutiveBackward;
    private bool _wasActive;

    public void Start()
    {
        lock (_lifecycleSync)
        {
            if (_disposed || _cts is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    public void Dispose()
    {
        lock (_lifecycleSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Một nhịp đọc. Public để test điều khiển từng bước; production chỉ gọi từ thread của RunAsync.</summary>
    public void PollOnce()
    {
        var config = runtimeConfigProvider.CurrentFastLegConfig;
        if (!config.IsActive)
        {
            if (_wasActive)
            {
                ResetMaps();
                _wasActive = false;
            }

            buffer.SetDownReason("disabled");
            return;
        }

        _wasActive = true;
        buffer.SetRetentionMs(config.WindowMs);
        if (!string.Equals(_tickMapName, config.MapName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_heartbeatMapName, config.HeartbeatMapName, StringComparison.OrdinalIgnoreCase))
        {
            ResetMaps();
            _tickMapName = config.MapName;
            _heartbeatMapName = config.HeartbeatMapName;
        }

        try
        {
            ReadHeartbeat();
            ReadTick(config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or ArgumentException or InvalidOperationException)
        {
            // Giữ handle hỏng thì mọi nhịp sau đều hỏng: đóng để lần thử kế tiếp mở lại từ đầu.
            CloseMaps();
            buffer.SetDownReason("read_error");
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!runtimeConfigProvider.CurrentFastLegConfig.IsActive)
                {
                    PollOnceSafe();
                    await Task.Delay(IdleDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                PollOnceSafe();
            }
        }
        catch (OperationCanceledException)
        {
            // Thoát app.
        }
        finally
        {
            CloseMaps();
        }
    }

    private void PollOnceSafe()
    {
        try
        {
            PollOnce();
        }
        catch (Exception)
        {
            // Không bao giờ để chết thread: mất sampler thì tick cũ dần và evaluator tự ra Stale/Down.
            buffer.SetDownReason("sampler_error");
        }
    }

    private void ReadHeartbeat()
    {
        if (string.IsNullOrEmpty(_heartbeatMapName))
        {
            buffer.SetHeartbeat(null);
            return;
        }

        if (_heartbeatMap is null)
        {
            _heartbeatMap = TryOpen(_heartbeatMapName, HeartbeatViewSize, ref _nextHeartbeatMapAttemptMs);
            if (_heartbeatMap is null)
            {
                buffer.SetHeartbeat(null);
                return;
            }
        }

        var accessor = _heartbeatMap.Accessor;
        buffer.SetHeartbeat(new FastLegHeartbeat(
            accessor.ReadInt64(HeartbeatTimestampOffset),
            accessor.ReadInt32(HeartbeatConnectedOffset) != 0));
    }

    private void ReadTick(FastLegConfig config)
    {
        if (_tickMap is null)
        {
            _tickMap = TryOpen(_tickMapName, TickViewSize, ref _nextTickMapAttemptMs);
            if (_tickMap is null)
            {
                buffer.SetDownReason("map_not_found");
                return;
            }
        }

        var accessor = _tickMap.Accessor;
        var version = accessor.ReadInt32(VersionOffset);
        if (version != SupportedVersion)
        {
            buffer.SetDownReason($"bad_version:{version}");
            return;
        }

        // EA ghi body trước header sau, nên đọc header → body → header: khác nhau nghĩa là EA vừa commit tick mới
        // giữa lúc đọc. Header giống nhau vẫn có thể rơi vào lúc EA đang ghi body, phần đó do checksum spread bắt.
        for (var attempt = 0; attempt < MaxReadAttempts; attempt++)
        {
            var timestamp = accessor.ReadInt64(TimestampOffset);
            var bid = accessor.ReadDouble(BidOffset);
            var ask = accessor.ReadDouble(AskOffset);
            var spread = accessor.ReadDouble(SpreadOffset);
            var timeMsc = accessor.ReadInt64(TimeMscOffset);
            if (accessor.ReadInt64(TimestampOffset) != timestamp)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(config.Symbol))
            {
                var symbol = ReadSymbol(accessor);
                if (!string.Equals(symbol, config.Symbol, StringComparison.OrdinalIgnoreCase))
                {
                    buffer.SetDownReason($"symbol_mismatch:{symbol}");
                    return;
                }
            }

            buffer.SetDownReason(null);
            if (timestamp > 0 && IsConsistent(bid, ask, spread))
            {
                AcceptIfNew(timestamp, timeMsc, bid, ask);
            }

            return;
        }
    }

    private static bool IsConsistent(double bid, double ask, double spread)
    {
        if (!double.IsFinite(bid) || !double.IsFinite(ask) || !double.IsFinite(spread))
        {
            return false;
        }

        if (bid <= 0d || ask <= 0d || bid > MaxReasonablePrice || ask > MaxReasonablePrice)
        {
            return false;
        }

        // EA ghi đúng `spread = ask - bid`: đây là checksum có sẵn để bắt bản ghi trộn hai tick.
        return Math.Abs(ask - bid - spread) <= SpreadChecksumTolerance;
    }

    private void AcceptIfNew(long timestamp, long timeMsc, double bid, double ask)
    {
        if (_hasLast
            && timestamp == _lastTimestamp
            && timeMsc == _lastTimeMsc
            && bid.Equals(_lastBid)
            && ask.Equals(_lastAsk))
        {
            return;
        }

        if (_hasLast && timeMsc < _lastTimeMsc && ++_consecutiveBackward <= MaxConsecutiveBackwardTicks)
        {
            return;
        }

        _consecutiveBackward = 0;
        _hasLast = true;
        _lastTimestamp = timestamp;
        _lastTimeMsc = timeMsc;
        _lastBid = bid;
        _lastAsk = ask;
        buffer.Push(new FastLegTickSample(timestamp, (decimal)bid, (decimal)ask));
    }

    private string ReadSymbol(MemoryMappedViewAccessor accessor)
    {
        accessor.ReadArray(SymbolOffset, _symbolBuffer, 0, SymbolSize);
        var length = Array.IndexOf(_symbolBuffer, (byte)0);
        return Encoding.ASCII.GetString(_symbolBuffer, 0, length < 0 ? SymbolSize : length).Trim();
    }

    private MapHandle? TryOpen(string mapName, int viewSize, ref long nextAttemptMs)
    {
        // Named map chỉ có trên Windows (EA MT5 cũng vậy); guard này để analyzer biết, không đổi hành vi thực tế.
        var nowMs = _tickCount();
        if (nowMs < nextAttemptMs || !OperatingSystem.IsWindows())
        {
            return null;
        }

        nextAttemptMs = nowMs + MapRetryIntervalMs;
        MemoryMappedFile? file = null;
        try
        {
            file = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
            var accessor = file.CreateViewAccessor(0, viewSize, MemoryMappedFileAccess.Read);
            return new MapHandle(file, accessor);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            file?.Dispose();
            return null;
        }
    }

    private void ResetMaps()
    {
        CloseMaps();
        buffer.Reset();
        _tickMapName = string.Empty;
        _heartbeatMapName = string.Empty;
        _nextTickMapAttemptMs = 0;
        _nextHeartbeatMapAttemptMs = 0;
        _hasLast = false;
        _consecutiveBackward = 0;
    }

    private void CloseMaps()
    {
        _tickMap?.Dispose();
        _tickMap = null;
        _heartbeatMap?.Dispose();
        _heartbeatMap = null;
    }

    private sealed class MapHandle(MemoryMappedFile file, MemoryMappedViewAccessor accessor) : IDisposable
    {
        public MemoryMappedViewAccessor Accessor { get; } = accessor;

        public void Dispose()
        {
            Accessor.Dispose();
            file.Dispose();
        }
    }
}
