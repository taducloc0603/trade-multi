using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

public sealed class SosCloseRuleTests
{
    private sealed class CaptureLogger : ISlotLogger
    {
        public List<string> Messages { get; } = [];
        public void Log(string message) => Messages.Add(message);
    }

    private sealed class CaptureCloseEngine : ICloseSignalEngine
    {
        public GapSignalConfirmationConfig? LastConfig { get; private set; }
        public int GapResetCount { get; private set; }
        public int ProcessCount { get; private set; }
        public GapSignalTriggerResult? NextResult { get; set; }

        public GapSignalTriggerResult? ProcessSnapshot(
            GapSignalSnapshot snapshot,
            GapSignalConfirmationConfig config,
            TradingOpenMode openMode,
            double? slotProfit = null)
        {
            ProcessCount++;
            LastConfig = config;
            return NextResult;
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

    [Fact]
    public void GlobalCooldown_OnlySosSlotWithCloseSignalCanTriggerClose()
    {
        var sosFactory = new CaptureFactory();
        var logger = new CaptureLogger();
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(), sosFactory, logger, new Random(42));
        coordinator.UpdateCooldownConfig(30, 30);
        coordinator.UpdatePostOpenLockConfig(0, 0);
        var now = DateTime.UtcNow;
        coordinator.RecoverSlotsFromPersisted(new[]
        {
            new RecoveredSlotData(
                1, "pair-sos", TradingPositionSide.Buy, TradingOpenMode.GapBuy,
                101, 201, now.AddSeconds(-10), 0)
        });
        coordinator.UpdateProfit(101, 20);
        sosFactory.Engine.NextResult = CloseTrigger(now);

        var result = coordinator.ProcessSnapshot(
            new GapSignalSnapshot(now, 100m, 101m, 100m, 101m, null, null, 1),
            Config() with { SosTriggerAfterSeconds = 0 });

        Assert.NotNull(result.CloseTrigger);
        Assert.Equal("pair-sos", result.CloseTargetSlot!.PairId);
        var notice = Assert.Single(result.UiNotices!);
        Assert.Equal("SOS_COOLDOWN_BYPASS", notice.Code);
        Assert.Contains(logger.Messages, message =>
            message.Contains("[SLOT][COOLDOWN][SOS_BYPASS]")
            && message.Contains("source=A_OPEN_DISTANCE")
            && message.Contains("closeMode=SOS"));

        var normalFactory = new CaptureFactory();
        var normalCoordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(), normalFactory, random: new Random(42));
        normalCoordinator.UpdateCooldownConfig(30, 30);
        normalCoordinator.RecoverSlotsFromPersisted(new[]
        {
            new RecoveredSlotData(
                1, "pair-normal", TradingPositionSide.Buy, TradingOpenMode.GapBuy,
                301, 401, now.AddSeconds(-10), 0)
        });
        normalCoordinator.UpdateProfit(301, 19);
        normalFactory.Engine.NextResult = CloseTrigger(now);

        var normalResult = normalCoordinator.ProcessSnapshot(
            new GapSignalSnapshot(now, 100m, 101m, 100m, 101m, null, null, 1),
            Config() with { SosTriggerAfterSeconds = 0 });

        Assert.Null(normalResult.CloseTrigger);
        Assert.Equal(0, normalFactory.Engine.ProcessCount);
    }

    [Fact]
    public void GlobalCooldown_SosWithoutCloseSignal_DoesNotClose()
    {
        var factory = new CaptureFactory();
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(), factory, random: new Random(42));
        coordinator.UpdateCooldownConfig(30, 30);
        var now = DateTime.UtcNow;
        coordinator.RecoverSlotsFromPersisted(new[]
        {
            new RecoveredSlotData(
                1, "pair-sos", TradingPositionSide.Buy, TradingOpenMode.GapBuy,
                101, 201, now.AddSeconds(-10), 0)
        });
        coordinator.UpdateProfit(101, 20);

        var result = coordinator.ProcessSnapshot(
            new GapSignalSnapshot(now, 100m, 101m, 100m, 101m, null, null, 1),
            Config() with { SosTriggerAfterSeconds = 0 });

        Assert.Null(result.CloseTrigger);
        Assert.Equal(1, factory.Engine.ProcessCount);
    }

    private static GapSignalTriggerResult CloseTrigger(DateTime now) => new(
        true, GapSignalAction.Close, GapSignalTriggerType.CloseByGapSell, GapSignalSide.Sell,
        Array.Empty<int>(), new[] { -8 }, null, -8, now,
        100m, 101m, 100m, 101m, null, null, null, null, 1,
        CloseGapMode: CloseGapMode.Sos);
}
