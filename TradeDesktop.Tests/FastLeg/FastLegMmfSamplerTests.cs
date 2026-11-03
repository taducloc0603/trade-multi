using System.IO.MemoryMappedFiles;
using System.Text;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.FastLeg;
using TradeDesktop.Domain.Models;
using TradeDesktop.Infrastructure.MarketData;

namespace TradeDesktop.Tests.FastLeg;

// Dùng map THẬT, layout đúng như EA (TickMemory.mqh / TradesMemory.mqh), mỗi test một tên riêng.
public sealed class FastLegMmfSamplerTests : IDisposable
{
    private readonly string _tickName = $"Local\\FastLegTest_{Guid.NewGuid():N}_Tick";
    private readonly string _heartbeatName;
    private readonly List<IDisposable> _disposables = [];
    private readonly FastLegTickBuffer _buffer = new();
    private readonly Provider _provider = new();
    private long _now = 1000;

    public FastLegMmfSamplerTests()
    {
        _heartbeatName = FastLegConfig.DeriveHeartbeatMapName(_tickName);
        _provider.FastLeg = Config();
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private FastLegConfig Config(string symbol = "XAUUSD") => new(
        Enabled: true,
        MapName: _tickName,
        HeartbeatMapName: _heartbeatName,
        Symbol: symbol,
        Mode: FastLegMode.Filter,
        MaxMovePts: 5m,
        WindowMs: 200,
        MaxAgeMs: 1000,
        MaxDeferMs: 3000,
        FailOpen: false,
        ApplyToExit: true);

    private FastLegMmfSampler Sampler()
    {
        var sampler = new FastLegMmfSampler(_provider, _buffer, () => _now);
        _disposables.Add(sampler);
        return sampler;
    }

    private MemoryMappedViewAccessor CreateMap(string name, int size = 256)
    {
        var file = MemoryMappedFile.CreateNew(name, size);
        var accessor = file.CreateViewAccessor();
        _disposables.Add(accessor);
        _disposables.Add(file);
        return accessor;
    }

    private static void WriteTick(
        MemoryMappedViewAccessor accessor,
        long timestamp,
        double bid,
        double ask,
        long timeMsc = 1,
        string symbol = "XAUUSD",
        int version = 1,
        double? spread = null)
    {
        accessor.Write(0, version);
        accessor.Write(4, timestamp);
        accessor.Write(16, bid);
        accessor.Write(24, ask);
        accessor.Write(32, spread ?? ask - bid);
        accessor.Write(40, timeMsc);
        var symbolBytes = new byte[16];
        Encoding.ASCII.GetBytes(symbol).CopyTo(symbolBytes, 0);
        accessor.WriteArray(48, symbolBytes, 0, 16);
    }

    private static void WriteHeartbeat(MemoryMappedViewAccessor accessor, long timestamp, bool connected)
    {
        accessor.Write(4, timestamp);
        accessor.Write(12, connected ? 1 : 0);
    }

    private FastLegBufferSnapshot Capture() => _buffer.Capture(200, _now);

    [Fact]
    public void ReadsBidAskTimestampAndSymbol_FromEaLayout()
    {
        var tick = CreateMap(_tickName);
        WriteTick(tick, 900, 3991.838, 3992.006);

        Sampler().PollOnce();

        var snapshot = Capture();
        Assert.Null(snapshot.DownReason);
        Assert.Equal(900, snapshot.Last!.Value.TimestampMs);
        Assert.Equal(3991.838m, snapshot.Last.Value.Bid);
        Assert.Equal(3992.006m, snapshot.Last.Value.Ask);
    }

    [Fact]
    public void SameRecord_IsCountedOnce()
    {
        var tick = CreateMap(_tickName);
        WriteTick(tick, 900, 100, 101);
        var sampler = Sampler();

        sampler.PollOnce();
        sampler.PollOnce();

        Assert.Equal(1, Capture().TotalTicks);
    }

    [Fact]
    public void NewPriceInSameTickCountQuantum_IsANewSample()
    {
        // GetTickCount64 chỉ nhảy mỗi ~15.6 ms: hai lần EA ghi có thể cùng timestamp.
        var tick = CreateMap(_tickName);
        var sampler = Sampler();
        WriteTick(tick, 900, 100, 101, timeMsc: 1);
        sampler.PollOnce();
        WriteTick(tick, 900, 100.5, 101.5, timeMsc: 2);
        sampler.PollOnce();

        Assert.Equal(2, Capture().TotalTicks);
        Assert.Equal(100.5m, Capture().Last!.Value.Bid);
    }

    [Theory]
    [InlineData(100d, 101d, 2d)]            // bid/ask của hai tick khác nhau: spread không khớp.
    [InlineData(double.NaN, 101d, 1d)]
    [InlineData(100d, double.PositiveInfinity, 1d)]
    [InlineData(0d, 1d, 1d)]
    [InlineData(-5d, -4d, 1d)]
    [InlineData(1e13, 1e13, 0d)]
    public void InconsistentRecord_IsRejectedWithoutGoingDown(double bid, double ask, double spread)
    {
        var tick = CreateMap(_tickName);
        WriteTick(tick, 900, bid, ask, spread: spread);

        Sampler().PollOnce();

        var snapshot = Capture();
        Assert.Null(snapshot.Last);
        Assert.Null(snapshot.DownReason);
    }

    [Fact]
    public void ZeroTimestamp_MeansNoTickYet()
    {
        var tick = CreateMap(_tickName);
        WriteTick(tick, 0, 100, 101);

        Sampler().PollOnce();

        Assert.Null(Capture().Last);
    }

    [Fact]
    public void BackwardServerTime_IsRejected_ThenAcceptedWhenPersistent()
    {
        var tick = CreateMap(_tickName);
        var sampler = Sampler();
        WriteTick(tick, 900, 100, 101, timeMsc: 1000);
        sampler.PollOnce();

        WriteTick(tick, 910, 200, 201, timeMsc: 500);
        sampler.PollOnce();
        Assert.Equal(100m, Capture().Last!.Value.Bid);

        // Lệch kéo dài không phải torn read: đồng hồ server đổi thật, không được kẹt mãi ở Stale.
        for (var i = 0; i < 25; i++)
        {
            sampler.PollOnce();
        }

        Assert.Equal(200m, Capture().Last!.Value.Bid);
    }

    [Fact]
    public void WrongVersion_IsDown()
    {
        var tick = CreateMap(_tickName);
        WriteTick(tick, 900, 100, 101, version: 2);

        Sampler().PollOnce();

        Assert.Equal("bad_version:2", Capture().DownReason);
        Assert.Null(Capture().Last);
    }

    [Fact]
    public void WrongSymbol_IsDown()
    {
        var tick = CreateMap(_tickName);
        WriteTick(tick, 900, 100, 101, symbol: "EURUSD");

        Sampler().PollOnce();

        Assert.Equal("symbol_mismatch:EURUSD", Capture().DownReason);
        Assert.Null(Capture().Last);
    }

    [Fact]
    public void SymbolCheck_IsCaseInsensitive_AndSkippedWhenNotConfigured()
    {
        var tick = CreateMap(_tickName);
        WriteTick(tick, 900, 100, 101, symbol: "xauusd");
        var sampler = Sampler();
        sampler.PollOnce();
        Assert.Null(Capture().DownReason);

        _provider.FastLeg = Config(symbol: string.Empty);
        WriteTick(tick, 910, 100, 101, symbol: "ANYTHING", timeMsc: 2);
        sampler.PollOnce();

        Assert.Null(Capture().DownReason);
        Assert.Equal(910, Capture().Last!.Value.TimestampMs);
    }

    [Fact]
    public void SixteenByteSymbol_WithoutTerminator_IsRead()
    {
        _provider.FastLeg = Config(symbol: "ABCDEFGHIJKLMNOP");
        var tick = CreateMap(_tickName);
        WriteTick(tick, 900, 100, 101, symbol: "ABCDEFGHIJKLMNOP");

        Sampler().PollOnce();

        Assert.Null(Capture().DownReason);
    }

    [Fact]
    public void MissingMap_IsDown_ThenRecovers_AfterRetryInterval()
    {
        var sampler = Sampler();
        sampler.PollOnce();
        Assert.Equal("map_not_found", Capture().DownReason);

        var tick = CreateMap(_tickName);
        WriteTick(tick, 900, 100, 101);

        // Chưa tới hạn thử lại: không gọi OpenExisting mỗi nhịp (exception đắt, 60+ lần/giây).
        _now += 500;
        sampler.PollOnce();
        Assert.Equal("map_not_found", Capture().DownReason);

        _now += 600;
        sampler.PollOnce();
        Assert.Null(Capture().DownReason);
        Assert.Equal(900, Capture().Last!.Value.TimestampMs);
    }

    [Fact]
    public void Heartbeat_IsRead_WithConnectedFlag()
    {
        var tick = CreateMap(_tickName);
        WriteTick(tick, 900, 100, 101);
        var heartbeat = CreateMap(_heartbeatName);
        WriteHeartbeat(heartbeat, 950, connected: false);
        var sampler = Sampler();

        sampler.PollOnce();
        Assert.Equal(new FastLegHeartbeat(950, false), Capture().Heartbeat);

        WriteHeartbeat(heartbeat, 1000, connected: true);
        sampler.PollOnce();
        Assert.Equal(new FastLegHeartbeat(1000, true), Capture().Heartbeat);
    }

    [Fact]
    public void MissingHeartbeatMap_IsNotDown()
    {
        var tick = CreateMap(_tickName);
        WriteTick(tick, 900, 100, 101);

        Sampler().PollOnce();

        Assert.Null(Capture().Heartbeat);
        Assert.Null(Capture().DownReason);
    }

    [Fact]
    public void Disabling_ClearsBufferAndReportsDisabled()
    {
        var tick = CreateMap(_tickName);
        WriteTick(tick, 900, 100, 101);
        var sampler = Sampler();
        sampler.PollOnce();
        Assert.NotNull(Capture().Last);

        _provider.FastLeg = FastLegConfig.Disabled;
        sampler.PollOnce();

        Assert.Null(Capture().Last);
        Assert.Equal("disabled", Capture().DownReason);
    }

    [Fact]
    public void ChangingMapName_ReopensAndClearsOldSamples()
    {
        var tick = CreateMap(_tickName);
        WriteTick(tick, 900, 100, 101);
        var sampler = Sampler();
        sampler.PollOnce();
        Assert.Equal(100m, Capture().Last!.Value.Bid);

        var otherName = $"Local\\FastLegTest_{Guid.NewGuid():N}_Tick";
        var other = CreateMap(otherName);
        WriteTick(other, 910, 500, 501);
        _provider.FastLeg = Config() with { MapName = otherName, HeartbeatMapName = string.Empty };

        sampler.PollOnce();

        Assert.Equal(500m, Capture().Last!.Value.Bid);
        Assert.Null(Capture().Heartbeat);
    }

    [Fact]
    public async Task Start_ReadsInBackground_AndStopsOnDispose()
    {
        var tick = CreateMap(_tickName);
        WriteTick(tick, 900, 100, 101);
        var sampler = new FastLegMmfSampler(_provider, _buffer);

        sampler.Start();
        sampler.Start();
        var deadline = Environment.TickCount64 + 5000;
        while (Capture().Last is null && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10);
        }

        sampler.Dispose();
        sampler.Dispose();

        Assert.NotNull(Capture().Last);
    }

    private sealed class Provider : IRuntimeConfigProvider
    {
        public FastLegConfig FastLeg { get; set; } = FastLegConfig.Disabled;
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
