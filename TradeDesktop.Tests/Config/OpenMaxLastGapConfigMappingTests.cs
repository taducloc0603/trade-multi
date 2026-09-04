using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests.Config;

// open_max_last_gap_pts là trần cho GAP CUỐI của Open Cycle.
// NULL (hoặc DB chưa có cột) = tắt gate; 0 và số âm là giá trị HỢP LỆ và KHÔNG bị clamp,
// giống 4 cột ngưỡng gap thường trên nhánh này. Các test dưới khoá điều đó.
public sealed class OpenMaxLastGapConfigMappingTests
{
    private static ConfigLoadResult Build(int? openMaxLastGapPts)
        => ConfigLoadResult.Success(
            machineHostName: "host",
            mapName1: "A",
            mapName2: "B",
            manualHwndColumns: null,
            platformA: "mt5",
            platformB: "mt5",
            point: 100,
            openPts: 1,
            confirmGapPts: 0,
            holdConfirmMs: 0,
            openPriceFreezeMs: 0,
            closePts: 1,
            closeConfirmGapPts: 0,
            closeTpProfit: 1,
            closeConfirmTpProfit: 0,
            closeMaxTpProfit: 35,
            closeHoldConfirmMs: 0,
            closePriceFreezeMs: 0,
            startTimeHold: 5,
            endTimeHold: 15,
            configId: "id",
            sansJson: "{}",
            openMaxLastGapPts: openMaxLastGapPts);

    [Fact]
    public void Default_IsNull_SoGateStaysDisabled()
    {
        Assert.Null(Build(null).OpenMaxLastGapPts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-25)]
    [InlineData(115)]
    public void Value_IsNotClamped(int configured)
    {
        Assert.Equal(configured, Build(configured).OpenMaxLastGapPts);
    }

    [Fact]
    public void NotFound_LeavesGateDisabled()
    {
        Assert.Null(ConfigLoadResult.NotFound("host").OpenMaxLastGapPts);
    }
}
