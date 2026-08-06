using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests.Config;

// Quota config (max_total_opens / max_buy_opens / max_sell_opens) flows from DB → ConfigRecord →
// ConfigLoadResult. These tests lock the mapping + normalization at the ConfigLoadResult.Success layer.
public sealed class ConfigQuotaMappingTests
{
    private static ConfigLoadResult Build(int maxBuy, int maxSell, int maxTotal)
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
            holdConfirmMs: 650,
            openPriceFreezeMs: 2000,
            closePts: 1,
            closeConfirmGapPts: 0,
            closeTpProfit: 1,
            closeConfirmTpProfit: 0,
            closeMaxTpProfit: 35,
            closeHoldConfirmMs: 650,
            closePriceFreezeMs: 2000,
            startTimeHold: 5,
            endTimeHold: 15,
            configId: "id",
            sansJson: "{}",
            maxBuyOpens: maxBuy,
            maxSellOpens: maxSell,
            maxTotalOpens: maxTotal);

    [Fact]
    public void Success_MapsQuotaFromDb()
    {
        var result = Build(maxBuy: 3, maxSell: 3, maxTotal: 5);

        Assert.Equal(3, result.MaxBuyOpens);
        Assert.Equal(3, result.MaxSellOpens);
        Assert.Equal(5, result.MaxTotalOpens);
    }

    [Fact]
    public void Success_FloorsQuotaToAtLeastOne()
    {
        // DB columns missing / zero must never lock the whole portfolio (floor to 1).
        var result = Build(maxBuy: 0, maxSell: 0, maxTotal: 0);

        Assert.Equal(1, result.MaxBuyOpens);
        Assert.Equal(1, result.MaxSellOpens);
        Assert.Equal(1, result.MaxTotalOpens);
    }

    [Fact]
    public void Success_DefaultsPreserveLegacyQuota_WhenColumnsAbsent()
    {
        // When the Success factory is called without quota args (older callers / rows without
        // the columns), defaults must match the historical hardcode 5/3/3.
        var result = ConfigLoadResult.Success(
            machineHostName: "host",
            mapName1: "A",
            mapName2: "B",
            manualHwndColumns: null,
            platformA: "mt5",
            platformB: "mt5",
            point: 100,
            openPts: 1,
            confirmGapPts: 0,
            holdConfirmMs: 650,
            openPriceFreezeMs: 2000,
            closePts: 1,
            closeConfirmGapPts: 0,
            closeTpProfit: 1,
            closeConfirmTpProfit: 0,
            closeMaxTpProfit: 35,
            closeHoldConfirmMs: 650,
            closePriceFreezeMs: 2000,
            startTimeHold: 5,
            endTimeHold: 15,
            configId: "id",
            sansJson: "{}");

        Assert.Equal(3, result.MaxBuyOpens);
        Assert.Equal(3, result.MaxSellOpens);
        Assert.Equal(5, result.MaxTotalOpens);
    }

    [Fact]
    public void Success_MapsRandomPostOpenLockRange()
    {
        var result = ConfigLoadResult.Success(
            machineHostName: "host", mapName1: "A", mapName2: "B", manualHwndColumns: null,
            platformA: "mt5", platformB: "mt5", point: 100, openPts: 1,
            confirmGapPts: 0, holdConfirmMs: 650, openPriceFreezeMs: 2000,
            closePts: 1, closeConfirmGapPts: 0, closeTpProfit: 1,
            closeConfirmTpProfit: 0, closeMaxTpProfit: 35, closeHoldConfirmMs: 650,
            closePriceFreezeMs: 2000, startTimeHold: 5, endTimeHold: 15,
            configId: "id", sansJson: "{}",
            rdStartPostOpenLockSeconds: 30,
            rdEndPostOpenLockSeconds: 45);

        Assert.Equal(30, result.RdStartPostOpenLockSeconds);
        Assert.Equal(45, result.RdEndPostOpenLockSeconds);
    }

    [Fact]
    public void Success_MapsOppositeOpenMinimumDistance()
    {
        var result = ConfigLoadResult.Success(
            machineHostName: "host", mapName1: "A", mapName2: "B", manualHwndColumns: null,
            platformA: "mt5", platformB: "mt5", point: 100, openPts: 1,
            confirmGapPts: 0, holdConfirmMs: 650, openPriceFreezeMs: 2000,
            closePts: 1, closeConfirmGapPts: 0, closeTpProfit: 1,
            closeConfirmTpProfit: 0, closeMaxTpProfit: 35, closeHoldConfirmMs: 650,
            closePriceFreezeMs: 2000, startTimeHold: 5, endTimeHold: 15,
            configId: "id", sansJson: "{}", oppositeOpenMinDistancePts: 25);

        Assert.Equal(25, result.OppositeOpenMinDistancePts);
    }

    [Fact]
    public void Success_MapsSameActionRandomRange()
    {
        var result = ConfigLoadResult.Success(
            machineHostName: "host", mapName1: "A", mapName2: "B", manualHwndColumns: null,
            platformA: "mt5", platformB: "mt5", point: 100, openPts: 1,
            confirmGapPts: 0, holdConfirmMs: 650, openPriceFreezeMs: 2000,
            closePts: 1, closeConfirmGapPts: 0, closeTpProfit: 1,
            closeConfirmTpProfit: 0, closeMaxTpProfit: 35, closeHoldConfirmMs: 650,
            closePriceFreezeMs: 2000, startTimeHold: 5, endTimeHold: 15,
            configId: "id", sansJson: "{}",
            rdStartSameActionLockSeconds: 6,
            rdEndSameActionLockSeconds: 12);

        Assert.Equal(6, result.RdStartSameActionLockSeconds);
        Assert.Equal(12, result.RdEndSameActionLockSeconds);
    }
}
