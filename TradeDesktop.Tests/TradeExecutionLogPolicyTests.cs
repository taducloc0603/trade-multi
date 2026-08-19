using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class TradeExecutionLogPolicyTests
{
    [Fact]
    public void BothLegsSuccessful_WritesPairOpenLog()
    {
        Assert.True(TradeExecutionLogPolicy.ShouldWritePairOpen(true, [true, true]));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    public void FailedOrPartialExecution_DoesNotWritePairOpenLog(
        bool pairSuccess,
        bool legA,
        bool legB)
    {
        Assert.False(TradeExecutionLogPolicy.ShouldWritePairOpen(pairSuccess, [legA, legB]));
    }
}
