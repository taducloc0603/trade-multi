using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class TradeSignalLogBuilderTests
{
    private readonly TradeInstructionFactory _instructionFactory = new();
    private readonly TradeSignalLogBuilder _logBuilder = new();

    [Fact]
    public void BuildLogLines_OpenBuy_PrintsAThenBWithCorrectSidesAndPrices()
    {
        var trigger = BuildTrigger(GapSignalAction.Open, GapSignalSide.Buy);

        var lines = _logBuilder.BuildLogLines(_instructionFactory.Create(trigger));

        Assert.Equal(4, lines.Count);
        Assert.Equal("[OPEN BY GAP_BUY] GAP 11 (29|2|2|20|29|11)", lines[0]);
        Assert.Equal("    = (B.Bid 4555.28 - A.Ask 4555.67) * Point(100)", lines[1]);
        Assert.Equal("    - [13:00:43.573] OPEN BUY A at Price: 4555.67", lines[2]);
        Assert.Equal("    - [13:00:43.573] OPEN SELL B at Price: 4555.28", lines[3]);
    }

    [Fact]
    public void BuildLogLines_OpenSell_PrintsAThenBWithCorrectSidesAndPrices()
    {
        var trigger = BuildTrigger(GapSignalAction.Open, GapSignalSide.Sell);

        var lines = _logBuilder.BuildLogLines(_instructionFactory.Create(trigger));

        Assert.Equal(4, lines.Count);
        Assert.Equal("[OPEN BY GAP_SELL] GAP -22 (-26|-15|-19|-22|-22|-22)", lines[0]);
        Assert.Equal("    = (B.Ask 4555.56 - A.Bid 4555.42) * Point(100)", lines[1]);
        Assert.Equal("    - [12:55:16.097] OPEN SELL A at Price: 4555.42", lines[2]);
        Assert.Equal("    - [12:55:16.097] OPEN BUY B at Price: 4555.56", lines[3]);
    }

    [Fact]
    public void BuildLogLines_CloseBuy_PrintsAThenBWithCorrectSidesAndPrices()
    {
        var trigger = BuildTrigger(GapSignalAction.Close, GapSignalSide.Buy);

        var lines = _logBuilder.BuildLogLines(_instructionFactory.Create(trigger));

        Assert.Equal(4, lines.Count);
        Assert.Equal("[CLOSE BY GAP_SELL] GAP -22 (-26|-15|-19|-22|-22|-22)", lines[0]);
        Assert.Equal("    = (B.Ask 4555.56 - A.Bid 4555.42) * Point(100)", lines[1]);
        Assert.Equal("    - [12:57:43.347] CLOSE BUY A at Price: 4555.42", lines[2]);
        Assert.Equal("    - [12:57:43.347] CLOSE SELL B at Price: 4555.56", lines[3]);
    }

    [Fact]
    public void BuildLogLines_CloseSell_PrintsAThenBWithCorrectSidesAndPrices()
    {
        var trigger = BuildTrigger(GapSignalAction.Close, GapSignalSide.Sell);

        var lines = _logBuilder.BuildLogLines(_instructionFactory.Create(trigger));

        Assert.Equal(4, lines.Count);
        Assert.Equal("[CLOSE BY GAP_BUY] GAP 11 (29|2|2|20|29|11)", lines[0]);
        Assert.Equal("    = (B.Bid 4555.28 - A.Ask 4555.67) * Point(100)", lines[1]);
        Assert.Equal("    - [12:53:15.737] CLOSE SELL A at Price: 4555.67", lines[2]);
        Assert.Equal("    - [12:53:15.737] CLOSE BUY B at Price: 4555.28", lines[3]);
    }

    private static GapSignalTriggerResult BuildTrigger(GapSignalAction action, GapSignalSide primarySide)
    {
        var triggeredAtUtc = new DateTime(2026, 3, 20, 6, 0, 43, 573, DateTimeKind.Utc);
        if (action == GapSignalAction.Open && primarySide == GapSignalSide.Sell)
        {
            triggeredAtUtc = new DateTime(2026, 3, 20, 5, 55, 16, 97, DateTimeKind.Utc);
        }
        else if (action == GapSignalAction.Close && primarySide == GapSignalSide.Buy)
        {
            triggeredAtUtc = new DateTime(2026, 3, 20, 5, 57, 43, 347, DateTimeKind.Utc);
        }
        else if (action == GapSignalAction.Close && primarySide == GapSignalSide.Sell)
        {
            triggeredAtUtc = new DateTime(2026, 3, 20, 5, 53, 15, 737, DateTimeKind.Utc);
        }

        var triggerType = action switch
        {
            GapSignalAction.Open when primarySide == GapSignalSide.Buy => GapSignalTriggerType.OpenByGapBuy,
            GapSignalAction.Open => GapSignalTriggerType.OpenByGapSell,
            GapSignalAction.Close when primarySide == GapSignalSide.Buy => GapSignalTriggerType.CloseByGapSell,
            _ => GapSignalTriggerType.CloseByGapBuy
        };

        return new GapSignalTriggerResult(
            Triggered: true,
            Action: action,
            TriggerType: triggerType,
            PrimarySide: primarySide,
            BuyGaps: new[] { 29, 2, 2, 20, 29, 11 },
            SellGaps: new[] { -26, -15, -19, -22, -22, -22 },
            LastBuyGap: 11,
            LastSellGap: -22,
            TriggeredAtUtc: triggeredAtUtc,
            LastABid: 4555.42m,
            LastAAsk: 4555.67m,
            LastBBid: 4555.28m,
            LastBAsk: 4555.56m,
            GapBuySourceBBid: 4555.28m,
            GapBuySourceAAsk: 4555.67m,
            GapSellSourceBAsk: 4555.56m,
            GapSellSourceABid: 4555.42m,
            PointMultiplier: 100);
    }
}
