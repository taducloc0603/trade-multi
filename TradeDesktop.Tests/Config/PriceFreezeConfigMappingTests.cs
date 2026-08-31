using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests.Config;

// open_price_freeze_ms / close_price_freeze_ms là hai cột ĐỘC LẬP, mỗi cột chỉ dùng
// giá trị của chính nó. Fallback cũ (price_freeze <- hold_confirm) đã bị bỏ, và bốn cột
// hold/max-tick đã bị gỡ khỏi ConfigLoadResult — nên không còn nguồn nào để fallback.
public sealed class PriceFreezeConfigMappingTests
{
    private static ConfigLoadResult Build(
        int openPriceFreezeMs,
        int closePriceFreezeMs)
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
            openPriceFreezeMs: openPriceFreezeMs,
            closePts: 1,
            closeConfirmGapPts: 0,
            closeTpProfit: 1,
            closeConfirmTpProfit: 0,
            closeMaxTpProfit: 35,
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
    public void ConfigLoadResult_NoLongerExposesDeprecatedHoldOrTickColumns()
    {
        // Khoá contract: 4 cột cũ đã bị gỡ khỏi source, DB có thể DROP an toàn.
        var properties = typeof(ConfigLoadResult)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("HoldConfirmMs", properties);
        Assert.DoesNotContain("CloseHoldConfirmMs", properties);
        Assert.DoesNotContain("OpenMaxTimesTick", properties);
        Assert.DoesNotContain("CloseMaxTimesTick", properties);
    }
}
