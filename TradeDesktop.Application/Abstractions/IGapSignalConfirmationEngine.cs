using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Abstractions;

public interface IGapSignalConfirmationEngine
{
    IReadOnlyList<GapSignalTriggerResult> ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config);

    void Reset();
}