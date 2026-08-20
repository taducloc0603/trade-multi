using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class PendingCloseRetryPolicyTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(100, 30)]
    public void GetBackoff_UsesExponentialDelayCappedAtThirtySeconds(
        int retryNumber,
        int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            PendingCloseRetryPolicy.GetBackoff(retryNumber));
    }
}
