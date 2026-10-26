using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.TradeLocker;

namespace TradeDesktop.Infrastructure.TradeLocker;

// docs/plans/tradelocker Phase 5 — decorator NGOÀI CÙNG của ITradesSharedMemoryReader (bọc chuỗi PrimeXBT → cTrader → MMF).
// Map khác TRADELOCKER_B_Trades (hoặc platform_b != tradelocker) ⇒ chuyển nguyên xi xuống reader bên trong.
// Rollback = gỡ đúng lớp bọc này ở DependencyInjection.
public sealed class TradeLockerAwareTradesReader : ITradesSharedMemoryReader
{
    private readonly ITradesSharedMemoryReader _inner;
    private readonly IRuntimeConfigProvider _runtimeConfigProvider;
    private readonly ITradeLockerQuoteSession _quoteSession;
    private readonly ITradeLockerTradeSession _tradeSession;

    public TradeLockerAwareTradesReader(
        ITradesSharedMemoryReader inner,
        IRuntimeConfigProvider runtimeConfigProvider,
        ITradeLockerQuoteSession quoteSession,
        ITradeLockerTradeSession tradeSession)
    {
        _inner = inner;
        _runtimeConfigProvider = runtimeConfigProvider;
        _quoteSession = quoteSession;
        _tradeSession = tradeSession;
    }

    public SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName)
    {
        var platformB = _runtimeConfigProvider.CurrentPlatformB;
        if (!TradeLockerRoutingRules.IsTradeLockerTradeMap(platformB, mapName))
        {
            return _inner.ReadTrades(mapName);
        }

        // Idempotent; bình thường reader giá đã gọi mỗi 50 ms — gọi lại để polling lệnh không phụ thuộc thứ tự khởi động.
        _quoteSession.EnsureState(platformB, _runtimeConfigProvider.CurrentTradeLockerConfig);
        return _tradeSession.ReadTrades(mapName);
    }
}

// docs/plans/tradelocker Phase 6 — decorator ngoài cùng của IHistorySharedMemoryReader, cùng khuôn trên. Không gọi
// EnsureState: vòng đời phiên do reader giá / decorator trades điều khiển. Rollback = gỡ đúng lớp bọc này.
public sealed class TradeLockerAwareHistoryReader : IHistorySharedMemoryReader
{
    private readonly IHistorySharedMemoryReader _inner;
    private readonly IRuntimeConfigProvider _runtimeConfigProvider;
    private readonly ITradeLockerTradeSession _tradeSession;

    public TradeLockerAwareHistoryReader(
        IHistorySharedMemoryReader inner,
        IRuntimeConfigProvider runtimeConfigProvider,
        ITradeLockerTradeSession tradeSession)
    {
        _inner = inner;
        _runtimeConfigProvider = runtimeConfigProvider;
        _tradeSession = tradeSession;
    }

    public SharedMapReadResult<HistorySharedRecord> ReadHistory(string mapName)
        => TradeLockerRoutingRules.IsTradeLockerHistoryMap(_runtimeConfigProvider.CurrentPlatformB, mapName)
            ? _tradeSession.ReadHistory(mapName)
            : _inner.ReadHistory(mapName);
}
