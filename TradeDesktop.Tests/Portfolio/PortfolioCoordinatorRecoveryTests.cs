using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Phase 5 — RecoverSlotsFromPersisted: restore slots from persisted snapshot.
public sealed class PortfolioCoordinatorRecoveryTests
{
    private static PortfolioCoordinator CreateCoordinator(int seed = 42)
        => new(
            new GapSignalConfirmationEngine(),
            new CloseSignalEngineFactory(),
            logger: null,
            random: new Random(seed));

    private static RecoveredSlotData Persisted(
        int slotId, string pairId,
        TradingPositionSide side = TradingPositionSide.Buy,
        TradingOpenMode mode = TradingOpenMode.GapBuy,
        ulong ticketA = 100, ulong ticketB = 200,
        DateTime? openConfirmedAtUtc = null,
        int holdingSeconds = 30)
        => new(slotId, pairId, side, mode, ticketA, ticketB,
            openConfirmedAtUtc ?? new DateTime(2026, 5, 21, 14, 30, 0, DateTimeKind.Utc),
            holdingSeconds);

    [Fact]
    public void RecoverSlotsFromPersisted_RestoresAllSlots()
    {
        var coordinator = CreateCoordinator();
        var slots = new[]
        {
            Persisted(1, "AUTO-0001-1", ticketA: 100, ticketB: 200),
            Persisted(2, "AUTO-0002-2", ticketA: 101, ticketB: 201,
                side: TradingPositionSide.Sell, mode: TradingOpenMode.GapSell),
        };

        coordinator.RecoverSlotsFromPersisted(slots);

        Assert.Equal(2, coordinator.LiveCount);
        Assert.Equal(1, coordinator.LiveBuyCount);
        Assert.Equal(1, coordinator.LiveSellCount);
        Assert.NotNull(coordinator.GetSlotByPairId("AUTO-0001-1"));
        Assert.NotNull(coordinator.GetSlotByTicket(101));
    }

    [Fact]
    public void RecoverSlotsFromPersisted_SetsNextSlotIdCorrectly()
    {
        var coordinator = CreateCoordinator();
        coordinator.RecoverSlotsFromPersisted(new[]
        {
            Persisted(3, "AUTO-0003-1"),
            Persisted(7, "AUTO-0007-2"),
        });

        // Subsequent allocations must use SlotId > max persisted.
        coordinator.UpdateQuotaConfig(maxTotal: 99, maxBuy: 99, maxSell: 99);
        var trigger = new GapSignalTriggerResult(
            true, GapSignalAction.Open, GapSignalTriggerType.OpenByGapBuy, GapSignalSide.Buy,
            Array.Empty<int>(), Array.Empty<int>(), null, null,
            new DateTime(2026, 5, 21, 15, 0, 0, DateTimeKind.Utc),
            null, null, null, null, null, null, null, null, 1);
        var newSlot = coordinator.AllocatePendingOpenSlot("p-new", trigger);

        Assert.NotNull(newSlot);
        Assert.True(newSlot!.SlotId > 7);
    }

    [Fact]
    public void RecoverSlotsFromPersisted_KicksStartupCooldown()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 5, maxSec: 5);

        var before = DateTime.UtcNow;
        coordinator.RecoverSlotsFromPersisted(new[] { Persisted(1, "p1") });
        var after = DateTime.UtcNow;

        Assert.NotNull(coordinator.GlobalActionLockUntilUtc);
        var lockUntil = coordinator.GlobalActionLockUntilUtc!.Value;
        Assert.InRange(lockUntil, before.AddSeconds(5), after.AddSeconds(5));
    }

    [Fact]
    public void RecoverSlotsFromPersisted_RestoresLastOpenSide()
    {
        var coordinator = CreateCoordinator();
        var earlier = new DateTime(2026, 5, 21, 14, 0, 0, DateTimeKind.Utc);
        var later = new DateTime(2026, 5, 21, 14, 30, 0, DateTimeKind.Utc);

        coordinator.RecoverSlotsFromPersisted(new[]
        {
            Persisted(1, "p1", side: TradingPositionSide.Buy, openConfirmedAtUtc: earlier),
            Persisted(2, "p2", side: TradingPositionSide.Sell,
                mode: TradingOpenMode.GapSell, ticketA: 300, ticketB: 400,
                openConfirmedAtUtc: later),
        });

        // Latest = Sell at `later`.
        Assert.Equal(TradingPositionSide.Sell, coordinator.LastOpenConfirmedSide);
        Assert.Equal(later, coordinator.LastOpenConfirmedAtUtc);
    }

    [Fact]
    public void RecoverSlotsFromPersisted_EmptyList_NoSlots()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);

        coordinator.RecoverSlotsFromPersisted(Array.Empty<RecoveredSlotData>());

        Assert.Equal(0, coordinator.LiveCount);
        Assert.Equal(TradingPositionSide.None, coordinator.LastOpenConfirmedSide);
    }

    [Fact]
    public void RecoverSlotsFromPersisted_ClearsPreviousState()
    {
        var coordinator = CreateCoordinator();
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);

        // Pre-populate with allocated slot.
        var trigger = new GapSignalTriggerResult(
            true, GapSignalAction.Open, GapSignalTriggerType.OpenByGapBuy, GapSignalSide.Buy,
            Array.Empty<int>(), Array.Empty<int>(), null, null, DateTime.UtcNow,
            null, null, null, null, null, null, null, null, 1);
        coordinator.AllocatePendingOpenSlot("p-pre", trigger);

        // Recover replaces everything.
        coordinator.RecoverSlotsFromPersisted(new[] { Persisted(99, "p-recovered") });

        Assert.Null(coordinator.GetSlotByPairId("p-pre"));
        Assert.NotNull(coordinator.GetSlotByPairId("p-recovered"));
    }
}

// Phase 5 — JSON round-trip for slot persistence.
public sealed class SlotPersistenceTests
{
    [Fact]
    public void Serialize_LiveSlotsOnly_SkipsOthers()
    {
        var slots = new[]
        {
            CreateSlot(1, "p1", PositionSlotStatus.Live, ticketA: 100, ticketB: 200),
            CreateSlot(2, "p2", PositionSlotStatus.PendingOpen, ticketA: null, ticketB: null),
            CreateSlot(3, "p3", PositionSlotStatus.Closed, ticketA: 300, ticketB: 400),
        };

        var json = SlotPersistence.Serialize(slots);

        Assert.Contains("\"slotId\":1", json);
        Assert.DoesNotContain("\"slotId\":2", json);
        Assert.DoesNotContain("\"slotId\":3", json);
    }

    [Fact]
    public void Deserialize_RoundTripsCorrectly()
    {
        var slot = CreateSlot(1, "AUTO-0001-1234", PositionSlotStatus.Live, ticketA: 555, ticketB: 666);

        var json = SlotPersistence.Serialize(new[] { slot });
        var recovered = SlotPersistence.Deserialize(json);

        Assert.Single(recovered);
        Assert.Equal(1, recovered[0].SlotId);
        Assert.Equal("AUTO-0001-1234", recovered[0].PairId);
        Assert.Equal(TradingPositionSide.Buy, recovered[0].Side);
        Assert.Equal(TradingOpenMode.GapBuy, recovered[0].OpenMode);
        Assert.Equal((ulong)555, recovered[0].TicketA);
        Assert.Equal((ulong)666, recovered[0].TicketB);
    }

    [Fact]
    public void Deserialize_MalformedJson_ReturnsEmpty()
    {
        var result = SlotPersistence.Deserialize("not-json{");
        Assert.Empty(result);
    }

    [Fact]
    public void Deserialize_EmptyString_ReturnsEmpty()
    {
        Assert.Empty(SlotPersistence.Deserialize(""));
        Assert.Empty(SlotPersistence.Deserialize("[]"));
    }

    private static PositionSlot CreateSlot(
        int slotId, string pairId, PositionSlotStatus status,
        ulong? ticketA, ulong? ticketB)
    {
        var s = new PositionSlot(slotId, pairId, new CloseSignalEngine());
        if (status == PositionSlotStatus.PendingOpen)
        {
            s.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, DateTime.UtcNow, 30);
            return s;
        }

        s.MarkOpenTriggered(TradingPositionSide.Buy, TradingOpenMode.GapBuy, DateTime.UtcNow, 30);
        s.MarkOpenConfirmed(ticketA ?? 0, ticketB ?? 0, DateTime.UtcNow);
        if (status == PositionSlotStatus.Closed)
        {
            s.MarkCloseTriggered(DateTime.UtcNow);
            s.MarkCloseConfirmed(DateTime.UtcNow);
        }
        return s;
    }
}
