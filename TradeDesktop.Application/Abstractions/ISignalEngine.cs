using TradeDesktop.Domain.Models;

namespace TradeDesktop.Application.Abstractions;

public interface ISignalEngine
{
    SignalResult Calculate(MarketData marketData);
}