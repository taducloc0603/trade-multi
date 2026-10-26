using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

// Ngưỡng log `[MARKET][WARN] Latency spike`: chân MMF giữ 500 ms; chân B socket (tuổi tick) dùng confirmLatencyB.
public sealed class LatencySpikeThresholdResolverTests
{
    [Fact]
    public void MmfLeg_KeepsDefaultThreshold()
    {
        Assert.Equal(500.0, LatencySpikeThresholdResolver.Resolve(latencyIsTickAge: false, confirmLatencyMsB: 2500, defaultThresholdMs: 500.0));
    }

    [Fact]
    public void TickAgeLeg_UsesConfirmLatencyB()
    {
        Assert.Equal(2500.0, LatencySpikeThresholdResolver.Resolve(latencyIsTickAge: true, confirmLatencyMsB: 2500, defaultThresholdMs: 500.0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TickAgeLeg_GuardDisabled_FallsBackToDefault(int confirmLatencyMsB)
    {
        Assert.Equal(500.0, LatencySpikeThresholdResolver.Resolve(latencyIsTickAge: true, confirmLatencyMsB, defaultThresholdMs: 500.0));
    }
}
