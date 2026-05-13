using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Abstractions;

public interface IOpenSignalEngine
{
    IReadOnlyList<GapSignalTriggerResult> ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config);

    void Reset();
}
