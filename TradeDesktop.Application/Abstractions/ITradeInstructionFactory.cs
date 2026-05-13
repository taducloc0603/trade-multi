using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Abstractions;

public interface ITradeInstructionFactory
{
    TradeSignalInstruction Create(GapSignalTriggerResult triggerResult);
}
