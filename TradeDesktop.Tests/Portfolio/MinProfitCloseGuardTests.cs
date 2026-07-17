using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// close_min_profit guard (coordinator-side): chặn CẮT-LỖ theo gap-reversal khi profit A+B của slot
// chưa đạt ngưỡng. Chỉ áp cho CloseReason.Gap (TP không bị đụng). Lệnh QUÁ HẠN
// (age > max_life_time_by_second) được MIỄN guard → vẫn cắt lỗ theo gap. CloseMinProfit<=0 = tắt.
public sealed class MinProfitCloseGuardTests
{
    private sealed class ScriptedCloseSignalEngine : ICloseSignalEngine
    {
        public GapSignalTriggerResult? NextResult { get; set; }

        public GapSignalTriggerResult? ProcessSnapshot(
            GapSignalSnapshot snapshot,
            GapSignalConfirmationConfig config,
            TradingOpenMode openMode,
            double? slotProfit = null)
            => NextResult;

        public void Reset() { }
    }

    private sealed class ScriptedFactory : ICloseSignalEngineFactory
    {
        public List<ScriptedCloseSignalEngine> Created { get; } = new();

        public ICloseSignalEngine Create()
        {
            var engine = new ScriptedCloseSignalEngine();
            Created.Add(engine);
            return engine;
        }
    }

    private static GapSignalTriggerResult OpenTrigger()
        => new(true, GapSignalAction.Open, GapSignalTriggerType.OpenByGapBuy, GapSignalSide.Buy,
            Array.Empty<int>(), Array.Empty<int>(), null, null,
            new DateTime(2026, 7, 17, 10, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, null, null, null, 1);

    private static GapSignalTriggerResult GapCloseTrigger()
        => new(true, GapSignalAction.Close, GapSignalTriggerType.CloseByGapSell, GapSignalSide.Buy,
            Array.Empty<int>(), Array.Empty<int>(), null, null,
            new DateTime(2026, 7, 17, 11, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, null, null, null, 1,
            CloseSignalReason.Gap);

    private static GapSignalTriggerResult TpCloseTrigger()
        => new(true, GapSignalAction.Close, GapSignalTriggerType.CloseByGapSell, GapSignalSide.Buy,
            Array.Empty<int>(), Array.Empty<int>(), null, null,
            new DateTime(2026, 7, 17, 11, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, null, null, null, 1,
            CloseSignalReason.Tp);

    private static GapSignalSnapshot Snapshot(DateTime ts)
        => new(ts, 100m, 100.5m, 100m, 100.5m, GapBuy: null, GapSell: null, PointMultiplier: 1);

    private static GapSignalConfirmationConfig Config(double closeMinProfit)
        => new(ConfirmGapPts: 5, OpenPts: 8, HoldConfirmMs: 100,
               CloseConfirmGapPts: 5, ClosePts: 8, CloseHoldConfirmMs: 100,
               StartTimeHold: 1, EndTimeHold: 1, CloseMinProfit: closeMinProfit);

    private static PortfolioCoordinator BuildCoordinator(ScriptedFactory factory, int maxLifeTimeSec)
    {
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(), factory, logger: null, random: new Random(42));
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);
        coordinator.UpdateMaxLifeTimeConfig(maxLifeTimeSec);
        return coordinator;
    }

    // Mở 1 slot Live với tuổi + profit cho trước; scripted engine trả về closeTrigger.
    private static PortfolioCoordinator SetupSingleSlot(
        ScriptedFactory factory,
        int maxLifeTimeSec,
        DateTime now,
        int ageSeconds,
        double combinedProfit,
        GapSignalTriggerResult closeTrigger)
    {
        var coordinator = BuildCoordinator(factory, maxLifeTimeSec);
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-ageSeconds));
        coordinator.UpdateProfit(100, combinedProfit / 2.0);
        coordinator.UpdateProfit(200, combinedProfit / 2.0);
        factory.Created[0].NextResult = closeTrigger;
        return coordinator;
    }

    [Fact]
    public void NonOvertime_GapClose_BelowMinProfit_Suppressed()
    {
        var factory = new ScriptedFactory();
        var now = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
        // age 300s < maxLife 1200 → không quá hạn; profit -10 < min 20 → guard chặn.
        var coordinator = SetupSingleSlot(factory, 1200, now, 300, -10.0, GapCloseTrigger());

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config(closeMinProfit: 20));

        Assert.Null(result.CloseTargetSlot);
    }

    [Fact]
    public void NonOvertime_GapClose_MeetsMinProfit_Closes()
    {
        var factory = new ScriptedFactory();
        var now = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
        // profit 25 >= min 20 → được đóng.
        var coordinator = SetupSingleSlot(factory, 1200, now, 300, 25.0, GapCloseTrigger());

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config(closeMinProfit: 20));

        Assert.NotNull(result.CloseTargetSlot);
        Assert.Equal("p1", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void Overtime_GapClose_BelowMinProfit_Closes_BypassGuard()
    {
        var factory = new ScriptedFactory();
        var now = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
        // age 1300s > maxLife 1200 → QUÁ HẠN; profit -10 < min 20 nhưng được MIỄN guard → vẫn đóng.
        var coordinator = SetupSingleSlot(factory, 1200, now, 1300, -10.0, GapCloseTrigger());

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config(closeMinProfit: 20));

        Assert.NotNull(result.CloseTargetSlot);
        Assert.Equal("p1", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void TpClose_BelowMinProfit_Closes_GuardIgnoresTp()
    {
        var factory = new ScriptedFactory();
        var now = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
        // TP close (CloseReason.Tp), profit 5 < min 20 → guard KHÔNG áp cho TP → vẫn đóng.
        var coordinator = SetupSingleSlot(factory, 1200, now, 300, 5.0, TpCloseTrigger());

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config(closeMinProfit: 20));

        Assert.NotNull(result.CloseTargetSlot);
        Assert.Equal("p1", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void MinProfitZero_NoSuppression_EvenWhenLosing()
    {
        var factory = new ScriptedFactory();
        var now = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
        // CloseMinProfit=0 = guard tắt → gap-close vẫn đóng dù lỗ -10.
        var coordinator = SetupSingleSlot(factory, 1200, now, 300, -10.0, GapCloseTrigger());

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config(closeMinProfit: 0));

        Assert.NotNull(result.CloseTargetSlot);
        Assert.Equal("p1", result.CloseTargetSlot!.PairId);
    }
}
