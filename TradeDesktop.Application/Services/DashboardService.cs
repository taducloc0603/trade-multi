using TradeDesktop.Application.Abstractions;
using TradeDesktop.Domain.Models;

namespace TradeDesktop.Application.Services;

public interface IDashboardService
{
    SignalResult EvaluateSignal(MarketData marketData);
}

public sealed class DashboardService(ISignalEngine signalEngine) : IDashboardService
{
    public SignalResult EvaluateSignal(MarketData marketData) => signalEngine.Calculate(marketData);
}