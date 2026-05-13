using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Abstractions;

public interface ITradeSignalLogBuilder
{
    IReadOnlyList<string> BuildLogLines(TradeSignalInstruction instruction);
}
