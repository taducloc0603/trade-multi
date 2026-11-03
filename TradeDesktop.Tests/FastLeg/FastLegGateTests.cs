using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.FastLeg;
using TradeDesktop.Domain.Models;

namespace TradeDesktop.Tests.FastLeg;

public sealed class FastLegGateTests
{
    private static FastLegConfig Filter() => new(
        Enabled: true,
        MapName: @"Local\MT_C_Tick",
        HeartbeatMapName: string.Empty,
        Symbol: "XAUUSD",
        Mode: FastLegMode.Filter,
        MaxMovePts: 5m,
        WindowMs: 200,
        MaxAgeMs: 1000,
        MaxDeferMs: 3000,
        FailOpen: false,
        ApplyToExit: true);

    [Fact]
    public void Disabled_ReturnsNull_SoCallersKeepOldBehavior()
    {
        var gate = new FastLegGate(new Provider(FastLegConfig.Disabled), new FastLegTickBuffer(), () => 1000);

        Assert.Null(gate.EvaluateNow());
    }

    [Fact]
    public void Active_EvaluatesWithCurrentPointAndClock()
    {
        var buffer = new FastLegTickBuffer();
        buffer.Push(new FastLegTickSample(700, 4000.00m, 4000.10m));
        buffer.Push(new FastLegTickSample(990, 4000.20m, 4000.30m));
        var gate = new FastLegGate(new Provider(Filter()), buffer, () => 1000);

        var verdict = gate.EvaluateNow();

        Assert.NotNull(verdict);
        Assert.Equal(FastLegVerdictKind.TooFast, verdict.Kind);
        Assert.Equal(20m, verdict.MovePts);
        Assert.True(verdict.BlocksOpen);
        Assert.True(verdict.BlocksClose);
    }

    [Fact]
    public void ConfigChange_AppliesImmediately()
    {
        var provider = new Provider(Filter());
        var buffer = new FastLegTickBuffer();
        buffer.Push(new FastLegTickSample(700, 4000.00m, 4000.10m));
        buffer.Push(new FastLegTickSample(990, 4000.20m, 4000.30m));
        var gate = new FastLegGate(provider, buffer, () => 1000);
        Assert.True(gate.EvaluateNow()!.BlocksOpen);

        provider.FastLeg = Filter() with { Mode = FastLegMode.Observe };
        Assert.False(gate.EvaluateNow()!.BlocksOpen);

        provider.FastLeg = FastLegConfig.Disabled;
        Assert.Null(gate.EvaluateNow());
    }

    [Fact]
    public void TotalTicks_FollowsBuffer()
    {
        var buffer = new FastLegTickBuffer();
        var gate = new FastLegGate(new Provider(Filter()), buffer, () => 1000);
        Assert.Equal(0, gate.TotalTicks);

        buffer.Push(new FastLegTickSample(990, 1m, 2m));

        Assert.Equal(1, gate.TotalTicks);
    }

    private sealed class Provider(FastLegConfig fastLeg) : IRuntimeConfigProvider
    {
        public FastLegConfig FastLeg { get; set; } = fastLeg;
        public FastLegConfig CurrentFastLegConfig => FastLeg;
        public string CurrentMachineHostName => "test";
        public int CurrentPoint => 100;
        public int CurrentOpenPts => 0;
        public int CurrentConfirmGapPts => 0;
        public int CurrentClosePts => 0;
        public int CurrentCloseConfirmGapPts => 0;
        public double CurrentCloseTpProfit => 0;
        public double CurrentCloseConfirmTpProfit => 0;
        public int CurrentStartTimeHold => 0;
        public int CurrentEndTimeHold => 0;
        public int CurrentConfirmLatencyMs => 0;
        public int CurrentMaxGap => 0;
        public int CurrentLimitMaxGap => 0;
        public double CurrentLimitMaxTp => 0;
        public double CurrentSosTriggerAOpenDistancePts => 0;
        public int CurrentSosTriggerAfterSeconds => 0;
        public int CurrentSosCloseConfirmGapPts => 0;
        public int CurrentSosCloseGapPts => 0;
        public int CurrentMaxSpread => 0;
        public int CurrentOpenPendingTimeMs => 0;
        public int CurrentClosePendingTimeMs => 0;
        public int CurrentDelayOpenAMs => 0;
        public int CurrentDelayOpenBMs => 0;
        public int CurrentDelayCloseAMs => 0;
        public int CurrentDelayCloseBMs => 0;
        public int CurrentOpenNumberOfQualifyingTimes => 1;
        public int CurrentCloseNumberOfQualifyingTimes => 1;
        public int CurrentOppositeOpenMinDistancePts => 0;
        public int CurrentOpenPriceFreezeMs => 0;
        public int CurrentClosePriceFreezeMs => 0;
        public string CurrentMapName1 => "A";
        public string CurrentMapName2 => "B";
        public DashboardMetrics? CurrentDashboardMetrics => null;
    }
}
