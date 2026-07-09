using TradeDesktop.Application.Services;
using Xunit;

namespace TradeDesktop.Tests;

public sealed class ConnectionStatusEvaluatorTests
{
    private static ChannelConnectionInput Healthy(ulong ts) => new(MapAvailable: true, ParseSuccess: true, Timestamp: ts, Connected: 1);

    [Fact]
    public void BothHealthy_NotDown()
    {
        var sut = new ConnectionStatusEvaluator();

        var status = sut.Evaluate(Healthy(100), Healthy(200));

        Assert.False(status.IsDown);
        Assert.Empty(status.Reasons);
    }

    [Fact]
    public void ChannelConnectedZero_Down_WithChannelReason()
    {
        var sut = new ConnectionStatusEvaluator();

        var status = sut.Evaluate(
            new ChannelConnectionInput(true, true, 100, Connected: 0),
            Healthy(200));

        Assert.True(status.IsDown);
        Assert.Contains(status.Reasons, r => r.Contains("Sàn A"));
    }

    [Fact]
    public void MapUnavailable_Down()
    {
        var sut = new ConnectionStatusEvaluator();

        var status = sut.Evaluate(
            Healthy(100),
            new ChannelConnectionInput(MapAvailable: false, ParseSuccess: false, Timestamp: 0, Connected: -1));

        Assert.True(status.IsDown);
        Assert.Contains(status.Reasons, r => r.Contains("Sàn B"));
    }

    [Fact]
    public void HeartbeatFrozen_DownOnlyAfterThreshold()
    {
        var sut = new ConnectionStatusEvaluator(frozenPollThreshold: 3);

        // Cùng timestamp lặp lại: poll 1..3 chưa đủ ngưỡng, poll 4 (unchanged=3) mới down.
        Assert.False(sut.Evaluate(Healthy(100), Healthy(100)).IsDown); // unchanged A=0,B=0
        Assert.False(sut.Evaluate(Healthy(100), Healthy(100)).IsDown); // unchanged=1
        Assert.False(sut.Evaluate(Healthy(100), Healthy(100)).IsDown); // unchanged=2
        var status = sut.Evaluate(Healthy(100), Healthy(100));          // unchanged=3 >= 3
        Assert.True(status.IsDown);
        Assert.Contains(status.Reasons, r => r.Contains("heartbeat"));
    }

    [Fact]
    public void HeartbeatAdvancing_NeverDown()
    {
        var sut = new ConnectionStatusEvaluator(frozenPollThreshold: 3);

        for (ulong ts = 1; ts <= 10; ts++)
        {
            Assert.False(sut.Evaluate(Healthy(ts), Healthy(ts + 1000)).IsDown);
        }
    }

    [Fact]
    public void FrozenCounterResets_WhenTimestampChanges()
    {
        var sut = new ConnectionStatusEvaluator(frozenPollThreshold: 3);

        // Kênh B luôn chạy đều để chỉ xét kênh A đóng băng rồi hồi.
        sut.Evaluate(Healthy(100), Healthy(200)); // A unchanged=0
        sut.Evaluate(Healthy(100), Healthy(201)); // A unchanged=1
        sut.Evaluate(Healthy(100), Healthy(202)); // A unchanged=2
        // Timestamp A đổi → reset về 0.
        Assert.False(sut.Evaluate(Healthy(101), Healthy(203)).IsDown);
        Assert.False(sut.Evaluate(Healthy(102), Healthy(204)).IsDown);
    }

    [Fact]
    public void FrozenDominates_EvenIfConnectedIsOne()
    {
        var sut = new ConnectionStatusEvaluator(frozenPollThreshold: 2);

        sut.Evaluate(Healthy(100), Healthy(200)); // A unchanged=0
        sut.Evaluate(Healthy(100), Healthy(201)); // A unchanged=1
        var status = sut.Evaluate(Healthy(100), Healthy(202)); // A unchanged=2 >= 2 → down dù Connected=1

        Assert.True(status.IsDown);
        Assert.Contains(status.Reasons, r => r.Contains("Sàn A") && r.Contains("heartbeat"));
    }

    [Fact]
    public void RecoversAfterDown_ReportsNotDown()
    {
        var sut = new ConnectionStatusEvaluator();

        var down = sut.Evaluate(new ChannelConnectionInput(true, true, 100, 0), Healthy(200));
        Assert.True(down.IsDown);

        var up = sut.Evaluate(Healthy(101), Healthy(201));
        Assert.False(up.IsDown);
    }

    [Fact]
    public void Reset_ClearsHeartbeatState()
    {
        var sut = new ConnectionStatusEvaluator(frozenPollThreshold: 2);

        sut.Evaluate(Healthy(100), Healthy(200));
        sut.Evaluate(Healthy(100), Healthy(200)); // A unchanged=1
        sut.Reset();
        // Sau reset, timestamp cũ 100 được coi như mới → không down ngay.
        Assert.False(sut.Evaluate(Healthy(100), Healthy(200)).IsDown);
    }
}
