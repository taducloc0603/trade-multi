using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.CTrader;

namespace TradeDesktop.Infrastructure.CTrader;

// Phase 6 — decorator IHistorySharedMemoryReader, cùng khuôn CTraderAwareTradesReader. Map không phải
// CTRADER_B_History (hoặc platform_b != ctrader) → gọi thẳng reader MMF, KHÔNG đổi gì. Map cTrader → lịch sử từ
// TRADE session (R2 cùng bảng sức khoẻ với trades, R3 version riêng, R10 Profit/Commission tính lại).
//
// KHÔNG gọi EnsureState: vòng đời TRADE session do decorator trades điều khiển (đọc cùng nhịp poll 500 ms). Rollback =
// revert đúng dòng đăng ký decorator này.
public sealed class CTraderAwareHistoryReader : IHistorySharedMemoryReader
{
    private readonly IHistorySharedMemoryReader _inner;
    private readonly IRuntimeConfigProvider _runtimeConfigProvider;
    private readonly ICTraderTradeSession _tradeSession;
    private readonly ICTraderQuoteSession _quoteSession;

    public CTraderAwareHistoryReader(
        IHistorySharedMemoryReader inner,
        IRuntimeConfigProvider runtimeConfigProvider,
        ICTraderTradeSession tradeSession,
        ICTraderQuoteSession quoteSession)
    {
        _inner = inner;
        _runtimeConfigProvider = runtimeConfigProvider;
        _tradeSession = tradeSession;
        _quoteSession = quoteSession;
    }

    public SharedMapReadResult<HistorySharedRecord> ReadHistory(string mapName)
    {
        if (!CTraderRoutingRules.IsCTraderHistoryMap(_runtimeConfigProvider.CurrentPlatformB, mapName))
        {
            return _inner.ReadHistory(mapName);
        }

        return _tradeSession.ReadHistory(mapName, _quoteSession.IsQuoteLoggedOn, _runtimeConfigProvider.CurrentPoint);
    }
}
