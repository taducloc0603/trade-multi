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
}
