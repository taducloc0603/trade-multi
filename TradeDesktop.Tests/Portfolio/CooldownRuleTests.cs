using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

public sealed class CooldownRuleTests
{
    private sealed class CaptureLogger : ISlotLogger
    {
        public List<string> Messages { get; } = [];
        public void Log(string message) => Messages.Add(message);
    }

    private static PortfolioCoordinator CreateCoordinator(int seed = 42, ISlotLogger? logger = null)
        => new(new GapSignalConfirmationEngine(), new CloseSignalEngineFactory(), logger, new Random(seed));

    private static GapSignalTriggerResult Trigger(GapSignalSide side = GapSignalSide.Buy)
        => new(true, GapSignalAction.Open,
            side == GapSignalSide.Buy ? GapSignalTriggerType.OpenByGapBuy : GapSignalTriggerType.OpenByGapSell,
            side, [], [], null, null, DateTime.UtcNow,
            null, null, null, null, null, null, null, null, 1);

    private static void AddLiveSlot(
        PortfolioCoordinator coordinator,
        string pairId,
        TradingPositionSide side,
        DateTime confirmedAt)
    {
        var ticketBase = (ulong)(pairId.GetHashCode() & 0xffff) + 1;
        coordinator.RegisterSyncedSlot(
            pairId, side,
            side == TradingPositionSide.Buy ? TradingOpenMode.GapBuy : TradingOpenMode.GapSell,
            ticketBase, ticketBase + 100, confirmedAt, holdingSeconds: 0);
    }

    [Theory]
    [InlineData(TradingPositionSide.Buy)]
    [InlineData(TradingPositionSide.Sell)]
    public void SameSideOpen_WaitsSingleRandomInterval(TradingPositionSide side)
    {
        var coordinator = CreateCoordinator();
        var now = DateTime.UtcNow;
        var first = coordinator.TryAcquireTradeAction(now, "OPEN", "first", side: side);

        Assert.True(first.Acquired);
        Assert.InRange(first.CooldownSeconds, 3, 10);
        Assert.False(coordinator.TryAcquireTradeAction(
            now.AddSeconds(first.CooldownSeconds - 1), "OPEN", "early", side: side).Acquired);
        Assert.True(coordinator.TryAcquireTradeAction(
            now.AddSeconds(first.CooldownSeconds), "OPEN", "ready", side: side).Acquired);
    }

    [Fact]
    public void SameSideOpen_UsesConfiguredDatabaseRange()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateSameActionLockConfig(17, 17);
        var now = DateTime.UtcNow;

        var first = coordinator.TryAcquireTradeAction(now, "OPEN", "first", side: TradingPositionSide.Buy);

        Assert.True(first.Acquired);
        Assert.Equal(17, first.CooldownSeconds);
        Assert.False(coordinator.TryAcquireTradeAction(
            now.AddSeconds(16), "OPEN", "early", side: TradingPositionSide.Buy).Acquired);
        Assert.True(coordinator.TryAcquireTradeAction(
            now.AddSeconds(17), "OPEN", "ready", side: TradingPositionSide.Buy).Acquired);
    }

    [Theory]
    [InlineData(TradingPositionSide.Buy, TradingPositionSide.Sell)]
    [InlineData(TradingPositionSide.Sell, TradingPositionSide.Buy)]
    public void OppositeOpen_IsNotBlockedByRandomTransitionGate(
        TradingPositionSide firstSide,
        TradingPositionSide nextSide)
    {
        var coordinator = CreateCoordinator();
        var now = DateTime.UtcNow;
        Assert.True(coordinator.TryAcquireTradeAction(now, "OPEN", "first", side: firstSide).Acquired);

        Assert.True(coordinator.TryAcquireTradeAction(
            now.AddMilliseconds(1), "OPEN", "opposite", side: nextSide).Acquired);
        // opposite_side_lock_seconds remains enforced by CanOpenNewSlot from confirmed opens.
    }

    [Fact]
    public void CloseToClose_WaitsRandomRegardlessOfSide()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdatePostOpenLockConfig(0);
        var now = DateTime.UtcNow;
        AddLiveSlot(coordinator, "p1", TradingPositionSide.Buy, now.AddMinutes(-1));
        AddLiveSlot(coordinator, "p2", TradingPositionSide.Sell, now.AddMinutes(-1));

        var first = coordinator.TryAcquireTradeAction(
            now, "CLOSE", "first", side: TradingPositionSide.Buy, pairId: "p1");

        Assert.True(first.Acquired);
        Assert.InRange(first.CooldownSeconds, 3, 10);
        Assert.False(coordinator.TryAcquireTradeAction(
            now.AddSeconds(first.CooldownSeconds - 1), "CLOSE", "early",
            side: TradingPositionSide.Sell, pairId: "p2").Acquired);
        Assert.True(coordinator.TryAcquireTradeAction(
            now.AddSeconds(first.CooldownSeconds), "CLOSE", "ready",
            side: TradingPositionSide.Sell, pairId: "p2").Acquired);
    }

    [Fact]
    public void CloseToClose_UsesConfiguredDatabaseRange()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateSameActionLockConfig(19, 19);
        coordinator.UpdatePostOpenLockConfig(0);
        var now = DateTime.UtcNow;
        AddLiveSlot(coordinator, "p1", TradingPositionSide.Buy, now.AddMinutes(-1));
        AddLiveSlot(coordinator, "p2", TradingPositionSide.Sell, now.AddMinutes(-1));

        var first = coordinator.TryAcquireTradeAction(
            now, "CLOSE", "first", side: TradingPositionSide.Buy, pairId: "p1");

        Assert.True(first.Acquired);
        Assert.Equal(19, first.CooldownSeconds);
        Assert.False(coordinator.TryAcquireTradeAction(
            now.AddSeconds(18), "CLOSE", "early", side: TradingPositionSide.Sell, pairId: "p2").Acquired);
        Assert.True(coordinator.TryAcquireTradeAction(
            now.AddSeconds(19), "CLOSE", "ready", side: TradingPositionSide.Sell, pairId: "p2").Acquired);
    }

    [Theory]
    [InlineData(TradingPositionSide.Buy)]
    [InlineData(TradingPositionSide.Sell)]
    public void CloseToOpen_WaitsPostCloseLock(TradingPositionSide openSide)
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdatePostOpenLockConfig(0);
        coordinator.UpdatePostCloseLockConfig(17);
        var now = DateTime.UtcNow;
        AddLiveSlot(coordinator, "p1", TradingPositionSide.Buy, now.AddMinutes(-1));
        Assert.True(coordinator.TryAcquireTradeAction(
            now, "CLOSE", "close", side: TradingPositionSide.Buy, pairId: "p1").Acquired);

        Assert.False(coordinator.TryAcquireTradeAction(
            now.AddSeconds(16), "OPEN", "early", side: openSide).Acquired);
        Assert.True(coordinator.TryAcquireTradeAction(
            now.AddSeconds(17), "OPEN", "ready", side: openSide).Acquired);
    }

    [Fact]
    public void CloseToOpen_SamplesPostCloseRangeOncePerAutoClose()
    {
        var coordinator = CreateCoordinator(seed: 7);
        coordinator.UpdatePostOpenLockConfig(0, 0);
        coordinator.UpdatePostCloseLockConfig(20, 30);
        var now = DateTime.UtcNow;
        AddLiveSlot(coordinator, "p1", TradingPositionSide.Buy, now.AddMinutes(-1));

        Assert.True(coordinator.TryAcquireTradeAction(
            now, "CLOSE", "close", side: TradingPositionSide.Buy, pairId: "p1").Acquired);

        var selected = coordinator.LastSelectedPostCloseLockSeconds;
        Assert.InRange(selected, 20, 30);
        Assert.False(coordinator.TryAcquireTradeAction(
            now.AddSeconds(selected - 1), "OPEN", "early", side: TradingPositionSide.Buy).Acquired);
        Assert.True(coordinator.TryAcquireTradeAction(
            now.AddSeconds(selected), "OPEN", "ready", side: TradingPositionSide.Buy).Acquired);
    }

    [Fact]
    public void SosClose_BypassesGlobalCooldown_ThenResetsTransitionCooldowns()
    {
        var logger = new CaptureLogger();
        var coordinator = CreateCoordinator(logger: logger);
        coordinator.UpdateCooldownConfig(5, 5);
        coordinator.UpdateSameActionLockConfig(19, 19);
        coordinator.UpdatePostCloseLockConfig(23, 23);
        coordinator.UpdatePostOpenLockConfig(0, 0);
        var now = DateTime.UtcNow;
        coordinator.RecoverSlotsFromPersisted(new[]
        {
            new RecoveredSlotData(
                1, "p1", TradingPositionSide.Buy, TradingOpenMode.GapBuy,
                101, 201, now.AddMinutes(-1), 0),
            new RecoveredSlotData(
                2, "p2", TradingPositionSide.Sell, TradingOpenMode.GapSell,
                102, 202, now.AddMinutes(-1), 0)
        });
        coordinator.GetSlotByPairId("p1")!.UpdateSosMode(true, "A_OPEN_DISTANCE");
        Assert.Equal("GLOBAL_ACTION_COOLDOWN", coordinator.TryAcquireTradeAction(
            now, "OPEN", "open-during-global", side: TradingPositionSide.Buy).Reason);
        Assert.Equal("GLOBAL_ACTION_COOLDOWN", coordinator.TryAcquireTradeAction(
            now, "CLOSE", "normal-close-during-global",
            side: TradingPositionSide.Sell, pairId: "p2").Reason);
        coordinator.GetSlotByPairId("p2")!.UpdateSosMode(true, "TIME");

        var sosClose = coordinator.TryAcquireTradeAction(
            now, "CLOSE", "sos-close", side: TradingPositionSide.Buy, pairId: "p1");

        Assert.True(sosClose.Acquired);
        Assert.Equal(19, sosClose.CooldownSeconds);
        Assert.Equal(23, coordinator.LastSelectedPostCloseLockSeconds);
        Assert.Contains(logger.Messages, message =>
            message.Contains("[TRADE_GATE][SOS_RESET]")
            && message.Contains("sameActionSeconds=19")
            && message.Contains("postCloseSeconds=23"));
        var nextClose = coordinator.TryAcquireTradeAction(
            now.AddSeconds(18), "CLOSE", "next-close", side: TradingPositionSide.Sell, pairId: "p2");
        Assert.False(nextClose.Acquired);
        Assert.Equal("CLOSE_TO_CLOSE_RANDOM_LOCK", nextClose.Reason);
        var nextOpen = coordinator.TryAcquireTradeAction(
            now.AddSeconds(22), "OPEN", "next-open", side: TradingPositionSide.Buy);
        Assert.False(nextOpen.Acquired);
        Assert.Equal("POST_CLOSE_OPEN_LOCK", nextOpen.Reason);
        Assert.True(coordinator.TryAcquireTradeAction(
            now.AddSeconds(23), "OPEN", "open-ready", side: TradingPositionSide.Buy).Acquired);
    }

    [Fact]
    public void SosClose_DuringGlobalCooldown_StillRespectsPerSlotPostOpenLock()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(30, 30);
        coordinator.UpdatePostOpenLockConfig(60, 60);
        var now = DateTime.UtcNow;
        coordinator.RecoverSlotsFromPersisted(new[]
        {
            new RecoveredSlotData(
                1, "young-sos", TradingPositionSide.Buy, TradingOpenMode.GapBuy,
                101, 201, now.AddSeconds(-5), 0)
        });
        coordinator.GetSlotByPairId("young-sos")!.UpdateSosMode(true, "A_OPEN_DISTANCE");

        var result = coordinator.TryAcquireTradeAction(
            now, "CLOSE", "young-sos-close",
            side: TradingPositionSide.Buy, pairId: "young-sos");

        Assert.False(result.Acquired);
        Assert.Equal("PER_SLOT_POST_OPEN_LOCK", result.Reason);
    }

    [Fact]
    public void PerSlotPostOpenLock_DoesNotRefreshOlderSlot()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdatePostOpenLockConfig(60);
        var now = DateTime.UtcNow;
        AddLiveSlot(coordinator, "old", TradingPositionSide.Buy, now.AddSeconds(-60));
        AddLiveSlot(coordinator, "new", TradingPositionSide.Buy, now.AddSeconds(-5));

        Assert.True(coordinator.TryAcquireTradeAction(
            now, "CLOSE", "old", side: TradingPositionSide.Buy, pairId: "old").Acquired);
    }

    [Fact]
    public void PerSlotPostOpenLock_BlocksOnlyYoungTargetSlot()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdatePostOpenLockConfig(60);
        var now = DateTime.UtcNow;
        AddLiveSlot(coordinator, "young", TradingPositionSide.Sell, now.AddSeconds(-5));

        var result = coordinator.TryAcquireTradeAction(
            now, "CLOSE", "young", side: TradingPositionSide.Sell, pairId: "young");

        Assert.False(result.Acquired);
        Assert.Equal("PER_SLOT_POST_OPEN_LOCK", result.Reason);
    }

    [Fact]
    public void PostOpenRange_IsSampledAndStoredPerSlot()
    {
        var coordinator = CreateCoordinator(seed: 11);
        coordinator.UpdatePostOpenLockConfig(20, 30);
        var now = DateTime.UtcNow;
        AddLiveSlot(coordinator, "slot-random", TradingPositionSide.Buy, now);

        var slot = coordinator.GetSlotByPairId("slot-random");
        Assert.NotNull(slot);
        Assert.InRange(slot!.SelectedPostOpenLockSeconds, 20, 30);
    }

    [Theory]
    [InlineData(TradeActionOrigin.Manual)]
    [InlineData(TradeActionOrigin.Recovery)]
    public void NonAutoAction_BypassesAndDoesNotMutateAutoTransition(TradeActionOrigin origin)
    {
        var coordinator = CreateCoordinator();
        var now = DateTime.UtcNow;
        var auto = coordinator.TryAcquireTradeAction(now, "OPEN", "auto", side: TradingPositionSide.Buy);

        var nonAuto = coordinator.TryAcquireTradeAction(now.AddSeconds(1), "CLOSE", "non-auto", origin);

        Assert.True(nonAuto.Acquired);
        Assert.Equal(0, nonAuto.CooldownSeconds);
        Assert.False(coordinator.TryAcquireTradeAction(
            now.AddSeconds(1), "OPEN", "still-locked", side: TradingPositionSide.Buy).Acquired);
        Assert.InRange(auto.CooldownSeconds, 3, 10);
    }

    [Fact]
    public void NonAutoBarrier_BlocksAutoAndResumesImmediatelyAfterEnd()
    {
        var coordinator = CreateCoordinator();
        coordinator.BeginNonAutoCloseOperation("manual-pair");

        Assert.False(coordinator.TryAcquireTradeAction(
            DateTime.UtcNow, "OPEN", "auto", side: TradingPositionSide.Buy).Acquired);
        Assert.True(coordinator.TryAcquireTradeAction(
            DateTime.UtcNow, "CLOSE", "manual", TradeActionOrigin.Manual).Acquired);

        coordinator.EndNonAutoCloseOperation("manual-pair");
        Assert.True(coordinator.TryAcquireTradeAction(
            DateTime.UtcNow, "OPEN", "auto", side: TradingPositionSide.Buy).Acquired);
    }

    [Fact]
    public void SameSideOpenReservation_IsAtomicAcrossConcurrentCallers()
    {
        var coordinator = CreateCoordinator();
        var now = DateTime.UtcNow;
        Assert.True(coordinator.TryAcquireTradeAction(
            now.AddSeconds(-20), "OPEN", "seed", side: TradingPositionSide.Buy).Acquired);

        var results = Enumerable.Range(0, 32).AsParallel()
            .Select(i => coordinator.TryAcquireTradeAction(
                now, "OPEN", $"caller-{i}", side: TradingPositionSide.Buy))
            .ToArray();

        Assert.Single(results.Where(x => x.Acquired));
        Assert.Equal(31, results.Count(x => !x.Acquired));
    }
}
