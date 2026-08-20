namespace TradeDesktop.Infrastructure.Mt5Bridge;

public sealed record Mt5BridgeHealth(
    bool IsReady,
    long? Account,
    ulong? Generation,
    DateTime? LastSeenUtc,
    ulong? LastEaMilliseconds,
    uint? WriterUid,
    int? Lane,
    long GapCount,
    long RestartCount,
    bool? ManualUiReady,
    string? ManualUiCode,
    string? ChartSymbol,
    long? ChartHwnd,
    bool? PanelFound,
    int? Dpi,
    string? CoordinateSource)
{
    public static Mt5BridgeHealth Offline { get; } =
        new(false, null, null, null, null, null, null, 0, 0,
            null, null, null, null, null, null, null);
}

public sealed class Mt5BridgeHealthMonitor
{
    private readonly object _sync = new();
    private readonly TimeSpan _staleAfter;
    private Mt5BridgeHealth _health = Mt5BridgeHealth.Offline;

    public Mt5BridgeHealthMonitor(TimeSpan? staleAfter = null)
    {
        _staleAfter = staleAfter ?? TimeSpan.FromSeconds(2);
    }

    public Mt5BridgeHealth Current
    {
        get
        {
            lock (_sync)
            {
                if (_health.LastSeenUtc is not { } lastSeen ||
                    DateTime.UtcNow - lastSeen > _staleAfter)
                {
                    return _health with { IsReady = false };
                }

                return _health;
            }
        }
    }

    internal void Observe(Mt5BridgeInboundMessage message, long gapCount)
    {
        if (message.Type is not ("bridge_ready" or "heartbeat" or "pong"))
        {
            return;
        }

        lock (_sync)
        {
            var restartCount = _health.RestartCount;
            if (_health.Generation is { } previousGeneration &&
                message.Generation is { } currentGeneration &&
                previousGeneration != currentGeneration)
            {
                restartCount++;
            }

            _health = new Mt5BridgeHealth(
                IsReady: true,
                Account: message.Account ?? _health.Account,
                Generation: message.Generation ?? _health.Generation,
                LastSeenUtc: DateTime.UtcNow,
                LastEaMilliseconds: message.EaMilliseconds ?? _health.LastEaMilliseconds,
                WriterUid: message.WriterUid ?? _health.WriterUid,
                Lane: message.Lane ?? _health.Lane,
                GapCount: gapCount,
                RestartCount: restartCount,
                ManualUiReady: message.ManualUiReady ?? _health.ManualUiReady,
                ManualUiCode: message.ManualUiCode ?? _health.ManualUiCode,
                ChartSymbol: message.ChartSymbol ?? _health.ChartSymbol,
                ChartHwnd: message.ChartHwnd ?? _health.ChartHwnd,
                PanelFound: message.PanelFound ?? _health.PanelFound,
                Dpi: message.Dpi ?? _health.Dpi,
                CoordinateSource: message.CoordinateSource ?? _health.CoordinateSource);
        }
    }

    internal void SetGapCount(long gapCount)
    {
        lock (_sync)
        {
            _health = _health with { GapCount = gapCount };
        }
    }
}
