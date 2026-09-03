using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests.Config;

public sealed class GapThresholdConfigWarningsTests
{
    [Fact]
    public void ProductionShapedConfig_ProducesNoWarning()
    {
        // confirm < final ở cả hai cặp: gate mẫu cuối còn tác dụng.
        var warnings = GapThresholdConfigWarnings.Evaluate(
            confirmGapPts: 5,
            openPts: 8,
            closeConfirmGapPts: 50,
            closePts: 100);

        Assert.Empty(warnings);
    }

    [Fact]
    public void BothNegativeWithLooserConfirm_ProducesNoWarning()
    {
        // -12 < -8: gate mẫu cuối vẫn có tác dụng.
        var warnings = GapThresholdConfigWarnings.Evaluate(
            confirmGapPts: -12,
            openPts: -8,
            closeConfirmGapPts: -12,
            closePts: -8);

        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(8, 5)]
    [InlineData(-5, -8)]
    [InlineData(5, -8)]
    [InlineData(0, 0)]
    public void ConfirmAtLeastFinal_WarnsFinalGateIsInert(int confirm, int final)
    {
        var warnings = GapThresholdConfigWarnings.Evaluate(
            confirmGapPts: confirm,
            openPts: final,
            closeConfirmGapPts: 50,
            closePts: 100);

        var warning = Assert.Single(warnings);
        Assert.Contains("OPEN", warning, StringComparison.Ordinal);
        Assert.Contains("gate mẫu cuối", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroConfirmWithNegativeFinal_WarnsPairEqualsZeroZero()
    {
        var warnings = GapThresholdConfigWarnings.Evaluate(
            confirmGapPts: 5,
            openPts: 8,
            closeConfirmGapPts: 0,
            closePts: -8);

        var warning = Assert.Single(warnings);
        Assert.Contains("NORMAL CLOSE", warning, StringComparison.Ordinal);
        Assert.Contains("(0, 0)", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeConfirmWithZeroFinal_WarnsPairEqualsZeroZero()
    {
        var warnings = GapThresholdConfigWarnings.Evaluate(
            confirmGapPts: -8,
            openPts: 0,
            closeConfirmGapPts: 50,
            closePts: 100);

        var warning = Assert.Single(warnings);
        Assert.Contains("OPEN", warning, StringComparison.Ordinal);
        Assert.Contains("(0, 0)", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void BothPairsInvalid_ProduceTwoWarnings()
    {
        var warnings = GapThresholdConfigWarnings.Evaluate(
            confirmGapPts: 8,
            openPts: 5,
            closeConfirmGapPts: -5,
            closePts: -8);

        Assert.Equal(2, warnings.Count);
    }

    // ---- Nhóm cảnh báo hold-time của nhánh TIME ----

    [Fact]
    public void HoldConfirmOmitted_ProducesNoHoldWarning()
    {
        // Contract: caller cũ không truyền hold thì không sinh cảnh báo nhóm hold.
        var warnings = GapThresholdConfigWarnings.Evaluate(
            confirmGapPts: 5,
            openPts: 8,
            closeConfirmGapPts: 50,
            closePts: 100);

        Assert.Empty(warnings);
    }

    [Fact]
    public void PositiveHoldConfirm_ProducesNoHoldWarning()
    {
        var warnings = GapThresholdConfigWarnings.Evaluate(
            confirmGapPts: 5,
            openPts: 8,
            closeConfirmGapPts: 50,
            closePts: 100,
            holdConfirmMs: 500,
            closeHoldConfirmMs: 500);

        Assert.Empty(warnings);
    }

    [Fact]
    public void ZeroOpenHoldConfirm_WarnsCycleClosesOnMinStableSamples()
    {
        var warnings = GapThresholdConfigWarnings.Evaluate(
            confirmGapPts: 5,
            openPts: 8,
            closeConfirmGapPts: 50,
            closePts: 100,
            holdConfirmMs: 0,
            closeHoldConfirmMs: 500);

        var warning = Assert.Single(warnings);
        Assert.Contains("open_hold_confirm_ms=0", warning, StringComparison.Ordinal);
        Assert.Contains("open_gap_min_stable_samples", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroCloseHoldConfirm_WarnsTpTriggersOnFirstTick()
    {
        var warnings = GapThresholdConfigWarnings.Evaluate(
            confirmGapPts: 5,
            openPts: 8,
            closeConfirmGapPts: 50,
            closePts: 100,
            holdConfirmMs: 500,
            closeHoldConfirmMs: 0);

        var warning = Assert.Single(warnings);
        Assert.Contains("close_hold_confirm_ms=0", warning, StringComparison.Ordinal);
        Assert.Contains("close_tp_profit", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void BothHoldConfirmZero_ProduceTwoWarnings()
    {
        var warnings = GapThresholdConfigWarnings.Evaluate(
            confirmGapPts: 5,
            openPts: 8,
            closeConfirmGapPts: 50,
            closePts: 100,
            holdConfirmMs: 0,
            closeHoldConfirmMs: 0);

        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void HoldWarningsAreIndependentOfThresholdWarnings()
    {
        // Một cảnh báo ngưỡng (OPEN) + một cảnh báo hold (CLOSE) cùng xuất hiện.
        var warnings = GapThresholdConfigWarnings.Evaluate(
            confirmGapPts: 8,
            openPts: 5,
            closeConfirmGapPts: 50,
            closePts: 100,
            holdConfirmMs: 500,
            closeHoldConfirmMs: 0);

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("gate mẫu cuối", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("close_hold_confirm_ms=0", StringComparison.Ordinal));
    }
}
