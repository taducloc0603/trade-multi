using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests.Config;

// open_price_freeze_ms / close_price_freeze_ms là hai cột ĐỘC LẬP, mỗi cột chỉ dùng
// giá trị của chính nó. Nhánh TIME có nối lại 4 cột hold/max-tick vào ConfigLoadResult,
// nhưng price-freeze VẪN KHÔNG được fallback về hold-time — các test dưới khoá điều đó.
public sealed class PriceFreezeConfigMappingTests
{
    private static ConfigLoadResult Build(
        int openPriceFreezeMs,
        int closePriceFreezeMs,
        int holdConfirmMs = 0,
        int closeHoldConfirmMs = 0)
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
            holdConfirmMs: holdConfirmMs,
            openPriceFreezeMs: openPriceFreezeMs,
            closePts: 1,
            closeConfirmGapPts: 0,
            closeTpProfit: 1,
            closeConfirmTpProfit: 0,
            closeMaxTpProfit: 35,
            closeHoldConfirmMs: closeHoldConfirmMs,
            closePriceFreezeMs: closePriceFreezeMs,
            startTimeHold: 5,
            endTimeHold: 15,
            configId: "id",
            sansJson: "{}");

    [Fact]
    public void PriceFreeze_MapsEachColumnToItsOwnValue()
    {
        var result = Build(openPriceFreezeMs: 2000, closePriceFreezeMs: 3000);

        Assert.Equal(2000, result.OpenPriceFreezeMs);
        Assert.Equal(3000, result.ClosePriceFreezeMs);
    }

    [Fact]
    public void PriceFreezeZero_StaysZero()
    {
        // Quy ước: 0 = tắt kiểm tra price-freeze. Không có fallback về hold-time.
        var result = Build(openPriceFreezeMs: 0, closePriceFreezeMs: 0);

        Assert.Equal(0, result.OpenPriceFreezeMs);
        Assert.Equal(0, result.ClosePriceFreezeMs);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-5000, 0)]
    [InlineData(1500, 1500)]
    public void PriceFreeze_NormalizesNegativeToZero(int configured, int expected)
    {
        var result = Build(openPriceFreezeMs: configured, closePriceFreezeMs: configured);

        Assert.Equal(expected, result.OpenPriceFreezeMs);
        Assert.Equal(expected, result.ClosePriceFreezeMs);
    }

    [Fact]
    public void OpenAndClosePriceFreeze_AreIndependentColumns()
    {
        var result = Build(openPriceFreezeMs: 2000, closePriceFreezeMs: 0);

        Assert.Equal(2000, result.OpenPriceFreezeMs);
        Assert.Equal(0, result.ClosePriceFreezeMs);
    }

    [Fact]
    public void PriceFreezeZero_DoesNotFallBackToHoldConfirm()
    {
        // Điểm mấu chốt của nhánh TIME: hold-time đã được nối lại, nhưng price-freeze = 0
        // vẫn phải là 0 (tắt), KHÔNG mượn giá trị hold.
        var result = Build(
            openPriceFreezeMs: 0,
            closePriceFreezeMs: 0,
            holdConfirmMs: 2500,
            closeHoldConfirmMs: 1800);

        Assert.Equal(0, result.OpenPriceFreezeMs);
        Assert.Equal(0, result.ClosePriceFreezeMs);
        Assert.Equal(2500, result.HoldConfirmMs);
        Assert.Equal(1800, result.CloseHoldConfirmMs);
    }

    [Fact]
    public void ConfigLoadResult_ExposesHoldAndTickColumns()
    {
        // Nhánh TIME: 4 cột đã được nối lại vào config pipeline.
        // KHÔNG chạy docs/DROP-DEPRECATED-SIGNAL-COLUMNS.sql trên nhánh này.
        var properties = typeof(ConfigLoadResult)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains("HoldConfirmMs", properties);
        Assert.Contains("CloseHoldConfirmMs", properties);
        Assert.Contains("OpenMaxTimesTick", properties);
        Assert.Contains("CloseMaxTimesTick", properties);
        Assert.Contains("OpenMaxLastGapPts", properties);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-5000, 0)]
    [InlineData(2500, 2500)]
    public void HoldConfirm_NormalizesNegativeToZero(int configured, int expected)
    {
        var result = Build(
            openPriceFreezeMs: 0,
            closePriceFreezeMs: 0,
            holdConfirmMs: configured,
            closeHoldConfirmMs: configured);

        Assert.Equal(expected, result.HoldConfirmMs);
        Assert.Equal(expected, result.CloseHoldConfirmMs);
    }
}
