using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

/// <summary>
/// Khóa các chuỗi nghiệp vụ trong CAC-TRUONG-HOP-GAP-VA-KET-QUA.docx.
/// Mỗi tick cách nhau 1 giây; Hold được đặt để chỉ đánh giá Stable ở mẫu cuối
/// trừ khi test cần quan sát chuyển Cycle sớm hơn.
/// </summary>
public sealed class GapStabilityDocumentScenariosTests
{
    private static readonly GapStabilityConfig Config =
        new(10, 0.50, 4.0, 3, 0.35, 0.40);

    private static readonly DateTime Start =
        new(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CaseA_SmallStableGap_IsOneStableCycle()
    {
        var result = Feed([2, 5, 6, 5, 2, 7, 4], holdMs: 6000);

        Assert.Empty(result.CompletedCycles);
        Assert.Equal(GapCycleStatus.Stable, result.Current.Status);
        Assert.Equal([2, 5, 6, 5, 2, 7, 4], result.Current.Gaps);
        Assert.Equal(5d, result.Current.Center);
        Assert.Equal(1d, result.Current.Mad);
        Assert.Equal(10d, result.Current.Tolerance);
        Assert.Equal(0.20d, result.Current.Dispersion);
    }

    [Fact]
    public void CaseB_LargeStableGap_IsOneStableCycle()
    {
        var result = Feed([100, 120, 150, 110, 160], holdMs: 4000);

        Assert.Empty(result.CompletedCycles);
        Assert.Equal(GapCycleStatus.Stable, result.Current.Status);
        Assert.Equal(120d, result.Current.Center);
        Assert.Equal(20d, result.Current.Mad);
        Assert.Equal(80d, result.Current.Tolerance);
        Assert.Equal(20d / 120d, result.Current.Dispersion!.Value, precision: 12);
    }

    [Fact]
    public void CaseC_Gap20AfterSmallRegion_StartsNewCycle()
    {
        int[] gaps = [2, 4, 2, 6, 1, 7, 3, 20];

        var result = Feed(gaps, holdMs: 10000);

        Assert.Single(result.CompletedCycles);
        Assert.Equal([2, 4, 2, 6, 1, 7, 3], result.CompletedCycles[0].Gaps);
        Assert.Equal([20], result.Current.Gaps);
        Assert.Equal(GapCycleStatus.Collecting, result.Current.Status);
    }

    [Fact]
    public void CaseD_GapReturnsTo2_AfterSpike20_StartsThirdCycle()
    {
        int[] gaps = [2, 4, 2, 6, 1, 7, 3, 20, 2];

        var result = Feed(gaps, holdMs: 10000);

        Assert.Equal(2, result.CompletedCycles.Count);
        Assert.Equal([2, 4, 2, 6, 1, 7, 3], result.CompletedCycles[0].Gaps);
        Assert.Equal([20], result.CompletedCycles[1].Gaps);
        Assert.Equal([2], result.Current.Gaps);
    }

    [Fact]
    public void CaseE_RealTransitionFromSmallToLarge_ProducesTwoStableRegions()
    {
        var state = new GapCycleState();
        int[] gaps = [2, 4, 3, 5, 100, 110, 120, 115];
        var completed = new List<GapCycleSnapshot>();
        GapCycleUpdateResult? last = null;

        for (var index = 0; index < gaps.Length; index++)
        {
            // Hold 3 giây: Cycle nhỏ đủ thời gian trước 100; Cycle lớn đủ ở 115.
            last = Process(state, gaps[index], index, holdMs: 3000);
            if (last.CompletedCycle is not null)
            {
                completed.Add(last.CompletedCycle);
            }
        }

        Assert.NotNull(last);
        Assert.Single(completed);
        Assert.Equal(GapCycleStatus.Stable, completed[0].Status);
        Assert.Equal([2, 4, 3, 5], completed[0].Gaps);
        Assert.Equal(GapCycleStatus.Stable, last.CurrentCycle.Status);
        Assert.Equal([100, 110, 120, 115], last.CurrentCycle.Gaps);
    }

    [Fact]
    public void CaseF_Gap400AfterLargeRegion_StartsNewCycle()
    {
        int[] gaps = [100, 120, 150, 110, 160, 400];

        var result = Feed(gaps, holdMs: 10000);

        Assert.Single(result.CompletedCycles);
        Assert.Equal([100, 120, 150, 110, 160], result.CompletedCycles[0].Gaps);
        Assert.Equal([400], result.Current.Gaps);
    }

    [Fact]
    public void CaseF2_GapReturnsTo130_After400_StartsThirdCycle()
    {
        int[] gaps = [100, 120, 150, 110, 160, 400, 130];

        var result = Feed(gaps, holdMs: 10000);

        Assert.Equal(2, result.CompletedCycles.Count);
        Assert.Equal([100, 120, 150, 110, 160], result.CompletedCycles[0].Gaps);
        Assert.Equal([400], result.CompletedCycles[1].Gaps);
        Assert.Equal([130], result.Current.Gaps);
    }

    [Fact]
    public void CaseG_ContinuouslyIncreasingGap_IsUnstableByDrift()
    {
        var result = Feed([20, 30, 40, 50, 60, 70], holdMs: 5000);

        Assert.Single(result.CompletedCycles);
        var unstable = result.CompletedCycles[0];
        Assert.Equal(GapCycleStatus.Unstable, unstable.Status);
        Assert.Equal([20, 30, 40, 50, 60, 70], unstable.Gaps);
        Assert.Equal(45d, unstable.Center);
        Assert.Equal(30d, unstable.EarlyCenter);
        Assert.Equal(60d, unstable.LateCenter);
        Assert.Equal(2d / 3d, unstable.Drift!.Value, precision: 12);
        Assert.Contains("Drift", unstable.Reason);
        Assert.Equal([70], result.Current.Gaps);
    }

    [Fact]
    public void CaseH_WideAlternatingGap_IsSplitByToleranceBeforeDispersionGate()
    {
        // Quyết định nghiệp vụ đã chốt: ưu tiên Delta/Tolerance. Mẫu 250 không
        // thuộc Cycle bắt đầu tại 100 vì Delta=150 > Tolerance=50. Do đó mỗi
        // mẫu tạo một Cycle đơn và hệ thống không giao dịch vì thiếu mẫu.
        var result = Feed([100, 250, 80, 300, 70, 280], holdMs: 10000);

        Assert.Equal(5, result.CompletedCycles.Count);
        Assert.All(result.CompletedCycles, cycle => Assert.Equal(1, cycle.SampleCount));
        Assert.Equal([280], result.Current.Gaps);

        var wholeSeries = GapStabilityCalculator.Calculate(
            [100, 250, 80, 300, 70, 280],
            Config);
        Assert.Equal(175d, wholeSeries.Center);
        Assert.Equal(100d, wholeSeries.Mad);
        Assert.Equal(100d / 175d, wholeSeries.Dispersion, precision: 12);
        Assert.True(wholeSeries.Dispersion > Config.MaxDispersion);
    }

    [Fact]
    public void CaseI_GapAboveLimit_IsRejectedAndNext125StartsFreshCycle()
    {
        var state = new GapCycleState();
        foreach (var (gap, index) in new[] { 100, 120, 130 }.Select((gap, index) => (gap, index)))
        {
            Process(state, gap, index, holdMs: 10000, limitMaxGap: 500);
        }

        var rejected = Process(state, 550, 3, holdMs: 10000, limitMaxGap: 500);
        var next = Process(state, 125, 4, holdMs: 10000, limitMaxGap: 500);

        Assert.Equal(GapCycleTransition.Rejected, rejected.Transition);
        Assert.Empty(rejected.CurrentCycle.Gaps);
        Assert.Equal([100, 120, 130], rejected.CompletedCycle!.Gaps);
        Assert.Equal(GapCycleTransition.Started, next.Transition);
        Assert.Equal([125], next.CurrentCycle.Gaps);
    }

    [Fact]
    public void CaseJ_NumericallyStableGap_WithFrozenPrices_IsRejectedByGuard()
    {
        var cycle = Feed([100, 100, 100, 100], holdMs: 3000);
        Assert.Equal(GapCycleStatus.Stable, cycle.Current.Status);

        var triggeredAt = Start.AddSeconds(3);
        var trigger = new GapSignalTriggerResult(
            Triggered: true,
            Action: GapSignalAction.Open,
            TriggerType: GapSignalTriggerType.OpenByGapBuy,
            PrimarySide: GapSignalSide.Buy,
            BuyGaps: cycle.Current.Gaps,
            SellGaps: [],
            LastBuyGap: 100,
            LastSellGap: null,
            TriggeredAtUtc: triggeredAt,
            LastABid: 1.1000m,
            LastAAsk: 1.1001m,
            LastBBid: 1.1101m,
            LastBAsk: 1.1102m,
            GapBuySourceBBid: 1.1101m,
            GapBuySourceAAsk: 1.1001m,
            GapSellSourceBAsk: null,
            GapSellSourceABid: null,
            PointMultiplier: 100);
        var priceHistory = new Queue<SignalEntryGuard.PriceHistoryEntry>(
            Enumerable.Range(0, 4).Select(index =>
                new SignalEntryGuard.PriceHistoryEntry(
                    Start.AddSeconds(index),
                    BidA: 1.1000m,
                    AskA: 1.1001m,
                    BidB: 1.1101m,
                    AskB: 1.1102m)));

        var guard = SignalEntryGuard.Check(
            trigger,
            metrics: null,
            new SignalEntryGuard.GuardConfig(0, 0, 0, 100),
            priceHistory,
            priceFreezeMs: 3000);

        Assert.False(guard.CanTrade);
        Assert.Contains("đóng băng", guard.SkipReason);
    }

    [Fact]
    public void CaseK_StableNegativeSellGap_PreservesDirection()
    {
        int[] gaps = [-100, -120, -150, -110, -160];

        var result = Feed(gaps, holdMs: 4000);

        Assert.Equal(GapCycleStatus.Stable, result.Current.Status);
        Assert.Equal(gaps, result.Current.Gaps);
        Assert.Equal(120d, result.Current.Center);
        Assert.Equal(20d, result.Current.Mad);
    }

    [Fact]
    public void CaseL_NullGap_IsNotConvertedToZeroAndResetsCycle()
    {
        var state = new GapCycleState();
        Process(state, 100, 0, holdMs: 1000);

        var result = state.Process(
            Start.AddSeconds(1),
            gap: null,
            hasRequiredData: false,
            confirmSatisfied: false,
            Config,
            holdConfirmMs: 1000);

        Assert.Equal(GapCycleTransition.ResetMissingData, result.Transition);
        Assert.Equal([100], result.CompletedCycle!.Gaps);
        Assert.DoesNotContain(0, result.CompletedCycle.Gaps);
        Assert.Empty(result.CurrentCycle.Gaps);
    }

    private static ScenarioResult Feed(IReadOnlyList<int> gaps, int holdMs)
    {
        var state = new GapCycleState();
        var completed = new List<GapCycleSnapshot>();
        GapCycleUpdateResult? last = null;
        for (var index = 0; index < gaps.Count; index++)
        {
            last = Process(state, gaps[index], index, holdMs);
            if (last.CompletedCycle is not null)
            {
                completed.Add(last.CompletedCycle);
            }
        }

        return new ScenarioResult(completed, last?.CurrentCycle ?? state.Current);
    }

    private static GapCycleUpdateResult Process(
        GapCycleState state,
        int gap,
        int second,
        int holdMs,
        int limitMaxGap = 0) =>
        state.Process(
            Start.AddSeconds(second),
            gap,
            hasRequiredData: true,
            confirmSatisfied: true,
            Config,
            holdMs,
            limitMaxGap);

    private sealed record ScenarioResult(
        IReadOnlyList<GapCycleSnapshot> CompletedCycles,
        GapCycleSnapshot Current);
}
