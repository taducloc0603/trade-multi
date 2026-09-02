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
}
