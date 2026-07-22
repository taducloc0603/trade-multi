using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Abstractions;

public interface ICloseSignalEngine
{
    GapSignalTriggerResult? ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        TradingOpenMode openMode,
        double? slotProfit = null);

    // Chế độ close-gap (Normal/Target) mà lần ProcessSnapshot gần nhất đã resolve — đã tính latch.
    // Coordinator đọc để log [SLOT][GAP_MODE] khớp đúng quyết định engine.
    CloseGapMode LastResolvedGapMode { get; }

    void Reset();
}
