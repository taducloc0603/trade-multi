using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.HoldConfirm;

/// <summary>
/// hold_confirm_mode=ticks nối vào engine: Open / Normal Close / SOS Close chờ N tick chân chậm,
/// <c>*_hold_confirm_ms</c> bị bỏ qua; TP vẫn theo ms.
/// </summary>
public sealed class HoldConfirmTicksEngineTests
{
    private static readonly DateTime Start = new(2026, 11, 15, 4, 0, 0, DateTimeKind.Utc);
    private static readonly GapStabilityConfig Stability = new(10, 0.50, 4.0, 3, 0.45, 0.60);

    private static GapSignalSnapshot Snap(int index, int? gapBuy, int? gapSell, bool tickA, bool tickB) =>
        new(
            Start.AddMilliseconds(index * 50),
            ExchangeABid: 2411.00m,
            ExchangeAAsk: 2411.20m,
            ExchangeBBid: 2411.50m,
            ExchangeBAsk: 2411.70m,
            gapBuy,
            gapSell,
            PointMultiplier: 100,
            IsNewTickA: tickA,
            IsNewTickB: tickB);

    private static GapSignalConfirmationConfig OpenConfig(string configId) =>
        new(
            ConfirmGapPts: 50,
            OpenPts: 100,
            HoldConfirmMs: 60_000, // bị bỏ qua ở mode ticks
            OpenGapStability: Stability,
            DiagnosticConfigId: configId,
            DiagnosticSymbol: "XAUUSD|XAUUSD",
            DiagnosticMaxGap: 700,
            SignalCycleSize: 3,
            HoldConfirmMode: HoldConfirmMode.Ticks,
            OpenHoldConfirmTicks: 2);

    private static GapSignalConfirmationConfig CloseConfig(string configId, CloseGapMode mode) =>
        new(
            ConfirmGapPts: 0,
            OpenPts: 0,
            CloseConfirmGapPts: 50,
            ClosePts: 100,
            CloseHoldConfirmMs: 60_000, // bị bỏ qua ở mode ticks (Normal + SOS)
            CloseGapMode: mode,
            CloseGapStability: Stability,
            DiagnosticConfigId: configId,
            DiagnosticSymbol: "XAUUSD|XAUUSD",
            DiagnosticMaxGap: 700,
            SignalCycleSize: 3,
            HoldConfirmMode: HoldConfirmMode.Ticks,
            CloseHoldConfirmTicks: 2);

    [Theory]
    [InlineData(GapSignalSide.Buy)]
    [InlineData(GapSignalSide.Sell)]
    public void Open_WaitsForSlowLegTicks_ThenTriggers(GapSignalSide side)
    {
        var logger = new TranscriptLogger();
        var engine = new GapSignalConfirmationEngine(logger);
        var config = OpenConfig($"HC-TICKS-OPEN-{side}");
        int? Buy(int gap) => side == GapSignalSide.Buy ? gap : 0;
        int? Sell(int gap) => side == GapSignalSide.Sell ? -gap : 0;

        // Mẫu đầu + 5 mẫu chân A chạy, chân B đứng giá: đủ mẫu nhưng tick chân chậm = 0.
        for (var i = 0; i < 6; i++)
        {
            Assert.Empty(engine.ProcessSnapshot(Snap(i, Buy(110 + (i % 2)), Sell(110 + (i % 2)), true, false), config));
        }

        Assert.Empty(engine.ProcessSnapshot(Snap(6, Buy(110), Sell(110), true, true), config));
        var trigger = Assert.Single(engine.ProcessSnapshot(Snap(7, Buy(111), Sell(111), false, true), config));

        Assert.Equal(side, trigger.PrimarySide);
        Assert.Equal(Start.AddMilliseconds(7 * 50), trigger.TriggeredAtUtc);
        var log = logger.Build();
        Assert.Contains("hold_ticks=0/2", log, StringComparison.Ordinal);
        Assert.Contains("hold_ticks=2/2", log, StringComparison.Ordinal);
        Assert.Contains("confirmation_mode=TICKS_AND_MIN_SAMPLES", log, StringComparison.Ordinal);
        Assert.DoesNotContain("confirmation_mode=TIME_AND_MIN_SAMPLES", log, StringComparison.Ordinal);
        Assert.DoesNotContain("/60000ms", log, StringComparison.Ordinal);
        var stable = Assert.Single(log.Split('\n'), line => line.Contains("[GAP_STABILITY][CYCLE_STABLE]", StringComparison.Ordinal));
        Assert.Contains("hold_confirm_ms=60000 hold_confirm_ticks=2 confirmation_mode=TICKS_AND_MIN_SAMPLES", stable, StringComparison.Ordinal);
        Assert.Contains("hold_confirm_ignored=true", stable, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_MsModeWithSameSeries_TriggersByTimeNotTicks()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = OpenConfig("HC-TICKS-OPEN-MS") with { HoldConfirmMode = HoldConfirmMode.Ms, HoldConfirmMs = 100 };

        Assert.Empty(engine.ProcessSnapshot(Snap(0, 110, 0, true, false), config));
        Assert.Empty(engine.ProcessSnapshot(Snap(1, 111, 0, true, false), config));
        var trigger = Assert.Single(engine.ProcessSnapshot(Snap(2, 110, 0, true, false), config));

        Assert.Equal(GapSignalSide.Buy, trigger.PrimarySide);
    }

    [Fact]
    public void NormalClose_WaitsForSlowLegTicks_ThenTriggers()
    {
        var logger = new TranscriptLogger();
        var engine = new CloseSignalEngine(logger);
        var config = CloseConfig("HC-TICKS-NORMAL", CloseGapMode.Normal);

        // Vị thế Buy đóng theo GapSell <= -100.
        for (var i = 0; i < 6; i++)
        {
            Assert.Null(engine.ProcessSnapshot(Snap(i, 0, -110 - (i % 2), false, true), config, TradingOpenMode.GapBuy));
        }

        Assert.Null(engine.ProcessSnapshot(Snap(6, 0, -110, true, true), config, TradingOpenMode.GapBuy));
        var trigger = engine.ProcessSnapshot(Snap(7, 0, -111, true, false), config, TradingOpenMode.GapBuy);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalTriggerType.CloseByGapSell, trigger!.TriggerType);
        Assert.Equal(CloseGapMode.Normal, trigger.CloseGapMode);
        var log = logger.Build();
        Assert.Contains("[NORMAL_CLOSE_CYCLE][TRIGGERED]", log, StringComparison.Ordinal);
        Assert.Contains("hold_ticks=2/2", log, StringComparison.Ordinal);
        Assert.Contains("confirmation_mode=TICKS_AND_MIN_SAMPLES", log, StringComparison.Ordinal);
    }

    [Fact]
    public void SosClose_WaitsForSlowLegTicks_ThenTriggers()
    {
        var logger = new TranscriptLogger();
        var engine = new CloseSignalEngine(logger);
        var config = CloseConfig("HC-TICKS-SOS", CloseGapMode.Sos);

        // Vị thế Sell đóng theo GapBuy; SOS quy ngưỡng về hướng hồi vào trong (gap >= -100, confirm >= -50).
        for (var i = 0; i < 6; i++)
        {
            Assert.Null(engine.ProcessSnapshot(Snap(i, -40 - (i % 2), 0, true, false), config, TradingOpenMode.GapSell));
        }

        Assert.Null(engine.ProcessSnapshot(Snap(6, -40, 0, true, true), config, TradingOpenMode.GapSell));
        var trigger = engine.ProcessSnapshot(Snap(7, -41, 0, false, true), config, TradingOpenMode.GapSell);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalTriggerType.CloseByGapBuy, trigger!.TriggerType);
        Assert.Equal(CloseGapMode.Sos, trigger.CloseGapMode);
        var log = logger.Build();
        Assert.Contains("[SOS_CLOSE_CYCLE][TRIGGERED]", log, StringComparison.Ordinal);
        Assert.Contains("hold_ticks=2/2", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Tp_StaysOnMs_InTicksMode()
    {
        var logger = new TranscriptLogger();
        var engine = new CloseSignalEngine(logger);
        var config = CloseConfig("HC-TICKS-TP", CloseGapMode.Normal) with
        {
            CloseHoldConfirmMs = 100,
            CloseConfirmTpProfit = 2,
            CloseTpProfit = 5,
        };

        // Không có tick nào ở cả hai chân: TP vẫn trigger khi đủ 100 ms.
        Assert.Null(engine.ProcessSnapshot(Snap(0, 0, 0, false, false), config, TradingOpenMode.GapBuy, 6));
        Assert.Null(engine.ProcessSnapshot(Snap(1, 0, 0, false, false), config, TradingOpenMode.GapBuy, 6));
        var trigger = engine.ProcessSnapshot(Snap(2, 0, 0, false, false), config, TradingOpenMode.GapBuy, 6);

        Assert.NotNull(trigger);
        Assert.Equal(CloseSignalReason.Tp, trigger!.CloseReason);
        var tpLines = logger.Build().Split('\n').Where(line => line.Contains("[TP_CYCLE]", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(tpLines);
        Assert.All(tpLines, line =>
        {
            Assert.Contains("/100ms", line, StringComparison.Ordinal);
            Assert.Contains("confirmation_mode=TIME_AND_MIN_SAMPLES", line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Coordinator_EndToEnd_OpenWaitsForLegBToMove()
    {
        var coordinator = new PortfolioCoordinator(new GapSignalConfirmationEngine(), new CloseSignalEngineFactory());
        var config = OpenConfig("HC-TICKS-COORD") with { OpenHoldConfirmTicks = 1 };

        // Giá A đổi mỗi snapshot, B đứng yên: tracker của coordinator tự đánh dấu, KHÔNG dùng cờ đầu vào.
        PortfolioSnapshotResult result = PortfolioSnapshotResult.Empty;
        for (var i = 0; i < 8; i++)
        {
            result = coordinator.ProcessSnapshot(Quote(i, aBid: 2411.00m + (i * 0.01m), bBid: 2411.50m), config);
            Assert.Null(result.OpenTrigger);
        }

        result = coordinator.ProcessSnapshot(Quote(8, aBid: 2411.08m, bBid: 2411.55m), config);

        Assert.NotNull(result.OpenTrigger);
        Assert.Equal(GapSignalSide.Buy, result.OpenTrigger!.PrimarySide);
    }

    private static GapSignalSnapshot Quote(int index, decimal aBid, decimal bBid) =>
        new(
            Start.AddMilliseconds(index * 50),
            ExchangeABid: aBid,
            ExchangeAAsk: aBid + 0.20m,
            ExchangeBBid: bBid,
            ExchangeBAsk: bBid + 0.20m,
            GapBuy: 110,
            GapSell: -150,
            PointMultiplier: 100,
            // Cờ đầu vào cố ý sai: coordinator phải tự tính lại.
            IsNewTickA: true,
            IsNewTickB: true);
}
