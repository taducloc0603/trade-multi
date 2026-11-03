using TradeDesktop.Application.Services.FastLeg;

namespace TradeDesktop.Tests.FastLeg;

public sealed class FastLegTickBufferTests
{
    private static FastLegTickSample Sample(long ts, decimal mid = 100m) => new(ts, mid - 0.1m, mid + 0.1m);

    [Fact]
    public void Empty_HasNoReferenceOrLast()
    {
        var buffer = new FastLegTickBuffer();

        var snapshot = buffer.Capture(200, 1000);

        Assert.Null(snapshot.Reference);
        Assert.Null(snapshot.Last);
        Assert.Equal(0, snapshot.TotalTicks);
    }

    [Fact]
    public void Reference_IsNewestSampleAtOrBeforeWindowStart()
    {
        var buffer = new FastLegTickBuffer();
        buffer.SetRetentionMs(200);
        buffer.Push(Sample(700));
        buffer.Push(Sample(800));
        buffer.Push(Sample(900));
        buffer.Push(Sample(1000));

        var snapshot = buffer.Capture(200, 1000);

        Assert.Equal(800, snapshot.Reference!.Value.TimestampMs);
        Assert.Equal(1000, snapshot.Last!.Value.TimestampMs);
    }

    [Fact]
    public void Reference_FallsBackToOldest_WhenNothingIsOldEnough()
    {
        var buffer = new FastLegTickBuffer();
        buffer.SetRetentionMs(200);
        buffer.Push(Sample(950));
        buffer.Push(Sample(990));

        var snapshot = buffer.Capture(200, 1000);

        Assert.Equal(950, snapshot.Reference!.Value.TimestampMs);
    }

    [Fact]
    public void SparseTicks_KeepPriceAtWindowStartAsReference()
    {
        // Sàn C thưa tick: giá tại đầu cửa sổ chính là tick cuối trước đó, dù nó đã cũ 5 s.
        var buffer = new FastLegTickBuffer();
        buffer.SetRetentionMs(200);
        buffer.Push(Sample(5000));
        buffer.Push(Sample(10_000));

        var snapshot = buffer.Capture(200, 10_050);

        Assert.Equal(5000, snapshot.Reference!.Value.TimestampMs);
        Assert.Equal(10_000, snapshot.Last!.Value.TimestampMs);
    }

    [Fact]
    public void QuietMarket_ReferenceEqualsLast()
    {
        var buffer = new FastLegTickBuffer();
        buffer.SetRetentionMs(200);
        buffer.Push(Sample(1000));

        var snapshot = buffer.Capture(200, 5000);

        Assert.Equal(snapshot.Last, snapshot.Reference);
    }

    [Fact]
    public void Prune_DoesNotChangeResultOfCaptureWithinRetention()
    {
        var buffer = new FastLegTickBuffer();
        buffer.SetRetentionMs(200);
        for (var ts = 0; ts <= 10_000; ts += 10)
        {
            buffer.Push(Sample(ts, 100m + ts));
        }

        var snapshot = buffer.Capture(200, 10_000);

        Assert.Equal(9800, snapshot.Reference!.Value.TimestampMs);
        Assert.Equal(1001, snapshot.TotalTicks);
    }

    [Fact]
    public void BurstAtSameTimestamp_IsCappedAtMaxSamples()
    {
        var buffer = new FastLegTickBuffer();
        buffer.SetRetentionMs(5000);
        for (var i = 0; i < FastLegTickBuffer.MaxSamples * 3; i++)
        {
            buffer.Push(Sample(1000 + i));
        }

        var snapshot = buffer.Capture(5000, 1000 + (FastLegTickBuffer.MaxSamples * 3));

        // Không có mẫu đủ cũ: tham chiếu là mẫu cũ nhất CÒN LẠI, chứng tỏ phần cũ đã bị bỏ theo trần.
        Assert.Equal(1000 + (FastLegTickBuffer.MaxSamples * 2), snapshot.Reference!.Value.TimestampMs);
        Assert.Equal(FastLegTickBuffer.MaxSamples * 3, snapshot.TotalTicks);
    }

    [Fact]
    public void OutOfOrderSample_IsRejected()
    {
        var buffer = new FastLegTickBuffer();
        buffer.Push(Sample(1000));

        Assert.False(buffer.Push(Sample(999)));
        Assert.True(buffer.Push(Sample(1000)));
        Assert.Equal(2, buffer.Capture(200, 1000).TotalTicks);
    }

    [Fact]
    public void Reset_ClearsSamplesAndHeartbeat_ButKeepsDownReason()
    {
        var buffer = new FastLegTickBuffer();
        buffer.Push(Sample(1000));
        buffer.SetHeartbeat(new FastLegHeartbeat(1000, true));
        buffer.SetDownReason("map_not_found");

        buffer.Reset();

        var snapshot = buffer.Capture(200, 1000);
        Assert.Null(snapshot.Last);
        Assert.Null(snapshot.Heartbeat);
        Assert.Equal("map_not_found", snapshot.DownReason);
    }

    [Fact]
    public async Task ConcurrentPushAndCapture_NeverThrowsAndStaysOrdered()
    {
        var buffer = new FastLegTickBuffer();
        buffer.SetRetentionMs(50);
        var writer = Task.Run(() =>
        {
            for (var ts = 0; ts < 200_000; ts++)
            {
                buffer.Push(Sample(ts));
            }
        });

        while (!writer.IsCompleted)
        {
            var snapshot = buffer.Capture(50, long.MaxValue / 2);
            if (snapshot.Last is { } last && snapshot.Reference is { } reference)
            {
                Assert.True(reference.TimestampMs <= last.TimestampMs);
            }
        }

        await writer;
        Assert.Equal(200_000, buffer.Capture(50, 200_000).TotalTicks);
    }
}
