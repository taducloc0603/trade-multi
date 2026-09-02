using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests.Config;

// 4 cột ngưỡng gap thường (open_pts, confirm_gap_pts, close_pts, close_confirm_gap_pts) giữ
// nguyên dấu từ DB, giống cặp sos_close_*_pts. ÂM nghĩa là nới ngưỡng về phía trong (kiểu SOS).
// Trước đây chúng bị Math.Abs ép dương ở ConfigService nên điền số âm không có tác dụng gì.
public sealed class SignedGapThresholdMappingTests
{
    private static ConfigLoadResult Build(
        int openPts,
        int confirmGapPts,
        int closePts,
        int closeConfirmGapPts)
        => ConfigLoadResult.Success(
            machineHostName: "host",
            mapName1: "A",
            mapName2: "B",
            manualHwndColumns: null,
            platformA: "mt5",
            platformB: "mt5",
            point: 100,
            openPts: openPts,
            confirmGapPts: confirmGapPts,
            openPriceFreezeMs: 0,
            closePts: closePts,
            closeConfirmGapPts: closeConfirmGapPts,
            closeTpProfit: 1,
            closeConfirmTpProfit: 0,
            closeMaxTpProfit: 35,
            closePriceFreezeMs: 0,
            startTimeHold: 5,
            endTimeHold: 15,
            configId: "id",
            sansJson: "{}");

    [Fact]
    public void NegativeThresholds_KeepTheirSign()
    {
        var result = Build(
            openPts: -3,
            confirmGapPts: -5,
            closePts: -8,
            closeConfirmGapPts: -12);

        Assert.Equal(-3, result.OpenPts);
        Assert.Equal(-5, result.ConfirmGapPts);
        Assert.Equal(-8, result.ClosePts);
        Assert.Equal(-12, result.CloseConfirmGapPts);
    }

    [Fact]
    public void PositiveThresholds_AreUnchanged()
    {
        var result = Build(
            openPts: 3,
            confirmGapPts: 5,
            closePts: 8,
            closeConfirmGapPts: 12);

        Assert.Equal(3, result.OpenPts);
        Assert.Equal(5, result.ConfirmGapPts);
        Assert.Equal(8, result.ClosePts);
        Assert.Equal(12, result.CloseConfirmGapPts);
    }

    [Fact]
    public void ZeroThresholds_StayZero()
    {
        var result = Build(
            openPts: 0,
            confirmGapPts: 0,
            closePts: 0,
            closeConfirmGapPts: 0);

        Assert.Equal(0, result.OpenPts);
        Assert.Equal(0, result.ConfirmGapPts);
        Assert.Equal(0, result.ClosePts);
        Assert.Equal(0, result.CloseConfirmGapPts);
    }

    [Fact]
    public void MixedSignThresholds_AreIndependent()
    {
        var result = Build(
            openPts: 8,
            confirmGapPts: -5,
            closePts: -8,
            closeConfirmGapPts: 12);

        Assert.Equal(8, result.OpenPts);
        Assert.Equal(-5, result.ConfirmGapPts);
        Assert.Equal(-8, result.ClosePts);
        Assert.Equal(12, result.CloseConfirmGapPts);
    }
}
