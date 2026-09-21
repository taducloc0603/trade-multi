using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.CTrader;

namespace TradeDesktop.Infrastructure.CTrader;

// Phase 5 — decorator ITradesSharedMemoryReader. Map không phải CTRADER_B_Trades (hoặc platform_b != ctrader) → gọi
// thẳng reader MMF, KHÔNG đổi gì. Map cTrader → cache positions qua bảng sức khoẻ (R2/R3/R4).
//
// GetLivePairTradeState, TryDetectAndHandleExternalPartialClose, watchdog, TryRefreshCloseLeg KHÔNG bị sửa: chúng chạy
// nguyên xi trên kết quả của decorator này. Rollback = revert đúng dòng đăng ký decorator (tắt luôn TRADE session vì
// vòng đời session do EnsureState ở đây điều khiển).
public sealed class CTraderAwareTradesReader : ITradesSharedMemoryReader
{
    private readonly ITradesSharedMemoryReader _inner;
    private readonly IRuntimeConfigProvider _runtimeConfigProvider;
    private readonly ICTraderTradeSession _tradeSession;
    private readonly ICTraderQuoteSession _quoteSession;

    public CTraderAwareTradesReader(
        ITradesSharedMemoryReader inner,
        IRuntimeConfigProvider runtimeConfigProvider,
        ICTraderTradeSession tradeSession,
        ICTraderQuoteSession quoteSession)
    {
        _inner = inner;
        _runtimeConfigProvider = runtimeConfigProvider;
        _tradeSession = tradeSession;
        _quoteSession = quoteSession;
    }

    public SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName)
    {
        var platformB = _runtimeConfigProvider.CurrentPlatformB;
        _tradeSession.EnsureState(platformB, _runtimeConfigProvider.CurrentCTraderFixConfig);

        if (!CTraderRoutingRules.IsCTraderTradeMap(platformB, mapName))
        {
            return _inner.ReadTrades(mapName);
        }

        return _tradeSession.ReadTrades(mapName, _quoteSession.IsQuoteLoggedOn);
    }
}
