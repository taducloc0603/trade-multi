using System.Buffers.Binary;
using TradeDesktop.Infrastructure.Mt5Bridge;

namespace TradeDesktop.Tests.Mt5Bridge;

public sealed class Mt5BridgeTransportTests
{
    [Fact]
    public void Ring_ExchangesMessagesAcrossIndependentWriterLanes()
    {
        var buffer = new SharedBuffer();
        using var app = new Mt5BridgeSharedMemoryRing(buffer.Open(), 101, 1);
        using var ea = new Mt5BridgeSharedMemoryRing(buffer.Open(), 202, 2);

        Assert.True(app.TryWrite("app-command"));
        Assert.Equal(["app-command"], ea.Drain());

        Assert.True(ea.TryWrite("ea-response"));
        Assert.Equal(["ea-response"], app.Drain());
        Assert.NotEqual(app.WriterLane, ea.WriterLane);
    }

    [Fact]
    public void Ring_PrimesExistingHistoryButReadsMessagesWrittenAfterAttach()
    {
        var buffer = new SharedBuffer();
        using var ea = new Mt5BridgeSharedMemoryRing(buffer.Open(), 202, 2);
        Assert.True(ea.TryWrite("old-heartbeat"));

        using var app = new Mt5BridgeSharedMemoryRing(buffer.Open(), 101, 1);
        Assert.Empty(app.Drain());

        Assert.True(ea.TryWrite("new-heartbeat"));
        Assert.Equal(["new-heartbeat"], app.Drain());
    }

    [Fact]
    public async Task Transport_CorrelatesPongAndUpdatesHealth()
    {
        var buffer = new SharedBuffer();
        await using var transport = Mt5BridgeSharedMemoryTransport.ConnectForTest(
            buffer.Open(), 101, 1, TimeSpan.FromSeconds(1));
        using var ea = new Mt5BridgeSharedMemoryRing(buffer.Open(), 202, 55);

        var pingTask = transport.PingAsync(TimeSpan.FromSeconds(1));
        string command = string.Empty;
        for (var attempt = 0; attempt < 50 && string.IsNullOrEmpty(command); attempt++)
        {
            command = ea.Drain().SingleOrDefault() ?? string.Empty;
            if (string.IsNullOrEmpty(command))
            {
                await Task.Delay(5);
            }
        }

        var ping = Mt5BridgeProtocol.Deserialize<Mt5BridgePing>(command);
        Assert.NotNull(ping);

        var pong =
            $$"""{"v":1,"type":"pong","request_id":"{{ping.RequestId}}","account":12345,"generation":55,"ea_ms":100}""";
        Assert.True(ea.TryWrite(pong));

        var response = await pingTask;
        Assert.Equal("pong", response.Type);
        Assert.Equal(ping.RequestId, response.RequestId);
        Assert.True(transport.Health.IsReady);
        Assert.Equal(12345, transport.Health.Account);
        Assert.Equal(55UL, transport.Health.Generation);
    }

    [Fact]
    public async Task Transport_TimesOutWithoutResponseAndCanReuseAfterTimeout()
    {
        var buffer = new SharedBuffer();
        await using var transport = Mt5BridgeSharedMemoryTransport.ConnectForTest(
            buffer.Open(), 101, 1);

        await Assert.ThrowsAsync<TimeoutException>(
            () => transport.PingAsync(TimeSpan.FromMilliseconds(30)));

        await Assert.ThrowsAsync<TimeoutException>(
            () => transport.PingAsync(TimeSpan.FromMilliseconds(30)));
    }

    [Fact]
    public async Task Transport_WaitsForFinalExecutionResult()
    {
        var buffer = new SharedBuffer();
        await using var transport = Mt5BridgeSharedMemoryTransport.ConnectForTest(
            buffer.Open(), 101, 1);
        using var ea = new Mt5BridgeSharedMemoryRing(buffer.Open(), 202, 2);
        var command = new Mt5BridgeOpenCommand
        {
            RequestId = "open-1",
            Account = 12345,
            Symbol = "XAUUSD",
            Side = "BUY",
            Volume = 0.1,
            CreatedMilliseconds = 100,
            ExpiresMilliseconds = 1_000
        };

        var resultTask = transport.SendAsync(
            command, command.RequestId, TimeSpan.FromSeconds(1));
        Assert.True(ea.TryWrite(
            "{\"v\":1,\"type\":\"execution_result\",\"request_id\":\"open-1\",\"status\":\"received\"}"));
        await Task.Delay(25);
        Assert.False(resultTask.IsCompleted);

        Assert.True(ea.TryWrite(
            "{\"v\":1,\"type\":\"execution_result\",\"request_id\":\"open-1\",\"status\":\"confirmed\",\"ticket\":42}"));
        var result = await resultTask;

        Assert.Equal(Mt5BridgeExecutionStatuses.Confirmed, result.Status);
        Assert.Equal(42UL, result.Ticket);
    }

    [Fact]
    public async Task Transport_ReportsDispatchedProgressBeforeFinalResult()
    {
        var buffer = new SharedBuffer();
        await using var transport = Mt5BridgeSharedMemoryTransport.ConnectForTest(
            buffer.Open(), 101, 1);
        using var ea = new Mt5BridgeSharedMemoryRing(buffer.Open(), 202, 2);
        var command = new Mt5BridgeOpenCommand
        {
            RequestId = "open-progress-1",
            Account = 12345,
            Symbol = "XAUUSD",
            Side = "BUY",
            Volume = 0.1,
            CreatedMilliseconds = 100,
            ExpiresMilliseconds = 1_000
        };
        var dispatched = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var resultTask = transport.SendAsync(
            command,
            command.RequestId,
            TimeSpan.FromSeconds(1),
            onProgress: message =>
            {
                if (message.Status == Mt5BridgeExecutionStatuses.Dispatched)
                {
                    dispatched.TrySetResult();
                }
            });
        Assert.True(ea.TryWrite(
            "{\"v\":1,\"type\":\"execution_result\",\"request_id\":\"open-progress-1\",\"status\":\"dispatched\"}"));

        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(resultTask.IsCompleted);

        Assert.True(ea.TryWrite(
            "{\"v\":1,\"type\":\"execution_result\",\"request_id\":\"open-progress-1\",\"status\":\"confirmed\",\"ticket\":43}"));
        var result = await resultTask;
        Assert.Equal(43UL, result.Ticket);
    }

    [Fact]
    public async Task Transport_CompletesCloseOnAlreadyClosed()
    {
        var buffer = new SharedBuffer();
        await using var transport = Mt5BridgeSharedMemoryTransport.ConnectForTest(
            buffer.Open(), 101, 1);
        using var ea = new Mt5BridgeSharedMemoryRing(buffer.Open(), 202, 2);
        var command = new Mt5BridgeCloseCommand
        {
            RequestId = "close-1",
            Account = 12345,
            Ticket = 42,
            Symbol = "XAUUSD",
            Volume = 0,
            CreatedMilliseconds = 100,
            ExpiresMilliseconds = 1_000
        };

        var resultTask = transport.SendAsync(
            command, command.RequestId, TimeSpan.FromSeconds(1));
        Assert.True(ea.TryWrite(
            "{\"v\":1,\"type\":\"execution_result\",\"request_id\":\"close-1\",\"status\":\"already_closed\",\"ticket\":42}"));

        var result = await resultTask;
        Assert.Equal(Mt5BridgeExecutionStatuses.AlreadyClosed, result.Status);
        Assert.Equal(42UL, result.Ticket);
    }

    [Fact]
    public async Task Health_BecomesStaleWhenHeartbeatStops()
    {
        var buffer = new SharedBuffer();
        await using var transport = Mt5BridgeSharedMemoryTransport.ConnectForTest(
            buffer.Open(), 101, 1, TimeSpan.FromMilliseconds(30));
        using var ea = new Mt5BridgeSharedMemoryRing(buffer.Open(), 202, 55);

        Assert.True(ea.TryWrite(
            "{\"v\":1,\"type\":\"heartbeat\",\"account\":12345,\"generation\":55,\"ea_ms\":100}"));
        await Task.Delay(25);
        Assert.True(transport.Health.IsReady);

        await Task.Delay(40);
        Assert.False(transport.Health.IsReady);
    }

    [Fact]
    public void Health_DetectsBridgeGenerationChange()
    {
        var monitor = new Mt5BridgeHealthMonitor();
        monitor.Observe(new Mt5BridgeInboundMessage
        {
            Type = "bridge_ready",
            Generation = 10
        }, 0);
        monitor.Observe(new Mt5BridgeInboundMessage
        {
            Type = "heartbeat",
            Generation = 11
        }, 0);

        Assert.Equal(11UL, monitor.Current.Generation);
        Assert.Equal(1, monitor.Current.RestartCount);
    }

    [Fact]
    public void Health_TracksManualUiReadinessFromHeartbeat()
    {
        var monitor = new Mt5BridgeHealthMonitor();
        monitor.Observe(new Mt5BridgeInboundMessage
        {
            Type = "heartbeat",
            Account = 9611185,
            ManualUiReady = true,
            ManualUiCode = "ready",
            ChartSymbol = "XAUUSD",
            ChartHwnd = 591424,
            PanelFound = true,
            Dpi = 96,
            CoordinateSource = "scaled_chart_property"
        }, 0);

        var health = monitor.Current;
        Assert.True(health.IsReady);
        Assert.True(health.ManualUiReady);
        Assert.Equal("ready", health.ManualUiCode);
        Assert.Equal("XAUUSD", health.ChartSymbol);
        Assert.Equal(591424, health.ChartHwnd);
        Assert.True(health.PanelFound);
        Assert.Equal(96, health.Dpi);
        Assert.Equal("scaled_chart_property", health.CoordinateSource);
    }

    [Fact]
    public void ManualUiPolicy_AllowsVolumeNotPrimedToSelfRecoverOnOpen()
    {
        var health = Mt5BridgeHealth.Offline with
        {
            IsReady = true,
            ManualUiReady = false,
            ManualUiCode = "volume_not_primed"
        };

        Assert.True(Mt5BridgeManualUiPolicy.CanAttemptOpen(health));
    }

    [Theory]
    [InlineData("oct_panel_not_found")]
    [InlineData("chart_invalid")]
    [InlineData("trading_not_allowed")]
    [InlineData(null)]
    public void ManualUiPolicy_KeepsOtherReadinessFailuresBlocked(string? code)
    {
        var health = Mt5BridgeHealth.Offline with
        {
            IsReady = true,
            ManualUiReady = false,
            ManualUiCode = code
        };

        Assert.False(Mt5BridgeManualUiPolicy.CanAttemptOpen(health));
    }

    private sealed class SharedBuffer
    {
        private readonly byte[] _bytes = new byte[Mt5BridgeLayout.RegionSize];
        private readonly object _sync = new();

        public IMt5BridgeMemory Open() => new View(_bytes, _sync);

        private sealed class View(byte[] bytes, object sync) : IMt5BridgeMemory
        {
            public uint ReadUInt32(long offset)
            {
                lock (sync)
                {
                    return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset, sizeof(uint)));
                }
            }

            public ulong ReadUInt64(long offset)
            {
                lock (sync)
                {
                    return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)offset, sizeof(ulong)));
                }
            }

            public void WriteUInt32(long offset, uint value)
            {
                lock (sync)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)offset, sizeof(uint)), value);
                }
            }

            public void WriteUInt64(long offset, ulong value)
            {
                lock (sync)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan((int)offset, sizeof(ulong)), value);
                }
            }

            public void ReadBytes(long offset, byte[] destination, int count)
            {
                lock (sync)
                {
                    bytes.AsSpan((int)offset, count).CopyTo(destination);
                }
            }

            public void WriteBytes(long offset, byte[] source, int count)
            {
                lock (sync)
                {
                    source.AsSpan(0, count).CopyTo(bytes.AsSpan((int)offset, count));
                }
            }

            public void Dispose()
            {
            }
        }
    }
}
