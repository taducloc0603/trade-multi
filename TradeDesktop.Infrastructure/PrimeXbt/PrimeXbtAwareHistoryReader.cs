using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.PrimeXbt;

namespace TradeDesktop.Infrastructure.PrimeXbt;

// docs/plans/primexbt Phase 6 — decorator ngoài cùng của IHistorySharedMemoryReader, cùng khuôn PrimeXbtAwareTradesReader.
// Map khác PRIMEXBT_B_History (hoặc platform_b != primexbt) ⇒ chuyển nguyên xi xuống chuỗi cTrader → MMF. Không gọi
// EnsureState: vòng đời phiên do reader giá / decorator trades điều khiển. Rollback = gỡ đúng lớp bọc này.
public sealed class PrimeXbtAwareHistoryReader : IHistorySharedMemoryReader
{
    private readonly IHistorySharedMemoryReader _inner;
    private readonly IRuntimeConfigProvider _runtimeConfigProvider;
    private readonly IPrimeXbtTradeSession _tradeSession;

    public PrimeXbtAwareHistoryReader(
        IHistorySharedMemoryReader inner,
        IRuntimeConfigProvider runtimeConfigProvider,
        IPrimeXbtTradeSession tradeSession)
    {
        _inner = inner;
        _runtimeConfigProvider = runtimeConfigProvider;
        _tradeSession = tradeSession;
    }

    public SharedMapReadResult<HistorySharedRecord> ReadHistory(string mapName)
        => PrimeXbtRoutingRules.IsPrimeXbtHistoryMap(_runtimeConfigProvider.CurrentPlatformB, mapName)
            ? _tradeSession.ReadHistory(mapName)
            : _inner.ReadHistory(mapName);
}
