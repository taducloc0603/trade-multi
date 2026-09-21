using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.CTrader;

namespace TradeDesktop.Tests.Config;

public sealed class CTraderPhase2RulesTests
{
    private sealed class AlwaysExistsProbe : IWindowProbe
    {
        public bool WindowExists(ulong hwnd) => true;
    }

    private static readonly ManualHwndColumnConfig MissingB = new("0x10", "0x20", string.Empty, string.Empty);
    private static readonly ManualHwndColumnConfig Full = new("0x10", "0x20", "0x30", "0x40");

    // Ma trận platform Phase 2: HWND B chỉ bắt buộc khi B là MT.
    [Theory]
    [InlineData("mt4", "mt4", false)]
    [InlineData("mt4", "mt5", false)]
    [InlineData("mt5", "mt4", false)]
    [InlineData("mt5", "mt5", false)]
    [InlineData("mt4", "ctrader", true)]
    [InlineData("mt5", "ctrader", true)]
    public void IsCompleteFor_PlatformMatrix(string platformA, string platformB, bool expectedWhenBMissing)
    {
        _ = platformA;
        var requiresB = !CTraderRoutingRules.IsCTraderPlatform(platformB);

        Assert.Equal(expectedWhenBMissing, MissingB.IsCompleteFor(requiresB));
        Assert.True(Full.IsCompleteFor(requiresB));
    }

    [Fact]
    public void IsComplete_LegacyPropertyUnchanged()
    {
        Assert.False(MissingB.IsComplete);
        Assert.True(Full.IsComplete);
        Assert.False(new ManualHwndColumnConfig(string.Empty, "0x20", "0x30", "0x40").IsCompleteFor(false));
    }

    [Fact]
    public void HwndHealthChecker_DefaultStillRequiresB()
    {
        var checker = new HwndHealthChecker(new AlwaysExistsProbe());

        var issues = checker.Check([MissingB]);

        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(HwndIssueKind.Empty, i.Kind));
        Assert.Contains(issues, i => i.Label == "Cột 1 - Chart B");
        Assert.Contains(issues, i => i.Label == "Cột 1 - Trade B");
    }

    [Fact]
    public void HwndHealthChecker_CTraderSkipsBOnly()
    {
        var checker = new HwndHealthChecker(new AlwaysExistsProbe());

        Assert.Empty(checker.Check([MissingB], requiresExchangeBHwnd: false));

        var issues = checker.Check([new ManualHwndColumnConfig("bad", string.Empty, "junk", "junk")], requiresExchangeBHwnd: false);
        Assert.Equal(2, issues.Count);
        Assert.DoesNotContain(issues, i => i.Label.Contains(" B", StringComparison.Ordinal));
    }

    [Fact]
    public void HwndHealthChecker_CTraderColumnWithOnlyStaleBValues_IsSkippedAsEmpty()
    {
        var checker = new HwndHealthChecker(new AlwaysExistsProbe());

        Assert.Empty(checker.Check([new ManualHwndColumnConfig(string.Empty, string.Empty, "0x30", "0x40")], requiresExchangeBHwnd: false));
        Assert.Equal(2, checker.Check([new ManualHwndColumnConfig(string.Empty, string.Empty, "0x30", "0x40")]).Count);
    }

    [Theory]
    [InlineData("ctrader", "CTRADER_B_Trades", true)]
    [InlineData("CTRADER", "CTRADER_B_Trades", true)]
    [InlineData("ctrader", "Local\\MT_B_Trades", false)]
    [InlineData("ctrader", "Local\\MT_A_Trades", false)]
    [InlineData("ctrader", "CTRADER_B_History", false)]
    [InlineData("mt5", "CTRADER_B_Trades", false)]
    [InlineData("ctrader", null, false)]
    [InlineData("ctrader", "", false)]
    public void RoutingRules_TradeMap(string platformB, string? mapName, bool expected)
    {
        Assert.Equal(expected, CTraderRoutingRules.IsCTraderTradeMap(platformB, mapName));
    }

    [Theory]
    [InlineData("ctrader", "CTRADER_B_History", true)]
    [InlineData("ctrader", "CTRADER_B_Trades", false)]
    [InlineData("mt4", "CTRADER_B_History", false)]
    [InlineData("ctrader", "Local\\MT_B_History", false)]
    public void RoutingRules_HistoryMap(string platformB, string mapName, bool expected)
    {
        Assert.Equal(expected, CTraderRoutingRules.IsCTraderHistoryMap(platformB, mapName));
    }

    [Fact]
    public void RoutingRules_MapNamesFollowOrderMapNameResolverSuffixes()
    {
        Assert.Equal("CTRADER_B_Trades", CTraderRoutingRules.TradeMapName);
        Assert.Equal("CTRADER_B_History", CTraderRoutingRules.HistoryMapName);
    }

    [Theory]
    [InlineData(100, 2, true, 100)]
    [InlineData(1000, 3, true, 1000)]
    [InlineData(1, 0, true, 1)]
    [InlineData(10, 2, false, 100)]
    [InlineData(100, 5, false, 100000)]
    public void PointDigits_ConsistencyAndExpectedPoint(int point, int digits, bool consistent, int expectedPoint)
    {
        var result = PointDigitsConsistencyChecker.Check(point, digits);

        Assert.Equal(consistent, result.IsConsistent);
        Assert.Equal(expectedPoint, result.ExpectedPoint);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-100, 2)]
    [InlineData(100, -1)]
    [InlineData(100, 10)]
    public void PointDigits_InvalidInputs_AreInconsistentWithoutExpectedPoint(int point, int digits)
    {
        var result = PointDigitsConsistencyChecker.Check(point, digits);

        Assert.False(result.IsConsistent);
        Assert.Null(result.ExpectedPoint);
    }

    [Theory]
    [InlineData(0.01, 1, 100, HedgeVolumeLevel.Ok)]
    [InlineData(0.01, 1.04, 100, HedgeVolumeLevel.Ok)]
    [InlineData(0.01, 0.9, 100, HedgeVolumeLevel.Warn)]
    [InlineData(0.01, 1.15, 100, HedgeVolumeLevel.Warn)]
    [InlineData(0.01, 2, 100, HedgeVolumeLevel.Alert)]
    [InlineData(0.01, 0.5, 100, HedgeVolumeLevel.Alert)]
    public void HedgeVolume_Levels(double lotsA, double unitsB, double contractB, HedgeVolumeLevel expected)
    {
        Assert.Equal(expected, HedgeVolumeConsistencyChecker.Check(lotsA, unitsB, contractB).Level);
    }

    [Fact]
    public void HedgeVolume_RatioOneForFxProDefaults()
    {
        var result = HedgeVolumeConsistencyChecker.Check(0.01, 1, 100);

        Assert.NotNull(result.Ratio);
        Assert.Equal(1.0, result.Ratio!.Value, 6);
    }

    [Theory]
    [InlineData(0.01, 1, 0)]
    [InlineData(0.01, 1, -100)]
    [InlineData(0, 1, 100)]
    [InlineData(0.01, 0, 100)]
    [InlineData(double.NaN, 1, 100)]
    [InlineData(0.01, double.PositiveInfinity, 100)]
    [InlineData(0.01, 1, double.NaN)]
    public void HedgeVolume_InvalidInputs_NoDivideByZero(double lotsA, double unitsB, double contractB)
    {
        var result = HedgeVolumeConsistencyChecker.Check(lotsA, unitsB, contractB);

        Assert.Equal(HedgeVolumeLevel.Invalid, result.Level);
        Assert.Null(result.Ratio);
    }
}
