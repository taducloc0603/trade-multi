using TradeDesktop.App.State;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Services.CTrader;

namespace TradeDesktop.App.Services;

// Chưa đăng ký DI ở Phase 2 — decorator trades/history (Phase 5/6) mới dùng. Luật nằm ở CTraderRoutingRules.
public sealed class CTraderRouting(RuntimeConfigState runtimeConfigState) : ICTraderRouting
{
    public bool IsCTraderExchangeB => CTraderRoutingRules.IsCTraderPlatform(runtimeConfigState.CurrentPlatformB);

    public bool IsCTraderTradeMap(string? mapName)
        => CTraderRoutingRules.IsCTraderTradeMap(runtimeConfigState.CurrentPlatformB, mapName);

    public bool IsCTraderHistoryMap(string? mapName)
        => CTraderRoutingRules.IsCTraderHistoryMap(runtimeConfigState.CurrentPlatformB, mapName);
}
