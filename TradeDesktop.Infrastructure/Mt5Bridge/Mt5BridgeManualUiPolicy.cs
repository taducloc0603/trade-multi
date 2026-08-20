namespace TradeDesktop.Infrastructure.Mt5Bridge;

public static class Mt5BridgeManualUiPolicy
{
    public const string VolumeNotPrimed = "volume_not_primed";

    /// <summary>
    /// A missing volume prime is recoverable because OCTBridge validates and
    /// primes the requested volume before it dispatches the BUY/SELL click.
    /// All other manual-UI readiness failures remain fail-closed.
    /// </summary>
    public static bool CanAttemptOpen(Mt5BridgeHealth health) =>
        health.ManualUiReady is true ||
        string.Equals(
            health.ManualUiCode,
            VolumeNotPrimed,
            StringComparison.OrdinalIgnoreCase);
}
