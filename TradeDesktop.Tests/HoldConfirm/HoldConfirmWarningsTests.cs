using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests.HoldConfirm;

public sealed class HoldConfirmWarningsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(500, 0)]
    [InlineData(0, 500)]
    [InlineData(500, 500)]
    public void MsMode_IsIdenticalToLegacyOverload(int openMs, int closeMs)
    {
        var legacy = GapThresholdConfigWarnings.Evaluate(30, 40, 20, 25, openMs, closeMs);
        var explicitMs = GapThresholdConfigWarnings.Evaluate(30, 40, 20, 25, openMs, closeMs, HoldConfirmMode.Ms, 0, 0);

        Assert.Equal(legacy, explicitMs);
    }

    [Fact]
    public void TicksMode_ZeroTicks_WarnsBothSides()
    {
        var warnings = GapThresholdConfigWarnings.Evaluate(40, 40, 25, 25, 500, 500, HoldConfirmMode.Ticks, 0, 0);

        Assert.Contains(warnings, w => w.Contains("open_hold_confirm_ticks=0", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("close_hold_confirm_ticks=0", StringComparison.Ordinal));
    }

    [Fact]
    public void TicksMode_ConfirmDiffersFromFinal_Warns()
    {
        var warnings = GapThresholdConfigWarnings.Evaluate(30, 40, 20, 25, 500, 500, HoldConfirmMode.Ticks, 1, 1);

        Assert.Contains(warnings, w => w.Contains("confirm_gap_pts=30 khác open_pts=40", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("close_confirm_gap_pts=20 khác close_pts=25", StringComparison.Ordinal));
    }

    [Fact]
    public void TicksMode_RecommendedConfig_HasNoHoldWarnings()
    {
        var warnings = GapThresholdConfigWarnings.Evaluate(40, 40, 25, 25, 0, 500, HoldConfirmMode.Ticks, 1, 1);

        // open_hold_confirm_ms=0 không còn ý nghĩa ở mode ticks nên không cảnh báo.
        Assert.Empty(warnings);
    }

    [Fact]
    public void TicksMode_ConfirmAboveFinal_StillGetsLegacyPairWarning()
    {
        var warnings = GapThresholdConfigWarnings.Evaluate(50, 40, 25, 25, 500, 500, HoldConfirmMode.Ticks, 1, 1);

        Assert.Contains(warnings, w => w.Contains("confirm_gap_pts=50 >= open_pts=40", StringComparison.Ordinal));
    }

    [Fact]
    public void MsMode_ConfirmEqualsFinal_StillWarns()
    {
        var warnings = GapThresholdConfigWarnings.Evaluate(40, 40, 25, 25, 500, 500);

        Assert.Contains(warnings, w => w.Contains("confirm_gap_pts=40 >= open_pts=40", StringComparison.Ordinal));
    }

    [Fact]
    public void TicksMode_CloseMsZero_WarnsOnlyAboutTp()
    {
        var warnings = GapThresholdConfigWarnings.Evaluate(40, 40, 25, 25, 500, 0, HoldConfirmMode.Ticks, 1, 1);

        var warning = Assert.Single(warnings);
        Assert.Contains("chỉ còn dùng cho TP", warning, StringComparison.Ordinal);
    }
}
