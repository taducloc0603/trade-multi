using TradeDesktop.Domain.Models;
using TradeDesktop.Infrastructure.Signals;

namespace TradeDesktop.Tests;

public sealed class SimpleSignalEngineTests
{
    private readonly SimpleSignalEngine _sut = new();

    [Fact]
    public void Calculate_ReturnsHold_WhenDisconnected()
    {
        var marketData = new MarketData(100.0m, 100.01m, DateTime.UtcNow, false);

        var result = _sut.Calculate(marketData);

        Assert.Equal(SignalType.Hold, result.Signal);
    }

    [Fact]
    public void Calculate_ReturnsSell_WhenBidAboveThreshold()
    {
        var marketData = new MarketData(100.12m, 100.13m, DateTime.UtcNow, true);

        var result = _sut.Calculate(marketData);

        Assert.Equal(SignalType.Sell, result.Signal);
    }
}