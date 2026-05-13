using System.Globalization;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public sealed class TradeSignalLogBuilder : ITradeSignalLogBuilder
{
    public IReadOnlyList<string> BuildLogLines(TradeSignalInstruction instruction)
    {
        var triggeredAtLocal = instruction.TriggeredAtUtc.ToLocalTime();
        var header = BuildHeaderLine(instruction);
        var explain = BuildExplainLine(instruction);
        return
        [
            header,
            explain,
            BuildLegLine(triggeredAtLocal, instruction.ExchangeA),
            BuildLegLine(triggeredAtLocal, instruction.ExchangeB)
        ];
    }

    private static string BuildHeaderLine(TradeSignalInstruction instruction)
    {
        var triggerText = instruction.TriggerType switch
        {
            GapSignalTriggerType.OpenByGapBuy => "OPEN BY GAP_BUY",
            GapSignalTriggerType.OpenByGapSell => "OPEN BY GAP_SELL",
            GapSignalTriggerType.CloseByGapBuy => "CLOSE BY GAP_BUY",
            GapSignalTriggerType.CloseByGapSell => "CLOSE BY GAP_SELL",
            _ => "SIGNAL"
        };

        var lastGapText = instruction.LastTriggerGap?.ToString(CultureInfo.InvariantCulture) ?? "0";
        var allGaps = string.Join("|", instruction.TriggerGaps);
        return $"[{triggerText}] GAP {lastGapText} ({allGaps})";
    }

    private static string BuildExplainLine(TradeSignalInstruction instruction)
    {
        var left = instruction.TriggerLeftPrice.HasValue
            ? instruction.TriggerLeftPrice.Value.ToString("0.#####", CultureInfo.InvariantCulture)
            : "-";
        var right = instruction.TriggerRightPrice.HasValue
            ? instruction.TriggerRightPrice.Value.ToString("0.#####", CultureInfo.InvariantCulture)
            : "-";

        return $"    = ({instruction.TriggerLeftLabel} {left} - {instruction.TriggerRightLabel} {right}) * Point({instruction.PointMultiplier})";
    }

    private static string BuildLegLine(DateTime triggeredAtLocal, TradeInstructionLeg leg)
    {
        var actionText = leg.Action == GapSignalAction.Open ? "OPEN" : "CLOSE";
        var sideText = leg.Side == GapSignalSide.Buy ? "BUY" : "SELL";
        var priceText = leg.Price.HasValue
            ? leg.Price.Value.ToString("0.#####", CultureInfo.InvariantCulture)
            : "-";

        return $"    - [{triggeredAtLocal:HH:mm:ss.fff}] {actionText} {sideText} {leg.Exchange} at Price: {priceText}";
    }
}
