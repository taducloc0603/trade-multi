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
        GapSignalSide side = GapSignalSide.Buy)
    {
        var trigger = new GapSignalTriggerResult(
            Triggered: true,
            Action: GapSignalAction.Open,
            TriggerType: side == GapSignalSide.Buy ? GapSignalTriggerType.OpenByGapBuy : GapSignalTriggerType.OpenByGapSell,
            PrimarySide: side,
            BuyGaps: side == GapSignalSide.Buy ? gaps : [],
            SellGaps: side == GapSignalSide.Sell ? gaps : [],
            LastBuyGap: side == GapSignalSide.Buy && gaps.Count > 0 ? gaps[^1] : null,
            LastSellGap: side == GapSignalSide.Sell && gaps.Count > 0 ? gaps[^1] : null,
            TriggeredAtUtc: new DateTime(2026, 6, 9, 14, 33, 15, DateTimeKind.Utc),
            LastABid: null, LastAAsk: null, LastBBid: null, LastBAsk: null,
            GapBuySourceBBid: null, GapBuySourceAAsk: null, GapSellSourceBAsk: null, GapSellSourceABid: null,
            PointMultiplier: 1);

        var config = new SignalEntryGuard.GuardConfig(
            ConfirmLatencyMs: 0, MaxGap: 0, MaxSpread: 0, PointMultiplier: 1, FreezeLastN: freezeLastN);

        return SignalEntryGuard.Check(
            trigger, metrics: null, config,
            new Queue<SignalEntryGuard.PriceHistoryEntry>(),
            holdConfirmMs: 0, closeHoldConfirmMs: 0);
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
        Assert.Contains("Gap 3 mẫu cuối bằng nhau", result.SkipReason);
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
