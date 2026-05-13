using TradeDesktop.Application.Abstractions;
using TradeDesktop.Domain.Models;
using DomainMarketData = TradeDesktop.Domain.Models.MarketData;

namespace TradeDesktop.Infrastructure.Signals;

public sealed class SimpleSignalEngine : ISignalEngine
{
    public SignalResult Calculate(DomainMarketData marketData)
    {
        if (!marketData.IsConnected)
        {
            return new SignalResult(SignalType.Hold, "No market connection");
        }

        if (marketData.Spread > 0.03m)
        {
            return new SignalResult(SignalType.Hold, "Spread too wide");
        }

        if (marketData.Bid >= 100.10m)
        {
            return new SignalResult(SignalType.Sell, "Bid above resistance zone");
        }

        if (marketData.Ask <= 99.90m)
        {
            return new SignalResult(SignalType.Buy, "Ask below support zone");
        }

        return new SignalResult(SignalType.Hold, "No edge in current range");
    }
}