using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using Xunit;

namespace TradeDesktop.Tests;

public sealed class SignalEntryGuardTests
{
    private static readonly SignalEntryGuard.GuardConfig DisabledConfig =
        new(ConfirmLatencyMs: 0, MaxGap: 0, MaxSpread: 0, PointMultiplier: 1);

    [Fact]
    public void EqualOpenGapSamples_AreAllowed()
    {
        var result = Check(CreateTrigger(
            GapSignalAction.Open,
            CloseSignalReason.Gap,
            buyGaps: [5, 5, 5],
            profits: null));

        Assert.True(result.CanTrade);
        Assert.Null(result.SkipReason);
    }

    [Fact]
    public void EqualTpProfitSamples_AreAllowed()
    {
        var result = Check(CreateTrigger(
            GapSignalAction.Close,
            CloseSignalReason.Tp,
            buyGaps: [],
            profits: [19.00, 19.00, 19.00]));

        Assert.True(result.CanTrade);
        Assert.Null(result.SkipReason);
    }

    private static SignalEntryGuard.GuardResult Check(GapSignalTriggerResult trigger) =>
        SignalEntryGuard.Check(
            trigger,
            metrics: null,
            DisabledConfig,
            new Queue<SignalEntryGuard.PriceHistoryEntry>(),
            priceFreezeMs: 0);

    private static GapSignalTriggerResult CreateTrigger(
        GapSignalAction action,
        CloseSignalReason reason,
        IReadOnlyList<int> buyGaps,
        IReadOnlyList<double>? profits) =>
        new(
            Triggered: true,
            Action: action,
            TriggerType: action == GapSignalAction.Open
                ? GapSignalTriggerType.OpenByGapBuy
                : GapSignalTriggerType.CloseByGapSell,
            PrimarySide: GapSignalSide.Buy,
            BuyGaps: buyGaps,
            SellGaps: [],
            LastBuyGap: buyGaps.Count > 0 ? buyGaps[^1] : null,
            LastSellGap: null,
            TriggeredAtUtc: new DateTime(2026, 6, 9, 14, 33, 15, DateTimeKind.Utc),
            LastABid: null,
            LastAAsk: null,
            LastBBid: null,
            LastBAsk: null,
            GapBuySourceBBid: null,
            GapBuySourceAAsk: null,
            GapSellSourceBAsk: null,
            GapSellSourceABid: null,
            PointMultiplier: 1,
            CloseReason: reason,
            CloseTpProfits: profits);
}
