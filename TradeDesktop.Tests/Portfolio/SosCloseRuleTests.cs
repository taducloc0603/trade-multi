using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

public sealed class SosCloseRuleTests
{
    private sealed class CaptureCloseEngine : ICloseSignalEngine
    {
        public GapSignalConfirmationConfig? LastConfig { get; private set; }
        public int GapResetCount { get; private set; }

        public GapSignalTriggerResult? ProcessSnapshot(
            GapSignalSnapshot snapshot,
            GapSignalConfirmationConfig config,
            TradingOpenMode openMode,
            double? slotProfit = null)
        {
            LastConfig = config;
            return null;
        }

        public void ResetGapState() => GapResetCount++;
        public void Reset() => GapResetCount++;
    }

    private sealed class CaptureFactory : ICloseSignalEngineFactory
    {
        public CaptureCloseEngine Engine { get; } = new();
        public ICloseSignalEngine Create() => Engine;
    }

    private static readonly DateTime OpenAt = new(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc);

    private static GapSignalSnapshot Snapshot(double secondsAfterOpen) => new(
        OpenAt.AddSeconds(secondsAfterOpen), 100m, 101m, 100m, 101m, null, null, 1);

    private static GapSignalConfirmationConfig Config() => new(
        ConfirmGapPts: 10,
        OpenPts: 20,
        HoldConfirmMs: 0,
        CloseConfirmGapPts: 30,
        ClosePts: 40,
        CloseConfirmTpProfit: 5,
        CloseTpProfit: 10,
        SosTriggerAOpenDistancePts: 20,
        SosTriggerAfterSeconds: 60,
        SosCloseConfirmGapPts: -12,
        SosCloseGapPts: -8);

    private static (PortfolioCoordinator Coordinator, CaptureCloseEngine Engine, PositionSlot Slot) Setup()
    {
        var factory = new CaptureFactory();
        var coordinator = new PortfolioCoordinator(new GapSignalConfirmationEngine(), factory);
        var slot = coordinator.RegisterSyncedSlot(
            "pair-1", TradingPositionSide.Buy, TradingOpenMode.GapBuy, 101, 201, OpenAt, 0);
        return (coordinator, factory.Engine, slot);
    }

    [Fact]
    public void PositiveAOpenDistanceReached_UsesSosGapThresholds_ButKeepsTpThresholds()
    {
        var (coordinator, engine, slot) = Setup();
        coordinator.UpdateProfit(101, 20);

        coordinator.ProcessSnapshot(Snapshot(10), Config());

        Assert.True(slot.IsSosActive);
        Assert.Equal("A_OPEN_DISTANCE", slot.SosActivationSource);
        Assert.Equal(-12, engine.LastConfig!.SosCloseConfirmGapPts);
        Assert.Equal(-12, engine.LastConfig.CloseConfirmGapPts);
        Assert.Equal(-8, engine.LastConfig.ClosePts);
        Assert.Equal(CloseGapMode.Sos, engine.LastConfig.CloseGapMode);
        Assert.Equal(5, engine.LastConfig.CloseConfirmTpProfit);
        Assert.Equal(10, engine.LastConfig.CloseTpProfit);
        Assert.Equal(1, engine.GapResetCount);
    }

    [Fact]
    public void AOpenDistanceDropsBeforeTimeTrigger_ReturnsToNormalAndResetsGapAgain()
    {
        var (coordinator, engine, slot) = Setup();
        coordinator.UpdateProfit(101, -20);
        coordinator.ProcessSnapshot(Snapshot(10), Config());

        coordinator.UpdateProfit(101, -15);
        coordinator.ProcessSnapshot(Snapshot(20), Config());

        Assert.False(slot.IsSosActive);
        Assert.Equal(30, engine.LastConfig!.CloseConfirmGapPts);
        Assert.Equal(40, engine.LastConfig.ClosePts);
        Assert.Equal(CloseGapMode.Normal, engine.LastConfig.CloseGapMode);
        Assert.Equal(2, engine.GapResetCount);
    }

    [Fact]
    public void NegativeAOpenDistanceReached_ActivatesSosWithoutLegBProfit()
    {
        var (coordinator, engine, slot) = Setup();
        coordinator.UpdateProfit(101, -20);

        coordinator.ProcessSnapshot(Snapshot(10), Config());

        Assert.True(slot.IsSosActive);
        Assert.Equal("A_OPEN_DISTANCE", slot.SosActivationSource);
        Assert.Equal(-12, engine.LastConfig!.CloseConfirmGapPts);
    }

    [Fact]
    public void LargeLegBProfit_DoesNotActivateWhenAOpenDistanceIsBelowThreshold()
    {
        var (coordinator, engine, slot) = Setup();
        coordinator.UpdateProfit(101, 15);
        coordinator.UpdateProfit(201, 100);

        coordinator.ProcessSnapshot(Snapshot(10), Config());

        Assert.False(slot.IsSosActive);
        Assert.Equal(30, engine.LastConfig!.CloseConfirmGapPts);
    }

    [Fact]
    public void TimeReached_ActivatesSosEvenWhenProfitIsBelowThreshold()
    {
        var (coordinator, engine, slot) = Setup();
        coordinator.UpdateProfit(101, -10);
        coordinator.UpdateProfit(201, 0);

        coordinator.ProcessSnapshot(Snapshot(60), Config());

        Assert.True(slot.IsSosActive);
        Assert.Equal("TIME", slot.SosActivationSource);
        Assert.Equal(-12, engine.LastConfig!.CloseConfirmGapPts);
        Assert.Equal(-8, engine.LastConfig.ClosePts);
        Assert.Equal(1, engine.GapResetCount);
    }
}
