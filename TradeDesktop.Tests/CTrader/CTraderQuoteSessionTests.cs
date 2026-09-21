using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Domain.Models;
using TradeDesktop.Infrastructure.CTrader;
using TradeDesktop.Infrastructure.MarketData;

namespace TradeDesktop.Tests.CTrader;

public sealed class CTraderQuoteSessionTests
{
    private const string Password = "Pw-Test-Only$$";

    private const string SecurityList =
        "35=y|34=2|49=cServer|50=QUOTE|52=20260916-14:52:24.901|56=live.fxpro.8220816|57=QUOTE|320=sec|322=responce:sec|560=0|146=2|55=1|1007=EURUSD|1008=5|55=41|1007=XAUUSD|1008=2";

    private const string SpotW =
        "35=W|34=3|49=cServer|50=QUOTE|52=20260916-14:57:52.978|56=live.fxpro.8220816|57=QUOTE|55=41|262=MARKETDATAID|268=2|269=0|270=4350.77|269=1|270=4350.93";

    private static readonly ExchangeMetrics ExchangeA = new("XAUUSD", 4350.10m, 4350.20m, 0.10m, 1, 5, "t", 1, 1, true, null);

    internal static CTraderFixConfig ValidConfig() => CTraderFixConfig.Empty with
    {
        Quote = new CTraderEndpoint("live-uk-eqx-01.p.c-trader.com", 5211, 5201),
        Trade = new CTraderEndpoint("live-uk-eqx-01.p.c-trader.com", 5212, 5202),
        SenderCompId = "live.fxpro.8220816",
        Password = Password,
        SymbolId = 41,
        VolumeBUnits = 100m,
        ContractSizeB = 100m,
    };

    private sealed class Harness
    {
        public long Clock = 10_000;
        public List<FakeCTraderTransport> Transports { get; } = [];
        public List<string> Logs { get; } = [];
        public List<CTraderQuoteSessionEvent> Events { get; } = [];
        public CTraderQuoteSession Session { get; }

        public Harness()
        {
            Session = new CTraderQuoteSession(_ =>
            {
                var t = new FakeCTraderTransport();
                Transports.Add(t);
                return t;
            }, () => Clock);
            Session.LogLine += line => { lock (Logs) Logs.Add(line); };
            Session.EventRaised += e => { lock (Events) Events.Add(e); };
        }

        public FakeCTraderTransport Current => Transports[^1];

        public async Task EnsureAsync(string platformB, CTraderFixConfig config)
        {
            Session.EnsureState(platformB, config);
            await Session.WaitForLifecycleAsync();
        }

        public async Task<FakeCTraderTransport> StreamingAsync()
        {
            await EnsureAsync("ctrader", ValidConfig());
            Current.Logon(CTraderSessionRole.Quote);
            Current.Receive(CTraderSessionRole.Quote, FixTestSupport.Parse(SecurityList));
            Current.Receive(CTraderSessionRole.Quote, FixTestSupport.Parse(SpotW));
            return Current;
        }

        public ExchangeMetrics Read(int point = 100, int confirmLatencyMs = 0) => Session.Read(ExchangeA, point, confirmLatencyMs);
    }

    [Theory]
    [InlineData("mt5")]
    [InlineData("mt4")]
    [InlineData("")]
    public async Task G4_NotCTrader_NeverOpensTransport(string platformB)
    {
        var h = new Harness();

        await h.EnsureAsync(platformB, ValidConfig());

        Assert.Empty(h.Transports);
        Assert.False(h.Read().IsConnected);
    }

    [Fact]
    public async Task G4_CTrader_StartsQuoteOnlyOnce_NoTradeSession()
    {
        var h = new Harness();

        await h.EnsureAsync("ctrader", ValidConfig());
        await h.EnsureAsync("ctrader", ValidConfig());
        await h.EnsureAsync("CTRADER", ValidConfig());

        var transport = Assert.Single(h.Transports);
        Assert.Equal(["start:Quote"], transport.Calls);
    }

    [Fact]
    public async Task MissingRequiredConfig_DoesNotConnect_AndLogsOnce()
    {
        var h = new Harness();
        var incomplete = ValidConfig() with { Password = string.Empty };

        await h.EnsureAsync("ctrader", incomplete);
        await h.EnsureAsync("ctrader", incomplete);

        Assert.Empty(h.Transports);
        Assert.Single(h.Logs, l => l.Contains("thiếu", StringComparison.Ordinal));
        var metrics = h.Read();
        Assert.False(metrics.IsConnected);
        Assert.Contains("Password", metrics.Error);
    }

    [Fact]
    public async Task Logon_SendsSecurityListThenSpotSubscribe_OnQuote()
    {
        var h = new Harness();
        await h.EnsureAsync("ctrader", ValidConfig());

        h.Current.Logon(CTraderSessionRole.Quote);

        Assert.Equal(["send:Quote:x", "send:Quote:V"], h.Current.Calls.Where(c => c.StartsWith("send", StringComparison.Ordinal)));
        var subscribe = Assert.IsType<QuickFix.FIX44.MarketDataRequest>(h.Current.Sent[1].Message);
        Assert.Equal('1', subscribe.SubscriptionRequestType.getValue());
        Assert.Equal(1, subscribe.MarketDepth.getValue());
    }

    [Fact]
    public async Task Streaming_ReadReturnsTopOfBookWithSymbolFromSecurityList()
    {
        var h = new Harness();
        await h.StreamingAsync();
        h.Clock += 120;

        var metrics = h.Read();

        Assert.True(metrics.IsConnected);
        Assert.Equal("XAUUSD", metrics.Symbol);
        Assert.Equal(4350.77m, metrics.Bid);
        Assert.Equal(4350.93m, metrics.Ask);
        Assert.Equal(0.16m, metrics.Spread);
        Assert.Equal(120m, metrics.LatencyMs);
        Assert.Equal(1f, metrics.Tps);
        Assert.Null(metrics.Error);
    }

    [Fact]
    public async Task NotConnected_BeforeSecurityListOrBeforeW()
    {
        var h = new Harness();
        await h.EnsureAsync("ctrader", ValidConfig());
        Assert.False(h.Read().IsConnected);

        h.Current.Logon(CTraderSessionRole.Quote);
        h.Current.Receive(CTraderSessionRole.Quote, FixTestSupport.Parse(SpotW));
        Assert.False(h.Read().IsConnected);

        // W đến trước SecurityList vẫn là giá của đúng symbolId; chỉ được phục vụ sau khi digits đã kiểm.
        h.Current.Receive(CTraderSessionRole.Quote, FixTestSupport.Parse(SecurityList));
        Assert.True(h.Read().IsConnected);
    }

    [Fact]
    public async Task NotConnected_WithSecurityListButNoW()
    {
        var h = new Harness();
        await h.EnsureAsync("ctrader", ValidConfig());
        h.Current.Logon(CTraderSessionRole.Quote);
        h.Current.Receive(CTraderSessionRole.Quote, FixTestSupport.Parse(SecurityList));

        var metrics = h.Read();

        Assert.False(metrics.IsConnected);
        Assert.Equal("XAUUSD", metrics.Symbol);
        Assert.Equal("Book rỗng", metrics.Error);
    }

    [Fact]
    public async Task R9_Logout_ClearsBookImmediately_AndRelogonNeedsFreshData()
    {
        var h = new Harness();
        var transport = await h.StreamingAsync();
        Assert.True(h.Read().IsConnected);

        transport.Logout(CTraderSessionRole.Quote);

        var metrics = h.Read();
        Assert.False(metrics.IsConnected);
        Assert.Null(metrics.Bid);
        Assert.Contains(h.Events, e => e.Kind == CTraderQuoteEventKind.LoggedOut);

        transport.Logon(CTraderSessionRole.Quote);
        Assert.False(h.Read().IsConnected);
        Assert.Equal(4, transport.Sent.Count);
    }

    [Fact]
    public async Task R6_DigitsMismatch_FailsClosed_RaisesOncePerState_AndRecovers()
    {
        var h = new Harness();
        await h.StreamingAsync();

        var wrong = h.Read(point: 1000);
        h.Read(point: 1000);

        Assert.False(wrong.IsConnected);
        Assert.Contains("Lệch digits", wrong.Error);
        Assert.Single(h.Events, e => e.Kind == CTraderQuoteEventKind.DigitsMismatch);
        Assert.Contains(h.Logs, l => l.StartsWith("[CTRADER][ERROR] CTRADER_DIGITS_MISMATCH", StringComparison.Ordinal));

        Assert.True(h.Read(point: 100).IsConnected);
    }

    [Fact]
    public async Task G4_SwitchAway_UnsubscribesBeforeLogout_Disposes_NoLogoutAlert()
    {
        var h = new Harness();
        var transport = await h.StreamingAsync();

        await h.EnsureAsync("mt5", ValidConfig());

        Assert.Equal(["send:Quote:V", "stop:Quote", "dispose"], transport.Calls.Skip(3));
        var unsubscribe = Assert.IsType<QuickFix.FIX44.MarketDataRequest>(transport.Sent[^1].Message);
        Assert.Equal('2', unsubscribe.SubscriptionRequestType.getValue());
        Assert.Equal("MARKETDATAID", unsubscribe.MDReqID.getValue());
        Assert.True(transport.Disposed);
        Assert.DoesNotContain(h.Events, e => e.Kind == CTraderQuoteEventKind.LoggedOut);
        Assert.Contains(h.Events, e => e.Kind == CTraderQuoteEventKind.Stopped);
        Assert.False(h.Read().IsConnected);
    }

    [Fact]
    public async Task ConfigChange_RestartsSession_OldTransportEventsIgnored()
    {
        var h = new Harness();
        var old = await h.StreamingAsync();

        await h.EnsureAsync("ctrader", ValidConfig() with { SymbolId = 1 });

        Assert.Equal(2, h.Transports.Count);
        Assert.True(old.Disposed);
        old.Receive(CTraderSessionRole.Quote, FixTestSupport.Parse(SecurityList));
        old.Receive(CTraderSessionRole.Quote, FixTestSupport.Parse(SpotW));
        Assert.False(h.Read().IsConnected);
    }

    [Fact]
    public async Task SwitchBackAndForth_CreatesFreshTransportEachTime()
    {
        var h = new Harness();
        await h.EnsureAsync("ctrader", ValidConfig());
        await h.EnsureAsync("mt5", ValidConfig());
        await h.EnsureAsync("ctrader", ValidConfig());

        Assert.Equal(2, h.Transports.Count);
        Assert.True(h.Transports[0].Disposed);
        Assert.False(h.Transports[1].Disposed);
    }

    [Fact]
    public async Task Dispose_StopsActiveSession()
    {
        var h = new Harness();
        var transport = await h.StreamingAsync();

        h.Session.Dispose();

        Assert.True(transport.Disposed);
        h.Session.EnsureState("ctrader", ValidConfig());
        Assert.Single(h.Transports);
    }

    [Fact]
    public async Task SpotIncremental_ClearsBook_LoggedOnce()
    {
        var h = new Harness();
        var transport = await h.StreamingAsync();
        const string x = "35=X|34=44|49=cServer|52=20260916-14:58:03.237|56=live.fxpro.8220816|262=MARKETDATAID|268=1|279=2|269=0|278=1|55=41";

        transport.Receive(CTraderSessionRole.Quote, FixTestSupport.Parse(x));
        transport.Receive(CTraderSessionRole.Quote, FixTestSupport.Parse(x));

        Assert.False(h.Read().IsConnected);
        Assert.Single(h.Logs, l => l.Contains("35=X", StringComparison.Ordinal));
    }

    [Fact]
    public async Task R8_StatsLineEveryMinute_WithTickAgeAndWouldSkipLatency()
    {
        var h = new Harness();
        var transport = await h.StreamingAsync();

        h.Read(confirmLatencyMs: 100);
        for (var i = 0; i < 20; i++)
        {
            h.Clock += 50;
            h.Read(confirmLatencyMs: 100);
        }

        h.Clock += 60_000;
        h.Read(confirmLatencyMs: 100);

        var stats = Assert.Single(h.Logs, l => l.Contains("[STATS]", StringComparison.Ordinal));
        Assert.Contains("tick_age_ms min=0", stats);
        Assert.Contains("confirm_latency_ms=100", stats);
        Assert.Contains("would_skip_latency_b=", stats);
        Assert.Contains("gap_buy_pts=57", stats);
        Assert.DoesNotContain(Password, stats);
    }

    private const string Heartbeat =
        "35=0|34=9|49=cServer|50=QUOTE|52=20260916-14:59:00.000|56=live.fxpro.8220816|57=QUOTE|112=live";

    [Fact]
    public async Task Liveness_QuietMarket_TestRequestAnswered_StaysConnected()
    {
        var h = new Harness();
        var transport = await h.StreamingAsync();

        h.Clock += 4_999;
        h.Read();
        Assert.DoesNotContain(transport.Calls, c => c == "send:Quote:1");

        h.Clock += 1;
        Assert.True(h.Read().IsConnected);
        Assert.Single(transport.Calls, c => c == "send:Quote:1");
        var probe = transport.Sent.Single(s => s.Message.Header.GetString(35) == "1").Message;

        h.Clock += 800;
        transport.Receive(CTraderSessionRole.Quote,
            FixTestSupport.Parse(Heartbeat.Replace("112=live", "112=" + probe.GetString(112))));
        h.Clock += 4_900;

        Assert.True(h.Read().IsConnected);
        Assert.Contains(h.Logs, l => l.Contains("trả lời TestRequest sau 800 ms", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Logs, l => l.Contains("TestRequest không phản hồi", StringComparison.Ordinal));

        h.Clock += 60_000;
        h.Read();
        Assert.Contains(h.Logs, l => l.Contains("probes_sent=2 probes_answered=1 probe_answer_max_ms=800", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Liveness_SilentNetworkLoss_FailsClosedWithin10s_ThenRecovers()
    {
        var h = new Harness();
        var transport = await h.StreamingAsync();

        h.Clock += 5_000;
        Assert.True(h.Read().IsConnected);

        h.Clock += 4_999;
        Assert.True(h.Read().IsConnected);

        h.Clock += 1;
        var stale = h.Read();
        Assert.False(stale.IsConnected);
        Assert.Contains("TestRequest", stale.Error);
        Assert.Null(stale.Bid);

        // Không lặp WARN khi vẫn im lặng; vẫn thăm dò định kỳ.
        h.Clock += 5_000;
        h.Read();
        h.Clock += 5_000;
        h.Read();
        Assert.Single(h.Logs, l => l.Contains("TestRequest không phản hồi", StringComparison.Ordinal));
        Assert.Equal(2, transport.Calls.Count(c => c == "send:Quote:1"));

        transport.Receive(CTraderSessionRole.Quote, FixTestSupport.Parse(Heartbeat));
        Assert.Contains(h.Logs, l => l.Contains("phản hồi trở lại", StringComparison.Ordinal));
        Assert.Equal("Book rỗng", h.Read().Error);

        transport.Receive(CTraderSessionRole.Quote, FixTestSupport.Parse(SpotW));
        Assert.True(h.Read().IsConnected);
    }

    [Fact]
    public async Task FailedReconnectAttempts_DoNotSpamLogoutEvents()
    {
        var h = new Harness();
        var transport = await h.StreamingAsync();

        transport.Logout(CTraderSessionRole.Quote);
        for (var i = 0; i < 5; i++)
        {
            transport.Logout(CTraderSessionRole.Quote);
        }

        Assert.Single(h.Events, e => e.Kind == CTraderQuoteEventKind.LoggedOut);
        Assert.Single(h.Logs, l => l.Contains("logged out", StringComparison.Ordinal));
        Assert.Single(h.Logs, l => l.Contains("reconnect chưa logon", StringComparison.Ordinal));

        transport.Logon(CTraderSessionRole.Quote);
        Assert.Contains(h.Logs, l => l.Contains("sau 5 lần reconnect hỏng", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Liveness_NoProbeBeforeLogon()
    {
        var h = new Harness();
        await h.EnsureAsync("ctrader", ValidConfig());

        h.Clock += 60_000;
        h.Read();

        Assert.DoesNotContain(h.Current.Calls, c => c.StartsWith("send", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CrossCheck_LoggedOncePerLogon()
    {
        var h = new Harness();
        await h.StreamingAsync();

        h.Read();
        h.Read();

        Assert.Single(h.Logs, l => l.Contains("Cross-check", StringComparison.Ordinal) && l.StartsWith("[CTRADER][INFO]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logs_NeverContainPassword()
    {
        var h = new Harness();
        var transport = await h.StreamingAsync();
        transport.Logout(CTraderSessionRole.Quote);
        await h.EnsureAsync("mt5", ValidConfig());

        Assert.NotEmpty(h.Logs);
        Assert.All(h.Logs, l => Assert.DoesNotContain(Password, l));
    }

    [Fact]
    public async Task Reader_UsesSessionForB_OnlyWhenPlatformIsCTrader_ResolvedEveryTick()
    {
        var provider = new MutableProvider { PlatformB = "mt5" };
        var session = new SpySession();
        var reader = new SharedMemoryMarketDataReader(provider, session);
        var snapshots = new List<SharedMemorySnapshot>();
        reader.SnapshotReceived += (_, s) => { lock (snapshots) snapshots.Add(s); };

        await reader.StartAsync();
        try
        {
            await WaitUntilAsync(() => session.EnsureCalls.Count > 2);
            Assert.All(session.EnsureCalls.ToArray(), c => Assert.Equal("mt5", c));
            Assert.Equal(0, session.ReadCalls);
            lock (snapshots)
            {
                Assert.NotEqual("SESSION", snapshots[^1].SanB.Symbol);
            }

            provider.PlatformB = "ctrader";
            await WaitUntilAsync(() => session.ReadCalls > 2);
            lock (snapshots)
            {
                Assert.Equal("SESSION", snapshots[^1].SanB.Symbol);
                Assert.True(snapshots[^1].SanB.IsConnected);
            }

            provider.PlatformB = "mt5";
            var readsAtSwitch = session.ReadCalls;
            await WaitUntilAsync(() => session.EnsureCalls.Count(c => c == "mt5") > 5);
            await Task.Delay(120);
            lock (snapshots)
            {
                Assert.NotEqual("SESSION", snapshots[^1].SanB.Symbol);
            }

            Assert.True(session.ReadCalls <= readsAtSwitch + 1);
        }
        finally
        {
            await reader.StopAsync();
        }
    }

    [Fact]
    public async Task Reader_WithoutSession_KeepsMmfPathForCTrader()
    {
        var provider = new MutableProvider { PlatformB = "ctrader" };
        var reader = new SharedMemoryMarketDataReader(provider);
        var tcs = new TaskCompletionSource<SharedMemorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.SnapshotReceived += (_, s) => tcs.TrySetResult(s);

        await reader.StartAsync();
        try
        {
            var snapshot = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(snapshot.SanB.IsConnected);
            Assert.Contains("Map", snapshot.SanB.Error);
        }
        finally
        {
            await reader.StopAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Hết thời gian chờ điều kiện");
            await Task.Delay(20);
        }
    }

    private sealed class SpySession : ICTraderQuoteSession
    {
        private int _readCalls;
        private readonly List<string> _ensureCalls = [];

        public event Action<CTraderQuoteSessionEvent>? EventRaised { add { } remove { } }
        public event Action<string>? LogLine { add { } remove { } }

        public string StatusText => "spy";

        public int ReadCalls => Volatile.Read(ref _readCalls);

        public List<string> EnsureCalls
        {
            get
            {
                lock (_ensureCalls)
                {
                    return [.. _ensureCalls];
                }
            }
        }

        public void EnsureState(string platformB, CTraderFixConfig config)
        {
            lock (_ensureCalls)
            {
                _ensureCalls.Add(platformB);
            }
        }

        public ExchangeMetrics Read(ExchangeMetrics exchangeA, int point, int confirmLatencyMs)
        {
            Interlocked.Increment(ref _readCalls);
            return new ExchangeMetrics("SESSION", 1m, 2m, 1m, 0, 1, "t", 0, 0, true, null);
        }
    }

    private sealed class MutableProvider : IRuntimeConfigProvider
    {
        private volatile string _platformB = "mt5";

        public string PlatformB
        {
            get => _platformB;
            set => _platformB = value;
        }

        public string CurrentPlatformB => _platformB;
        public CTraderFixConfig CurrentCTraderFixConfig => ValidConfig();
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
        public string CurrentMapName1 => "Local\\TEST_NO_SUCH_MAP_A";
        public string CurrentMapName2 => "Local\\TEST_NO_SUCH_MAP_B";
        public DashboardMetrics? CurrentDashboardMetrics => null;
    }
}
