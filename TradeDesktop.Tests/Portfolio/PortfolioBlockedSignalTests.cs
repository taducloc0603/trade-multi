using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// CHỈ LOGGING: ProcessSnapshot phải surface các signal đã xác nhận nhưng bị vứt bỏ
// (quota, opposite-side lock, post-close lock, min-profit, mất quyền close) để caller ghi
// được vào signal-outcome log — mà KHÔNG đổi bất kỳ quyết định giao dịch nào.
public sealed class PortfolioBlockedSignalTests
{
    private sealed class ScriptedOpenSignalEngine : IOpenSignalEngine
    {
        public IReadOnlyList<GapSignalTriggerResult> NextResults { get; set; } = [];

        public IReadOnlyList<GapSignalTriggerResult> ProcessSnapshot(
            GapSignalSnapshot snapshot,
            GapSignalConfirmationConfig config)
            => NextResults;

        public void Reset() { }
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

    private static readonly DateTime OpenTime = new(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);

    private static GapSignalTriggerResult OpenTrigger(GapSignalSide side, string signalId = "sig-open")
        => new(true, GapSignalAction.Open,
            side == GapSignalSide.Buy ? GapSignalTriggerType.OpenByGapBuy : GapSignalTriggerType.OpenByGapSell,
            side,
            Array.Empty<int>(), Array.Empty<int>(), null, null,
            OpenTime, null, null, null, null, null, null, null, null, 1,
            DiagnosticSignalId: signalId);

    private static GapSignalTriggerResult CloseTrigger(string signalId = "sig-close")
        => new(true, GapSignalAction.Close, GapSignalTriggerType.CloseByGapSell, GapSignalSide.Buy,
            Array.Empty<int>(), Array.Empty<int>(), null, null,
            OpenTime.AddHours(1), null, null, null, null, null, null, null, null, 1,
            DiagnosticSignalId: signalId);

    private static GapSignalSnapshot Snapshot(DateTime ts)
        => new(ts, 100m, 100.5m, 100m, 100.5m, GapBuy: null, GapSell: null, PointMultiplier: 1);

    private static GapSignalConfirmationConfig Config()
        => new(ConfirmGapPts: 5, OpenPts: 8, HoldConfirmMs: 100,
               CloseConfirmGapPts: 5, ClosePts: 8, CloseHoldConfirmMs: 100,
               StartTimeHold: 1, EndTimeHold: 1);

    private static PortfolioCoordinator NewCoordinator(
        ScriptedFactory factory,
        IOpenSignalEngine? openEngine = null)
    {
        var coordinator = new PortfolioCoordinator(
            openEngine ?? new GapSignalConfirmationEngine(), factory, logger: null, random: new Random(42));
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 4, maxSell: 4);
        coordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);
        return coordinator;
    }

    private static void OpenLiveSlot(PortfolioCoordinator coordinator, string pairId, int index, GapSignalSide side)
    {
        coordinator.AllocatePendingOpenSlot(pairId, OpenTrigger(side));
        coordinator.MarkSlotOpenConfirmed(pairId, (ulong)(100 + index), (ulong)(200 + index),
            OpenTime.AddSeconds(-10));
    }

    [Fact]
    public void NoBlock_ReturnsNullBlockedSignals()
    {
        var factory = new ScriptedFactory();
        var coordinator = NewCoordinator(factory);

        var result = coordinator.ProcessSnapshot(Snapshot(OpenTime.AddSeconds(20)), Config());

        // Tick sạch không được allocate list nào.
        Assert.Null(result.BlockedSignals);
    }

    [Fact]
    public void MinProfitBlock_ReturnsBlockedSignal()
    {
        var factory = new ScriptedFactory();
        var coordinator = NewCoordinator(factory);
        coordinator.UpdateMinProfitToCloseConfig(50d);

        OpenLiveSlot(coordinator, "p1", 0, GapSignalSide.Buy);
        coordinator.UpdateProfit(100, 1.0);   // xa ngưỡng 50 -> bị MIN_PROFIT chặn
        coordinator.UpdateProfit(200, 0.0);
        factory.Created[0].NextResult = CloseTrigger("sig-minprofit");

        var result = coordinator.ProcessSnapshot(Snapshot(OpenTime.AddSeconds(20)), Config());

        Assert.Null(result.CloseTrigger);
        var blocked = Assert.Single(result.BlockedSignals!);
        Assert.Equal("MIN_PROFIT_NOT_REACHED", blocked.BlockReason);
        Assert.Equal("sig-minprofit", blocked.Trigger.DiagnosticSignalId);
        Assert.Equal("p1", blocked.CloseTargetSlot!.PairId);
    }

    [Fact]
    public void CanOpenNewSlotFail_ReturnsBlockedSignal_WithRawBlockReason()
    {
        var factory = new ScriptedFactory();
        var openEngine = new ScriptedOpenSignalEngine();
        var coordinator = NewCoordinator(factory, openEngine);
        // Quota Buy đầy nhưng quota tổng còn chỗ -> engine vẫn chạy, trigger bị CanOpenNewSlot chặn.
        coordinator.UpdateQuotaConfig(maxTotal: 7, maxBuy: 1, maxSell: 4);

        OpenLiveSlot(coordinator, "p1", 0, GapSignalSide.Buy);
        openEngine.NextResults = [OpenTrigger(GapSignalSide.Buy, "sig-quota")];

        var result = coordinator.ProcessSnapshot(Snapshot(OpenTime.AddSeconds(20)), Config());

        Assert.Null(result.OpenTrigger);
        var blocked = Assert.Single(result.BlockedSignals!);
        Assert.Equal("sig-quota", blocked.Trigger.DiagnosticSignalId);
        Assert.Null(blocked.CloseTargetSlot);
        // Chuỗi thô, caller mới cắt thành token.
        Assert.StartsWith("QUOTA_BUY_FULL", blocked.BlockReason, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulOpenTrigger_CarriesConcurrentBlockedSignalOfOtherSide()
    {
        var factory = new ScriptedFactory();
        var openEngine = new ScriptedOpenSignalEngine();
        var coordinator = NewCoordinator(factory, openEngine);

        // Đã có slot Buy live => Sell bị opposite-side lock chặn, Buy cùng chiều vẫn qua.
        OpenLiveSlot(coordinator, "p1", 0, GapSignalSide.Buy);
        openEngine.NextResults =
        [
            OpenTrigger(GapSignalSide.Sell, "sig-blocked"),
            OpenTrigger(GapSignalSide.Buy, "sig-ok")
        ];

        var result = coordinator.ProcessSnapshot(Snapshot(OpenTime.AddSeconds(20)), Config());

        Assert.NotNull(result.OpenTrigger);
        Assert.Equal("sig-ok", result.OpenTrigger!.DiagnosticSignalId);
        var blocked = Assert.Single(result.BlockedSignals!);
        Assert.Equal("sig-blocked", blocked.Trigger.DiagnosticSignalId);
        Assert.StartsWith("OPPOSITE_SIDE_LOCK", blocked.BlockReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Việc gom signal bị chặn là CHỈ LOGGING: không được đổi slot nào được chọn để close.
    /// </summary>
    [Fact]
    public void BlockedSignalCollection_DoesNotChangeTriggerSelection()
    {
        var factory = new ScriptedFactory();
        var coordinator = NewCoordinator(factory);

        for (var i = 0; i < 3; i++)
        {
            OpenLiveSlot(coordinator, $"p{i + 1}", i, GapSignalSide.Buy);
        }

        coordinator.UpdateProfit(100, 1.5);
        coordinator.UpdateProfit(200, 0.0);
        coordinator.UpdateProfit(101, 3.2);   // winner theo Rule D
        coordinator.UpdateProfit(201, 0.0);
        coordinator.UpdateProfit(102, 2.1);
        coordinator.UpdateProfit(202, 0.0);

        foreach (var engine in factory.Created)
        {
            engine.NextResult = CloseTrigger();
        }

        var result = coordinator.ProcessSnapshot(Snapshot(OpenTime.AddSeconds(20)), Config());

        Assert.Equal("p2", result.CloseTargetSlot!.PairId);
        Assert.Equal(3.2, result.CloseTargetSlot.LastProfitSnapshot);
        Assert.Null(result.BlockedSignals);
    }
}
