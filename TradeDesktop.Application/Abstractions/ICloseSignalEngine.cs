using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Abstractions;

public interface ICloseSignalEngine
{
    GapSignalTriggerResult? ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        TradingOpenMode openMode);

    void Reset();
}
