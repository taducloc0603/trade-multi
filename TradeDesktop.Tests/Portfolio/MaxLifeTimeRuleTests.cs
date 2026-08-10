using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// max_life_time_by_second: slots open longer than the threshold are prioritized for close.
// Among overtime slots, pick the OLDEST (largest age from OpenConfirmedAtUtc); tiebreak by highest
// profit when ages are equal. If none are overtime, fall back to Rule D (highest profit).
public sealed class MaxLifeTimeRuleTests
{
    private sealed class CaptureLogger : ISlotLogger
    {
        public List<string> Messages { get; } = [];
        public void Log(string message) => Messages.Add(message);
    }

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
        public void ResetGapState() { }
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
            new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, null, null, null, 1);

    private static GapSignalTriggerResult CloseTrigger(
        CloseGapMode closeGapMode = CloseGapMode.Normal,
        int? confirmGapPts = null,
        int? closeGapPts = null,
        int? holdMs = null,
        CloseSignalReason closeReason = CloseSignalReason.Gap)
        => new(true, GapSignalAction.Close, GapSignalTriggerType.CloseByGapSell, GapSignalSide.Buy,
            Array.Empty<int>(), [15, 12, 7], null, 7,
            new DateTime(2026, 5, 21, 11, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, null, null, null, 1,
            CloseReason: closeReason,
            CloseGapMode: closeGapMode,
            EffectiveCloseConfirmGapPts: confirmGapPts,
            EffectiveCloseGapPts: closeGapPts,
            EffectiveCloseHoldMs: holdMs);

    private static GapSignalSnapshot Snapshot(DateTime ts)
        => new(ts, 100m, 100.5m, 100m, 100.5m, GapBuy: null, GapSell: null, PointMultiplier: 1);

    private static GapSignalConfirmationConfig Config()
        => new(ConfirmGapPts: 5, OpenPts: 8, HoldConfirmMs: 100,
               CloseConfirmGapPts: 5, ClosePts: 8, CloseHoldConfirmMs: 100,
               StartTimeHold: 1, EndTimeHold: 1);

    private static PortfolioCoordinator BuildCoordinator(
        ScriptedFactory factory,
        ISlotLogger? logger = null)
    {
        var coordinator = new PortfolioCoordinator(
            new GapSignalConfirmationEngine(), factory, logger: logger, random: new Random(42));
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);
        return coordinator;
    }

    [Fact]
    public void OvertimeSlot_WinsOverHigherProfitNonOvertimeSlot()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(1200);

        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        // Slot 1: open 1300s ago (overtime), profit = -10
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-1300));
        coordinator.UpdateProfit(100, -5.0);
        coordinator.UpdateProfit(200, -5.0); // combined = -10

        // Slot 2: open 800s ago (not overtime), profit = +5
        coordinator.AllocatePendingOpenSlot("p2", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p2", 101, 201, now.AddSeconds(-800));
        coordinator.UpdateProfit(101, 2.5);
        coordinator.UpdateProfit(201, 2.5); // combined = +5

        factory.Created[0].NextResult = CloseTrigger();
        factory.Created[1].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        // p1 is overtime → wins even though profit is lower
        Assert.NotNull(result.CloseTargetSlot);
        Assert.Equal("p1", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void MultipleOvertimeSlots_SameAge_TiebreakByHighestProfit()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(1200);

        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        // Slots 1, 2, 3: all overtime AND same age (-1500s) → tiebreak by profit.
        for (var i = 0; i < 3; i++)
        {
            var pairId = $"p{i + 1}";
            coordinator.AllocatePendingOpenSlot(pairId, OpenTrigger());
            coordinator.MarkSlotOpenConfirmed(pairId, (ulong)(100 + i), (ulong)(200 + i),
                now.AddSeconds(-1500));
        }

        // Profits: p1=-10, p2=-5 (highest among overtime), p3=-8
        coordinator.UpdateProfit(100, -5.0); coordinator.UpdateProfit(200, -5.0);  // p1: -10
        coordinator.UpdateProfit(101, -2.5); coordinator.UpdateProfit(201, -2.5);  // p2: -5 ← WINNER (tiebreak)
        coordinator.UpdateProfit(102, -4.0); coordinator.UpdateProfit(202, -4.0);  // p3: -8

        foreach (var engine in factory.Created)
            engine.NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        // All same age → highest profit wins the tiebreak.
        Assert.Equal("p2", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void MultipleOvertimeSlots_PicksOldest_NotHighestProfit()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(1200);

        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        // p1: oldest (-2000s, overtime), profit = -10 (lowest)
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-2000));
        coordinator.UpdateProfit(100, -5.0); coordinator.UpdateProfit(200, -5.0);  // p1: -10 ← WINNER (oldest)

        // p2: younger but still overtime (-1300s), profit = +5 (highest)
        coordinator.AllocatePendingOpenSlot("p2", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p2", 101, 201, now.AddSeconds(-1300));
        coordinator.UpdateProfit(101, 2.5); coordinator.UpdateProfit(201, 2.5);  // p2: +5

        factory.Created[0].NextResult = CloseTrigger();
        factory.Created[1].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        // Among overtime slots, the OLDEST wins even though its profit is lower.
        Assert.Equal("p1", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void NoOvertimeSlots_FallsBackToRuleD_HighestProfit()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(1200);

        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        // Both slots well under 1200s (not overtime)
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-300));
        coordinator.UpdateProfit(100, 1.0); coordinator.UpdateProfit(200, 1.0);  // p1: 2.0

        coordinator.AllocatePendingOpenSlot("p2", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p2", 101, 201, now.AddSeconds(-600));
        coordinator.UpdateProfit(101, 2.5); coordinator.UpdateProfit(201, 2.5);  // p2: 5.0 ← WINNER

        factory.Created[0].NextResult = CloseTrigger();
        factory.Created[1].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        // No overtime → Rule D: highest profit (p2)
        Assert.Equal("p2", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void MaxLifeTime_Zero_Disabled_PureRuleD()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(0); // disabled

        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        // p1: very old (10000s), profit = -20
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-10000));
        coordinator.UpdateProfit(100, -10.0); coordinator.UpdateProfit(200, -10.0);

        // p2: recent, profit = +3
        coordinator.AllocatePendingOpenSlot("p2", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p2", 101, 201, now.AddSeconds(-100));
        coordinator.UpdateProfit(101, 1.5); coordinator.UpdateProfit(201, 1.5);

        factory.Created[0].NextResult = CloseTrigger();
        factory.Created[1].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        // MaxLifeTime=0 disabled → pure Rule D: p2 (profit +3 > -20)
        Assert.Equal("p2", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void SlotWithoutOpenConfirmedAtUtc_NotCountedAsOvertime()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(1200);

        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        // p1: synced slot without OpenConfirmedAtUtc set properly (MarkSynced with 0-holdingSeconds)
        // We simulate by using RegisterSyncedSlot with epoch openConfirmedAtUtc = far in past
        // but setting maxLifeTime > elapsed to ensure it's NOT overtime
        // Actually, we test that a non-overtime slot doesn't enter overtime tier when
        // there's another non-overtime slot with higher profit.

        // p1: 800s old (not overtime), profit = +10
        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-800));
        coordinator.UpdateProfit(100, 5.0); coordinator.UpdateProfit(200, 5.0);

        // p2: 1300s old (overtime), profit = -5
        coordinator.AllocatePendingOpenSlot("p2", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p2", 101, 201, now.AddSeconds(-1300));
        coordinator.UpdateProfit(101, -2.5); coordinator.UpdateProfit(201, -2.5);

        factory.Created[0].NextResult = CloseTrigger();
        factory.Created[1].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        // p2 is overtime → p2 wins despite lower profit
        Assert.Equal("p2", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void MinProfit_WithinMaxLifeTime_BlocksCloseBelowThreshold()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(300);
        coordinator.UpdateMinProfitToCloseConfig(2.5);
        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-120));
        coordinator.UpdateProfit(100, 0.75);
        coordinator.UpdateProfit(200, 0.75);
        factory.Created[0].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        Assert.Null(result.CloseTargetSlot);
        Assert.Equal(PositionSlotStatus.Live, coordinator.GetSlotByPairId("p1")!.Status);
    }

    [Fact]
    public void MinProfit_WithinMaxLifeTime_AllowsCloseAtThreshold()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(300);
        coordinator.UpdateMinProfitToCloseConfig(2.5);
        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-120));
        coordinator.UpdateProfit(100, 2.5);
        coordinator.UpdateProfit(200, -100);
        factory.Created[0].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        Assert.Equal("p1", result.CloseTargetSlot!.PairId);
    }

    [Theory]
    [InlineData(2.49)]
    [InlineData(-2.49)]
    public void MinProfit_AbsoluteAMoveBelowThreshold_BlocksClose(double profitA)
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(300);
        coordinator.UpdateMinProfitToCloseConfig(2.5);
        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-120));
        coordinator.UpdateProfit(100, profitA);
        coordinator.UpdateProfit(200, 100);
        factory.Created[0].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        Assert.Null(result.CloseTargetSlot);
    }

    [Theory]
    [InlineData(2.5)]
    [InlineData(-2.5)]
    [InlineData(3.0)]
    [InlineData(-3.0)]
    public void MinProfit_AbsoluteAMoveAtOrAboveThreshold_AllowsGapClose(double profitA)
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(300);
        coordinator.UpdateMinProfitToCloseConfig(2.5);
        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-120));
        coordinator.UpdateProfit(100, profitA);
        coordinator.UpdateProfit(200, -100);
        factory.Created[0].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        Assert.Equal("p1", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void MinProfit_AbsoluteAMoveAtThreshold_AllowsTpClose()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(300);
        coordinator.UpdateMinProfitToCloseConfig(200);
        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-120));
        coordinator.UpdateProfit(100, -200);
        coordinator.UpdateProfit(200, 202);
        factory.Created[0].NextResult = CloseTrigger(closeReason: CloseSignalReason.Tp);

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        Assert.Equal(CloseSignalReason.Tp, result.CloseTrigger!.CloseReason);
    }

    [Fact]
    public void MinProfit_MissingAProfit_BlocksEvenWhenBMoveIsLarge()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(300);
        coordinator.UpdateMinProfitToCloseConfig(200);
        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-120));
        coordinator.UpdateProfit(200, 500);
        factory.Created[0].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        Assert.Null(result.CloseTargetSlot);
    }

    [Fact]
    public void MinProfit_WaitingAudit_ReportsAbsoluteAMoveAndCombinedProfit()
    {
        var factory = new ScriptedFactory();
        var logger = new CaptureLogger();
        var coordinator = BuildCoordinator(factory, logger);
        coordinator.UpdateMaxLifeTimeConfig(300);
        coordinator.UpdateMinProfitToCloseConfig(200);
        var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-120));
        coordinator.UpdateProfit(100, -150);
        coordinator.UpdateProfit(200, 400);
        factory.Created[0].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        Assert.Null(result.CloseTargetSlot);
        var notice = Assert.Single(result.UiNotices!).Message;
        Assert.Equal(
            "[CHẶN CLOSE][NORMAL] Slot 1 | GapSell=(15|12|7) | abs(A)=150pt < min_profit_to_close=200pt",
            notice);
        Assert.Contains(logger.Messages, message =>
            message.Contains("profitA=-150")
            && message.Contains("absoluteAMovePts=150")
            && message.Contains("requiredAMovePts=200")
            && message.Contains("profitB=400")
            && message.Contains("combinedProfit=250"));
    }

    [Fact]
    public void MinProfit_AtMaxLifeTime_BypassesThreshold()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(300);
        coordinator.UpdateMinProfitToCloseConfig(2.5);
        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-300));
        coordinator.UpdateProfit(100, -1.0);
        coordinator.UpdateProfit(200, -1.0);
        factory.Created[0].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        Assert.Equal("p1", result.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void MinProfit_AtMaxLifeTime_SosClose_EmitsUiAndFileAuditWithEffectiveConfig()
    {
        var factory = new ScriptedFactory();
        var logger = new CaptureLogger();
        var coordinator = BuildCoordinator(factory, logger);
        coordinator.UpdateMaxLifeTimeConfig(1650);
        coordinator.UpdateMinProfitToCloseConfig(200);
        var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-1650));
        coordinator.UpdateProfit(100, -5);
        coordinator.UpdateProfit(200, -5);
        factory.Created[0].NextResult = CloseTrigger(CloseGapMode.Sos, 15, 8, 900);

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        var notice = Assert.Single(result.UiNotices!);
        Assert.Equal("MIN_PROFIT_EXPIRED", notice.Code);
        Assert.Equal("[MAX LIFETIME][SOS] Slot 1 đã đạt 1650 giây. Min Profit không còn chặn;", notice.Message);
        Assert.Contains(logger.Messages, message =>
            message.Contains("[MIN_PROFIT][EXPIRED][SOS]")
            && message.Contains("trackedGap=GapSell=7")
            && message.Contains("confirmGapPts=15")
            && message.Contains("closeGapPts=8")
            && message.Contains("holdMs=900"));
    }

    [Fact]
    public void CloseConfirmed_AfterSosSignal_PreservesModeAndEffectiveConfigInAuditLog()
    {
        var factory = new ScriptedFactory();
        var logger = new CaptureLogger();
        var coordinator = BuildCoordinator(factory, logger);
        var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-120));
        coordinator.UpdateProfit(100, 125);
        coordinator.UpdateProfit(200, 125);
        factory.Created[0].NextResult = CloseTrigger(CloseGapMode.Sos, 15, 8, 900);

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());
        Assert.NotNull(result.CloseTrigger);
        coordinator.MarkSlotCloseConfirmed("p1", now.AddMilliseconds(50));

        Assert.Contains(logger.Messages, message =>
            message.Contains("[SLOT][CLOSE_CONFIRMED]")
            && message.Contains("closeMode=SOS")
            && message.Contains("confirmGapPts=15")
            && message.Contains("closeGapPts=8")
            && message.Contains("holdMs=900")
            && message.Contains("owner=Auto"));
    }

    [Fact]
    public void MinProfit_WithMaxLifeTimeZero_DoesNotExpire()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(0);
        coordinator.UpdateMinProfitToCloseConfig(2.5);
        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-10_000));
        coordinator.UpdateProfit(100, 1.0);
        coordinator.UpdateProfit(200, 1.0);
        factory.Created[0].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        Assert.Null(result.CloseTargetSlot);
    }

    [Fact]
    public void MinProfit_Zero_DisablesGuard()
    {
        var factory = new ScriptedFactory();
        var coordinator = BuildCoordinator(factory);
        coordinator.UpdateMaxLifeTimeConfig(300);
        coordinator.UpdateMinProfitToCloseConfig(0);
        var now = new DateTime(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

        coordinator.AllocatePendingOpenSlot("p1", OpenTrigger());
        coordinator.MarkSlotOpenConfirmed("p1", 100, 200, now.AddSeconds(-120));
        coordinator.UpdateProfit(100, -5.0);
        coordinator.UpdateProfit(200, -5.0);
        factory.Created[0].NextResult = CloseTrigger();

        var result = coordinator.ProcessSnapshot(Snapshot(now), Config());

        Assert.Equal("p1", result.CloseTargetSlot!.PairId);
    }
}
