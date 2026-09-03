using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

/// <summary>
/// 4 cột ngưỡng gap thường giữ nguyên dấu. Ngưỡng ÂM nới ngưỡng về phía trong, cùng hướng
/// với SOS Close: nhánh GapBuy dùng "gap >= threshold", nhánh GapSell dùng "gap <= -threshold".
/// Ngưỡng dương giữ nguyên hành vi cũ.
/// </summary>
public sealed class NegativeGapThresholdTests
{
    private static readonly GapStabilityConfig OpenStability =
        new(10, 0.50, 4.0, 3, 0.35, 0.40);

    private static readonly GapStabilityConfig CloseStability =
        new(10, 0.60, 4.0, 3, 0.45, 0.60);

    private static readonly DateTime Start =
        new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    // ---- CLOSE: ngưỡng âm ----

    [Fact]
    public void NegativeCloseThresholds_GapBuySlot_ClosesOnPositiveGapSell()
    {
        // close_confirm_gap_pts=-12, close_pts=-8 -> mọi gapSell <= +12, gap cuối <= +8.
        var engine = new CloseSignalEngine();
        var config = CloseConfig(confirm: -12, close: -8);

        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 0, gapSell: 12));
        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 1, gapSell: 10));
        var trigger = Close(engine, config, TradingOpenMode.GapBuy, 2, gapSell: 8);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalTriggerType.CloseByGapSell, trigger.TriggerType);
        Assert.Equal([12, 10, 8], trigger.SellGaps);
        Assert.Equal(CloseGapMode.Normal, trigger.CloseGapMode);
        Assert.Equal(12, trigger.EffectiveCloseConfirmGapPts);
        Assert.Equal(8, trigger.EffectiveCloseGapPts);
    }

    [Fact]
    public void NegativeCloseThresholds_GapSellSlot_ClosesOnNegativeGapBuy()
    {
        var engine = new CloseSignalEngine();
        var config = CloseConfig(confirm: -12, close: -8);

        Assert.Null(Close(engine, config, TradingOpenMode.GapSell, 0, gapBuy: -12));
        Assert.Null(Close(engine, config, TradingOpenMode.GapSell, 1, gapBuy: -10));
        var trigger = Close(engine, config, TradingOpenMode.GapSell, 2, gapBuy: -8);

        Assert.NotNull(trigger);
        Assert.Equal(GapSignalTriggerType.CloseByGapBuy, trigger.TriggerType);
        Assert.Equal([-12, -10, -8], trigger.BuyGaps);
        Assert.Equal(-12, trigger.EffectiveCloseConfirmGapPts);
        Assert.Equal(-8, trigger.EffectiveCloseGapPts);
    }

    [Fact]
    public void NegativeCloseThresholds_ResetWhenConfirmBreaks()
    {
        var engine = new CloseSignalEngine();
        var config = CloseConfig(confirm: -12, close: -8);

        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 0, gapSell: 12));
        // 13 > +12 -> reset Cycle và loại luôn mẫu này.
        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 1, gapSell: 13));
        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 2, gapSell: 10));
        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 3, gapSell: 8));
    }

    [Fact]
    public void NegativeCloseThresholds_FinalGapAboveTarget_DoesNotTrigger()
    {
        var engine = new CloseSignalEngine();
        var config = CloseConfig(confirm: -12, close: -8);

        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 0, gapSell: 12));
        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 1, gapSell: 10));
        // Đủ mẫu + đủ hold nên Cycle đã Stable, nhưng gap cuối 9 > +8 -> không phát signal.
        // Nhánh TIME KHÔNG reset ở đây: Cycle tiếp tục thu mẫu, mẫu sau vẫn có thể trigger.
        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 2, gapSell: 9));
    }

    [Fact]
    public void MixedSignCloseThresholds_ConfirmLooseCloseStrict()
    {
        // close_confirm_gap_pts=-12 (nới), close_pts=+3 (mẫu cuối vẫn phải về phía lời).
        var engine = new CloseSignalEngine();
        var config = CloseConfig(confirm: -12, close: 3);

        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 0, gapSell: -3));
        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 1, gapSell: -5));
        var trigger = Close(engine, config, TradingOpenMode.GapBuy, 2, gapSell: -4);

        Assert.NotNull(trigger);
        Assert.Equal(12, trigger.EffectiveCloseConfirmGapPts);
        Assert.Equal(-3, trigger.EffectiveCloseGapPts);
    }

    [Fact]
    public void NegativeCloseThresholds_SignFlipInsideCycle_IsRejectedByGuard()
    {
        // Guard cùng dấu: Cycle không được trộn hai dấu dù cả 3 mẫu đều qua confirm.
        var engine = new CloseSignalEngine();
        var config = CloseConfig(confirm: -12, close: -8);

        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 0, gapSell: -5));
        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 1, gapSell: 5));
        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 2, gapSell: -5));
        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 3, gapSell: 5));
    }

    [Fact]
    public void PositiveCloseThresholds_BehaviorUnchanged()
    {
        var engine = new CloseSignalEngine();
        var config = CloseConfig(confirm: 50, close: 100);

        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 0, gapSell: -100));
        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 1, gapSell: -120));
        var trigger = Close(engine, config, TradingOpenMode.GapBuy, 2, gapSell: -110);

        Assert.NotNull(trigger);
        Assert.Equal(-50, trigger.EffectiveCloseConfirmGapPts);
        Assert.Equal(-100, trigger.EffectiveCloseGapPts);
    }

    [Fact]
    public void SosCloseThresholds_BehaviorUnchanged()
    {
        // SOS luôn quy ngưỡng về hướng hồi vào trong, bất kể dấu trong DB.
        var engine = new CloseSignalEngine();
        var config = CloseConfig(confirm: 15, close: 8) with { CloseGapMode = CloseGapMode.Sos };

        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 0, gapSell: 15));
        Assert.Null(Close(engine, config, TradingOpenMode.GapBuy, 1, gapSell: 12));
        var trigger = Close(engine, config, TradingOpenMode.GapBuy, 2, gapSell: 8);

        Assert.NotNull(trigger);
        Assert.Equal(CloseGapMode.Sos, trigger.CloseGapMode);
        Assert.Equal(15, trigger.EffectiveCloseConfirmGapPts);
        Assert.Equal(8, trigger.EffectiveCloseGapPts);
    }

    [Fact]
    public void SosCloseThresholds_NegativeConfigGivesSameResultAsPositive()
    {
        var positive = new CloseSignalEngine();
        var negative = new CloseSignalEngine();
        var positiveConfig = CloseConfig(confirm: 15, close: 8) with { CloseGapMode = CloseGapMode.Sos };
        var negativeConfig = CloseConfig(confirm: -15, close: -8) with { CloseGapMode = CloseGapMode.Sos };

        Close(positive, positiveConfig, TradingOpenMode.GapBuy, 0, gapSell: 15);
        Close(positive, positiveConfig, TradingOpenMode.GapBuy, 1, gapSell: 12);
        var positiveTrigger = Close(positive, positiveConfig, TradingOpenMode.GapBuy, 2, gapSell: 8);

        Close(negative, negativeConfig, TradingOpenMode.GapBuy, 0, gapSell: 15);
        Close(negative, negativeConfig, TradingOpenMode.GapBuy, 1, gapSell: 12);
        var negativeTrigger = Close(negative, negativeConfig, TradingOpenMode.GapBuy, 2, gapSell: 8);

        Assert.NotNull(positiveTrigger);
        Assert.NotNull(negativeTrigger);
        Assert.Equal(
            positiveTrigger.EffectiveCloseConfirmGapPts,
            negativeTrigger.EffectiveCloseConfirmGapPts);
        Assert.Equal(
            positiveTrigger.EffectiveCloseGapPts,
            negativeTrigger.EffectiveCloseGapPts);
    }

    // ---- OPEN: ngưỡng âm ----

    [Fact]
    public void NegativeOpenThresholds_TriggersBuyOnNegativeGapBuy()
    {
        // confirm_gap_pts=-5, open_pts=-3 -> mọi gapBuy >= -5, gap cuối >= -3.
        var engine = new GapSignalConfirmationEngine();
        var config = OpenConfig(confirm: -5, open: -3);

        Assert.Empty(Open(engine, config, 0, gapBuy: -4));
        Assert.Empty(Open(engine, config, 1, gapBuy: -5));
        var results = Open(engine, config, 2, gapBuy: -3);

        var trigger = Assert.Single(results);
        Assert.Equal(GapSignalTriggerType.OpenByGapBuy, trigger.TriggerType);
        Assert.Equal([-4, -5, -3], trigger.BuyGaps);
    }

    [Fact]
    public void NegativeOpenThresholds_TriggersSellOnPositiveGapSell()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = OpenConfig(confirm: -5, open: -3);

        Assert.Empty(Open(engine, config, 0, gapSell: 4));
        Assert.Empty(Open(engine, config, 1, gapSell: 5));
        var results = Open(engine, config, 2, gapSell: 3);

        var trigger = Assert.Single(results);
        Assert.Equal(GapSignalTriggerType.OpenByGapSell, trigger.TriggerType);
        Assert.Equal([4, 5, 3], trigger.SellGaps);
    }

    [Fact]
    public void NegativeOpenThresholds_ResetWhenConfirmBreaks()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = OpenConfig(confirm: -5, open: -3);

        Assert.Empty(Open(engine, config, 0, gapBuy: -4));
        // -6 < -5 -> reset Cycle và loại luôn mẫu này.
        Assert.Empty(Open(engine, config, 1, gapBuy: -6));
        Assert.Empty(Open(engine, config, 2, gapBuy: -5));
        Assert.Empty(Open(engine, config, 3, gapBuy: -3));
    }

    [Fact]
    public void NegativeOpenThresholds_FinalGapBelowTarget_DoesNotTrigger()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = OpenConfig(confirm: -5, open: -3);

        Assert.Empty(Open(engine, config, 0, gapBuy: -4));
        Assert.Empty(Open(engine, config, 1, gapBuy: -5));
        // Đủ 3 mẫu nhưng gap cuối -4 < -3 -> không phát signal.
        Assert.Empty(Open(engine, config, 2, gapBuy: -4));
    }

    [Fact]
    public void NegativeOpenThresholds_CanEmitBuyAndSellInSameSnapshot()
    {
        // GapSell >= GapBuy luôn đúng, nên hai nhánh Open chỉ có thể cùng thoả khi ngưỡng <= 0.
        var engine = new GapSignalConfirmationEngine();
        var config = OpenConfig(confirm: -5, open: -3);

        Assert.Empty(Open(engine, config, 0, gapBuy: -3, gapSell: 2));
        Assert.Empty(Open(engine, config, 1, gapBuy: -3, gapSell: 2));
        var results = Open(engine, config, 2, gapBuy: -3, gapSell: 2);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.TriggerType == GapSignalTriggerType.OpenByGapBuy);
        Assert.Contains(results, r => r.TriggerType == GapSignalTriggerType.OpenByGapSell);
    }

    [Fact]
    public void PositiveOpenThresholds_CannotEmitBuyAndSellInSameSnapshot()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = OpenConfig(confirm: 5, open: 3);

        for (var second = 0; second < 5; second++)
        {
            Assert.Empty(Open(engine, config, second, gapBuy: -3, gapSell: 2));
        }
    }

    [Fact]
    public void PositiveOpenThresholds_BehaviorUnchanged()
    {
        var engine = new GapSignalConfirmationEngine();
        var config = OpenConfig(confirm: 5, open: 8);

        Assert.Empty(Open(engine, config, 0, gapBuy: 8));
        Assert.Empty(Open(engine, config, 1, gapBuy: 9));
        var results = Open(engine, config, 2, gapBuy: 8);

        var trigger = Assert.Single(results);
        Assert.Equal(GapSignalTriggerType.OpenByGapBuy, trigger.TriggerType);
        Assert.Equal([8, 9, 8], trigger.BuyGaps);
    }

    // ---- helpers ----

    private static GapSignalConfirmationConfig CloseConfig(int confirm, int close) =>
        new(
            ConfirmGapPts: 0,
            OpenPts: 0,
            CloseConfirmGapPts: confirm,
            ClosePts: close,
            CloseGapStability: CloseStability,
            SignalCycleSize: 3);

    private static GapSignalConfirmationConfig OpenConfig(int confirm, int open) =>
        new(
            ConfirmGapPts: confirm,
            OpenPts: open,
            OpenGapStability: OpenStability,
            SignalCycleSize: 3);

    private static GapSignalTriggerResult? Close(
        CloseSignalEngine engine,
        GapSignalConfirmationConfig config,
        TradingOpenMode openMode,
        int second,
        int? gapBuy = null,
        int? gapSell = null) =>
        engine.ProcessSnapshot(Snapshot(second, gapBuy, gapSell), config, openMode);

    private static IReadOnlyList<GapSignalTriggerResult> Open(
        GapSignalConfirmationEngine engine,
        GapSignalConfirmationConfig config,
        int second,
        int? gapBuy = null,
        int? gapSell = null) =>
        engine.ProcessSnapshot(Snapshot(second, gapBuy, gapSell), config);

    private static GapSignalSnapshot Snapshot(int second, int? gapBuy, int? gapSell) =>
        new(
            Start.AddSeconds(second),
            ExchangeABid: 1.1000m,
            ExchangeAAsk: 1.1001m,
            ExchangeBBid: 1.1101m,
            ExchangeBAsk: 1.1102m,
            gapBuy,
            gapSell,
            PointMultiplier: 100);
}
