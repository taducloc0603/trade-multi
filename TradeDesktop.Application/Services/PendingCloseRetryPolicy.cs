namespace TradeDesktop.Application.Services;

public static class PendingCloseRetryPolicy
{
    private const int MaximumBackoffSeconds = 30;

    public static TimeSpan GetBackoff(int retryNumber)
    {
        var normalized = Math.Max(1, retryNumber);
        var exponent = Math.Min(5, normalized - 1);
        var seconds = Math.Min(MaximumBackoffSeconds, 1 << exponent);
        return TimeSpan.FromSeconds(seconds);
    }
}
