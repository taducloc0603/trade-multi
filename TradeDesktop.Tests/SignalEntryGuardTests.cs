using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using Xunit;

namespace TradeDesktop.Tests;

public sealed class SignalEntryGuardTests
{
    // Tất cả check khác disabled (config 0, metrics null, holdConfirmMs 0) để cô lập TP freeze.
    private static readonly SignalEntryGuard.GuardConfig DisabledConfig =
        new(ConfirmLatencyMs: 0, MaxGap: 0, MaxSpread: 0, PointMultiplier: 1);

    private static SignalEntryGuard.GuardResult CheckTp(
        IReadOnlyList<double>? profits,
        CloseSignalReason reason = CloseSignalReason.Tp,
        int closeHoldConfirmMs = 1000)
    {
        var trigger = new GapSignalTriggerResult(
            Triggered: true,
            Action: GapSignalAction.Close,
            TriggerType: GapSignalTriggerType.CloseByGapSell,
            PrimarySide: GapSignalSide.Buy,
            BuyGaps: [],
            SellGaps: [],
            LastBuyGap: null,
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

        return SignalEntryGuard.Check(
            trigger,
            metrics: null,
            DisabledConfig,
            new Queue<SignalEntryGuard.PriceHistoryEntry>(),
            holdConfirmMs: 0,
            closeHoldConfirmMs: closeHoldConfirmMs);
    }

    [Fact]
    public void TpFreeze_AllSameValue_Rejected()
    {
        var result = CheckTp([19.00, 19.00, 19.00]);

        Assert.False(result.CanTrade);
        Assert.Contains("TP đóng băng", result.SkipReason);
        Assert.Contains("1000 ms", result.SkipReason);
    }

    [Fact]
    public void TpFreeze_DifferByLessThanRounding_Rejected()
    {
        // 18.997 và 19.003 đều làm tròn về 19.00 → coi là đóng băng.
        var result = CheckTp([18.997, 19.003]);

        Assert.False(result.CanTrade);
        Assert.Contains("19.00", result.SkipReason);
    }

    [Fact]
    public void TpFreeze_DifferAfterRounding_Allowed()
    {
        // 19.00 vs 19.02 khác nhau sau làm tròn → không freeze.
        var result = CheckTp([19.00, 19.02]);

        Assert.True(result.CanTrade);
        Assert.Null(result.SkipReason);
    }

    [Fact]
    public void TpFreeze_SingleSample_Allowed()
    {
        var result = CheckTp([19.00]);

        Assert.True(result.CanTrade);
    }

    [Fact]
    public void TpFreeze_NullProfits_Allowed()
    {
        var result = CheckTp(null);

        Assert.True(result.CanTrade);
    }

    [Fact]
    public void TpFreeze_GapCloseSameValue_Allowed()
    {
        // TP freeze chỉ áp dụng cho CloseReason.Tp — gap close không bị chặn.
        var result = CheckTp([19.00, 19.00, 19.00], reason: CloseSignalReason.Gap);

        Assert.True(result.CanTrade);
    }

    // ===== Trailing-equal freeze (freeze_last_n) =====

    private static SignalEntryGuard.GuardResult CheckOpenGap(
        IReadOnlyList<int> gaps,
        int freezeLastN,
        GapSignalSide side = GapSignalSide.Buy,
        bool sourcePricesMove = false,
        int priceFreezeMs = 0,
        int sampleSpacingMs = 1)
    {
        var triggeredAtUtc = new DateTime(2026, 6, 9, 14, 33, 15, DateTimeKind.Utc);
        var trigger = new GapSignalTriggerResult(
            Triggered: true,
            Action: GapSignalAction.Open,
            TriggerType: side == GapSignalSide.Buy ? GapSignalTriggerType.OpenByGapBuy : GapSignalTriggerType.OpenByGapSell,
            PrimarySide: side,
            BuyGaps: side == GapSignalSide.Buy ? gaps : [],
            SellGaps: side == GapSignalSide.Sell ? gaps : [],
            LastBuyGap: side == GapSignalSide.Buy && gaps.Count > 0 ? gaps[^1] : null,
            LastSellGap: side == GapSignalSide.Sell && gaps.Count > 0 ? gaps[^1] : null,
            TriggeredAtUtc: triggeredAtUtc,
            LastABid: null, LastAAsk: null, LastBBid: null, LastBAsk: null,
            GapBuySourceBBid: null, GapBuySourceAAsk: null, GapSellSourceBAsk: null, GapSellSourceABid: null,
            PointMultiplier: 1);

        var config = new SignalEntryGuard.GuardConfig(
            ConfirmLatencyMs: 0, MaxGap: 0, MaxSpread: 0, PointMultiplier: 1, FreezeLastN: freezeLastN);

        var history = new Queue<SignalEntryGuard.PriceHistoryEntry>();
        for (var i = 0; i < Math.Max(0, freezeLastN); i++)
        {
            var movingOffset = sourcePricesMove ? i : 0;
            history.Enqueue(new SignalEntryGuard.PriceHistoryEntry(
                triggeredAtUtc.AddMilliseconds((i - freezeLastN + 1) * sampleSpacingMs),
                BidA: 100 + movingOffset,
                AskA: 101 + movingOffset,
                BidB: 102 + movingOffset,
                AskB: 103 + movingOffset));
        }

        return SignalEntryGuard.Check(
            trigger, metrics: null, config,
            history,
            holdConfirmMs: priceFreezeMs, closeHoldConfirmMs: 0);
    }

    private static SignalEntryGuard.GuardResult CheckTpTrailing(IReadOnlyList<double> profits, int freezeLastN)
    {
        var trigger = new GapSignalTriggerResult(
            Triggered: true, Action: GapSignalAction.Close,
            TriggerType: GapSignalTriggerType.CloseByGapSell, PrimarySide: GapSignalSide.Buy,
            BuyGaps: [], SellGaps: [], LastBuyGap: null, LastSellGap: null,
            TriggeredAtUtc: new DateTime(2026, 6, 9, 14, 33, 15, DateTimeKind.Utc),
            LastABid: null, LastAAsk: null, LastBBid: null, LastBAsk: null,
            GapBuySourceBBid: null, GapBuySourceAAsk: null, GapSellSourceBAsk: null, GapSellSourceABid: null,
            PointMultiplier: 1, CloseReason: CloseSignalReason.Tp, CloseTpProfits: profits);

        var config = new SignalEntryGuard.GuardConfig(
            ConfirmLatencyMs: 0, MaxGap: 0, MaxSpread: 0, PointMultiplier: 1, FreezeLastN: freezeLastN);

        return SignalEntryGuard.Check(
            trigger, metrics: null, config,
            new Queue<SignalEntryGuard.PriceHistoryEntry>(),
            holdConfirmMs: 0, closeHoldConfirmMs: 0);
    }

    [Fact]
    public void OpenTrailing_Last3Equal_Rejected()
    {
        // Ví dụ user: [1,3,4,5,5,5], N=3 → 3 phần tử cuối [5,5,5] bằng nhau → skip.
        var result = CheckOpenGap([1, 3, 4, 5, 5, 5], freezeLastN: 3);

        Assert.False(result.CanTrade);
        Assert.Contains("Gap và giá nguồn 3 mẫu cuối cùng đóng băng", result.SkipReason);
    }

    [Fact]
    public void OpenTrailing_Last4NotEqual_Allowed()
    {
        // Ví dụ user: [1,3,4,5,5,5], N=4 → [4,5,5,5] không bằng nhau → cho phép.
        var result = CheckOpenGap([1, 3, 4, 5, 5, 5], freezeLastN: 4);

        Assert.True(result.CanTrade);
    }

    [Fact]
    public void OpenTrailing_NLessThan2_Disabled()
    {
        Assert.True(CheckOpenGap([5, 5, 5], freezeLastN: 1).CanTrade);
        Assert.True(CheckOpenGap([5, 5, 5], freezeLastN: 0).CanTrade);
    }

    [Fact]
    public void OpenTrailing_FewerSamplesThanN_Allowed()
    {
        var result = CheckOpenGap([5, 5], freezeLastN: 3);

        Assert.True(result.CanTrade);
    }

    [Fact]
    public void OpenTrailing_SellSide_UsesSellGaps_Rejected()
    {
        var result = CheckOpenGap([1, -3, -5, -5, -5], freezeLastN: 3, side: GapSignalSide.Sell);

        Assert.False(result.CanTrade);
    }

    [Fact]
    public void OpenTrailing_EqualRoundedGapsButSourcePricesMove_Allowed()
    {
        var result = CheckOpenGap([5, 5, 5], freezeLastN: 3, sourcePricesMove: true);

        Assert.True(result.CanTrade);
    }

    [Fact]
    public void OpenTrailing_FrozenSamplesButInsufficientObservedTime_Allowed()
    {
        var result = CheckOpenGap(
            [5, 5, 5],
            freezeLastN: 3,
            priceFreezeMs: 1000,
            sampleSpacingMs: 100);

        Assert.True(result.CanTrade);
    }

    [Theory]
    [InlineData(GapSignalTriggerType.OpenByGapBuy, 12)]
    [InlineData(GapSignalTriggerType.CloseByGapBuy, 12)]
    [InlineData(GapSignalTriggerType.OpenByGapSell, -7)]
    [InlineData(GapSignalTriggerType.CloseByGapSell, -7)]
    public void ResolveTriggerGap_UsesGapMatchingTriggerType(
        GapSignalTriggerType triggerType,
        int expected)
    {
        var trigger = new GapSignalTriggerResult(
            Triggered: true,
            Action: triggerType is GapSignalTriggerType.OpenByGapBuy or GapSignalTriggerType.OpenByGapSell
                ? GapSignalAction.Open
                : GapSignalAction.Close,
            TriggerType: triggerType,
            PrimarySide: triggerType is GapSignalTriggerType.OpenByGapBuy or GapSignalTriggerType.CloseByGapBuy
                ? GapSignalSide.Buy
                : GapSignalSide.Sell,
            BuyGaps: [12], SellGaps: [-7], LastBuyGap: 12, LastSellGap: -7,
            TriggeredAtUtc: DateTime.UtcNow,
            LastABid: null, LastAAsk: null, LastBBid: null, LastBAsk: null,
            GapBuySourceBBid: null, GapBuySourceAAsk: null,
            GapSellSourceBAsk: null, GapSellSourceABid: null,
            PointMultiplier: 1);

        Assert.Equal(expected, SignalEntryGuard.ResolveTriggerGap(trigger));
    }

    [Fact]
    public void PriceFreeze_InsufficientObservedDuration_Allowed()
    {
        var result = CheckPriceFreeze(
            holdMs: 1000,
            entries:
            [
                (0, 100m, 101m, 200m, 201m),
                (200, 100m, 101m, 200m, 201m),
                (500, 100m, 101m, 200m, 201m)
            ]);

        Assert.True(result.CanTrade);
    }

    [Fact]
    public void PriceFreeze_OnlyBidIsConstant_Allowed()
    {
        var result = CheckPriceFreeze(
            holdMs: 1000,
            entries:
            [
                (0, 100m, 101m, 200m, 201m),
                (500, 100m, 102m, 200m, 202m),
                (1000, 100m, 103m, 200m, 203m)
            ]);

        Assert.True(result.CanTrade);
    }

    [Fact]
    public void PriceFreeze_BidAndAskOfOneExchangeConstantForFullWindow_Rejected()
    {
        var result = CheckPriceFreeze(
            holdMs: 1000,
            entries:
            [
                (0, 100m, 101m, 200m, 201m),
                (500, 100m, 101m, 201m, 202m),
                (1000, 100m, 101m, 202m, 203m)
            ]);

        Assert.False(result.CanTrade);
        Assert.Contains("sàn A đóng băng", result.SkipReason);
        Assert.Contains("observedMs=1000", result.SkipReason);
        Assert.Contains("ticks=3", result.SkipReason);
    }

    [Fact]
    public void PriceFreeze_SosClose_BypassesGuard()
    {
        var result = CheckPriceFreeze(
            holdMs: 1000,
            closeGapMode: CloseGapMode.Sos,
            entries:
            [
                (0, 100m, 101m, 200m, 201m),
                (500, 100m, 101m, 200m, 201m),
                (1000, 100m, 101m, 200m, 201m)
            ]);

        Assert.True(result.CanTrade);
    }

    private static SignalEntryGuard.GuardResult CheckPriceFreeze(
        int holdMs,
        IReadOnlyList<(int OffsetMs, decimal BidA, decimal AskA, decimal BidB, decimal AskB)> entries,
        CloseGapMode closeGapMode = CloseGapMode.Normal)
    {
        var startedAt = new DateTime(2026, 6, 9, 14, 33, 15, DateTimeKind.Utc);
        var triggeredAt = startedAt.AddMilliseconds(entries.Max(entry => entry.OffsetMs));
        var action = closeGapMode == CloseGapMode.Sos ? GapSignalAction.Close : GapSignalAction.Open;
        var trigger = new GapSignalTriggerResult(
            Triggered: true,
            Action: action,
            TriggerType: action == GapSignalAction.Open
                ? GapSignalTriggerType.OpenByGapBuy
                : GapSignalTriggerType.CloseByGapSell,
            PrimarySide: GapSignalSide.Buy,
            BuyGaps: [], SellGaps: [], LastBuyGap: null, LastSellGap: null,
            TriggeredAtUtc: triggeredAt,
            LastABid: null, LastAAsk: null, LastBBid: null, LastBAsk: null,
            GapBuySourceBBid: null, GapBuySourceAAsk: null,
            GapSellSourceBAsk: null, GapSellSourceABid: null,
            PointMultiplier: 1,
            CloseGapMode: closeGapMode);
        var history = new Queue<SignalEntryGuard.PriceHistoryEntry>(entries.Select(entry =>
            new SignalEntryGuard.PriceHistoryEntry(
                startedAt.AddMilliseconds(entry.OffsetMs),
                entry.BidA,
                entry.AskA,
                entry.BidB,
                entry.AskB)));

        return SignalEntryGuard.Check(
            trigger,
            metrics: null,
            DisabledConfig,
            history,
            holdConfirmMs: holdMs,
            closeHoldConfirmMs: 0);
    }

    [Fact]
    public void TpTrailing_Last3Equal_Rejected()
    {
        var result = CheckTpTrailing([4.00, 4.50, 5.00, 5.00, 5.00], freezeLastN: 3);

        Assert.False(result.CanTrade);
        Assert.Contains("TP 3 mẫu cuối bằng nhau", result.SkipReason);
    }

    [Fact]
    public void TpTrailing_Last4NotEqual_Allowed()
    {
        var result = CheckTpTrailing([4.00, 4.50, 5.00, 5.00, 5.00], freezeLastN: 4);

        Assert.True(result.CanTrade);
    }

    [Fact]
    public void OpenTrailing_DoesNotApplyToTpTrigger_And_ViceVersa()
    {
        // Open trigger + FreezeLastN nhưng CheckTpTrailing self-gate (Action=Open) → không chặn nhầm.
        // Chuỗi gap không có N cuối bằng nhau → cho phép.
        Assert.True(CheckOpenGap([1, 2, 3], freezeLastN: 3).CanTrade);
    }
}
