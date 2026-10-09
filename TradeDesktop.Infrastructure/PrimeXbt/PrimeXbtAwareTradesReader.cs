using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.PrimeXbt;

namespace TradeDesktop.Infrastructure.PrimeXbt;

// docs/plans/primexbt Phase 5 — decorator ngoài cùng của ITradesSharedMemoryReader. Map khác PRIMEXBT_B_Trades (hoặc
// platform_b != primexbt) ⇒ chuyển nguyên xi xuống reader bên trong (chuỗi cTrader → MMF giữ nguyên). Rollback = gỡ
// đúng lớp bọc này ở DependencyInjection.
public sealed class PrimeXbtAwareTradesReader : ITradesSharedMemoryReader
{
    private readonly ITradesSharedMemoryReader _inner;
    private readonly IRuntimeConfigProvider _runtimeConfigProvider;
    private readonly IPrimeXbtQuoteSession _quoteSession;
    private readonly IPrimeXbtTradeSession _tradeSession;

    public PrimeXbtAwareTradesReader(
        ITradesSharedMemoryReader inner,
        IRuntimeConfigProvider runtimeConfigProvider,
        IPrimeXbtQuoteSession quoteSession,
        IPrimeXbtTradeSession tradeSession)
    {
        _inner = inner;
        _runtimeConfigProvider = runtimeConfigProvider;
        _quoteSession = quoteSession;
        _tradeSession = tradeSession;
    }

    public SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName)
    {
        var platformB = _runtimeConfigProvider.CurrentPlatformB;
        if (!PrimeXbtRoutingRules.IsPrimeXbtTradeMap(platformB, mapName))
        {
            return _inner.ReadTrades(mapName);
        }

        // Idempotent; bình thường reader giá đã gọi mỗi 50 ms — gọi lại để polling lệnh không phụ thuộc thứ tự khởi động.
        _quoteSession.EnsureState(platformB, _runtimeConfigProvider.CurrentPrimeXbtConfig);
        return _tradeSession.ReadTrades(mapName);
    }
}
