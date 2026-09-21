using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.CTrader;
using TradeDesktop.Domain.Models;
using TradeDesktop.Infrastructure.CTrader;

namespace TradeDesktop.Tests.CTrader;

public sealed class CTraderTradeSessionTests
{
    private const string MapName = CTraderRoutingRules.TradeMapName;
    private const string Header = "34=5|49=cServer|50=TRADE|52=20260917-10:00:00.000|56=live.fxpro.8220816|57=TRADE";

    private static readonly string SecurityList =
        $"35=y|{Header}|320=sec|322=responce:sec|560=0|146=2|55=1|1007=EURUSD|1008=5|55=41|1007=XAUUSD|1008=2";

    private static string NoPositions(string posReqId) => $"35=AP|{Header}|710={posReqId}|727=0|728=2";

    private sealed class Harness
    {
        public long Clock = 50_000;
        public long UnixMs = 1_789_000_000_000;
        public List<FakeCTraderTransport> Transports { get; } = [];
        public List<string> Logs { get; } = [];
        public List<CTraderTradeSessionEvent> Events { get; } = [];
        public CTraderTradeSession Session { get; }

        public Harness()
        {
            Session = new CTraderTradeSession(_ =>
            {
                var t = new FakeCTraderTransport();
                Transports.Add(t);
                return t;
            }, () => Clock, () => UnixMs++, startTimer: false);
            Session.LogLine += l => { lock (Logs) Logs.Add(l); };
            Session.EventRaised += e => { lock (Events) Events.Add(e); };
        }

        public FakeCTraderTransport Current => Transports[^1];

        public async Task EnsureAsync(string platformB)
        {
            Session.EnsureState(platformB, CTraderQuoteSessionTests.ValidConfig());
            await Session.WaitForLifecycleAsync();
        }

        public string LastPosReqId()
            => Current.Sent.Last(s => s.Message.Header.GetString(35) == "AN").Message.GetString(710);

        public async Task<FakeCTraderTransport> SyncedEmptyAsync()
        {
            await EnsureAsync("ctrader");
            Current.Logon(CTraderSessionRole.Trade);
            Current.Receive(CTraderSessionRole.Trade, FixTestSupport.Parse(SecurityList));
            Current.Receive(CTraderSessionRole.Trade, FixTestSupport.Parse(NoPositions(LastPosReqId())));
            return Current;
        }

        public SharedMapReadResult<TradeSharedRecord> Read(bool quote = true) => Session.ReadTrades(MapName, quote);
    }

    [Theory]
    [InlineData("mt5")]
    [InlineData("mt4")]
    public async Task G4_NotCTrader_NeverOpensTradeTransport(string platformB)
    {
        var h = new Harness();

        await h.EnsureAsync(platformB);

        Assert.Empty(h.Transports);
        Assert.False(h.Read().IsMapAvailable);
    }

    [Fact]
    public async Task CTrader_StartsTradeRoleOnly_Once()
    {
        var h = new Harness();

        await h.EnsureAsync("ctrader");
        await h.EnsureAsync("ctrader");

        var transport = Assert.Single(h.Transports);
        Assert.Equal(["start:Trade"], transport.Calls);
    }

    [Fact]
    public async Task Logon_SendsSecurityListAndPositionsRequest_NeverAnOrder()
    {
        var h = new Harness();
        await h.EnsureAsync("ctrader");

        h.Current.Logon(CTraderSessionRole.Trade);

        Assert.Equal(["send:Trade:x", "send:Trade:AN"], h.Current.Calls.Where(c => c.StartsWith("send", StringComparison.Ordinal)));
        var request = h.Current.Sent[1].Message;
        Assert.StartsWith("pos-", request.GetString(710));
        Assert.False(request.IsSetField(721));
        Assert.DoesNotContain(h.Current.Calls, c => c.EndsWith(":D", StringComparison.Ordinal));
        Assert.Contains(h.Logs, l => l.StartsWith("[CTRADER][TRADE][WARN] PositionsSynced=false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task R2_UnsyncedWindow_IsMapNotFound_NeverAvailableWithZeroCount()
    {
        var h = new Harness();
        await h.EnsureAsync("ctrader");
        Assert.False(h.Read().IsMapAvailable);

        h.Current.Logon(CTraderSessionRole.Trade);
        Assert.False(h.Read().IsMapAvailable);

        h.Current.Receive(CTraderSessionRole.Trade, FixTestSupport.Parse(SecurityList));
        var unsynced = h.Read();

        Assert.False(unsynced.IsMapAvailable);
        Assert.False(unsynced.IsParseSuccess);
        Assert.Equal(0UL, unsynced.Timestamp);
    }

    [Fact]
    public async Task R2_NoPositions728Eq2_IsSyncedEmpty_Available()
    {
        var h = new Harness();
        await h.SyncedEmptyAsync();

        var result = h.Read();

        Assert.True(result.IsMapAvailable);
        Assert.True(result.IsParseSuccess);
        Assert.Equal(0, result.Count);
        Assert.Empty(result.Records);
        Assert.Equal(1, result.Connected);
        Assert.Contains(h.Events, e => e.Kind == CTraderTradeEventKind.PositionsSynced);
    }

    [Fact]
    public async Task Connected_IsZeroWhenQuoteDown_NeverMinusOne()
    {
        var h = new Harness();
        await h.SyncedEmptyAsync();

        var result = h.Read(quote: false);

        Assert.True(result.IsMapAvailable);
        Assert.Equal(0, result.Connected);
    }

    [Fact]
    public async Task R2_SyncedButSymbolNotResolved_IsMapNotFound()
    {
        var h = new Harness();
        await h.EnsureAsync("ctrader");
        h.Current.Logon(CTraderSessionRole.Trade);
        h.Current.Receive(CTraderSessionRole.Trade, FixTestSupport.Parse(NoPositions(h.LastPosReqId())));

        Assert.False(h.Read().IsMapAvailable);
    }

    [Fact]
    public async Task R2_Logout_MapNotFoundImmediately_RelogonNeedsFreshBatch()
    {
        var h = new Harness();
        var transport = await h.SyncedEmptyAsync();
        Assert.True(h.Read().IsMapAvailable);

        transport.Logout(CTraderSessionRole.Trade);
        Assert.False(h.Read().IsMapAvailable);
        Assert.Contains(h.Events, e => e.Kind == CTraderTradeEventKind.LoggedOut);

        transport.Logon(CTraderSessionRole.Trade);
        transport.Receive(CTraderSessionRole.Trade, FixTestSupport.Parse(SecurityList));
        Assert.False(h.Read().IsMapAvailable);

        transport.Receive(CTraderSessionRole.Trade, FixTestSupport.Parse(NoPositions(h.LastPosReqId())));
        Assert.True(h.Read().IsMapAvailable);
    }

    [Fact]
    public async Task R3_EmptyList_VersionStableAcrossReadsAndReconciliation()
    {
        var h = new Harness();
        var transport = await h.SyncedEmptyAsync();
        var version = h.Read().Timestamp;

        for (var i = 0; i < 600; i++)
        {
            Assert.Equal(version, h.Read().Timestamp);
        }

        var firstId = h.LastPosReqId();
        h.Clock += CTraderTradeSession.ReconcileIntervalMs;
        h.Session.Tick();
        var secondId = h.LastPosReqId();
        Assert.NotEqual(firstId, secondId);

        transport.Receive(CTraderSessionRole.Trade, FixTestSupport.Parse(NoPositions(secondId)));
        Assert.Equal(version, h.Read().Timestamp);
        Assert.True(h.Read().IsMapAvailable);
    }

    [Fact]
    public async Task Reconciliation_NotSentBefore60s()
    {
        var h = new Harness();
        var transport = await h.SyncedEmptyAsync();

        h.Clock += CTraderTradeSession.ReconcileIntervalMs - 1;
        h.Session.Tick();

        Assert.Single(transport.Calls, c => c == "send:Trade:AN");
    }

    [Fact]
    public async Task NotSynced60s_RaisesEventOncePerLogon()
    {
        var h = new Harness();
        await h.EnsureAsync("ctrader");
        h.Current.Logon(CTraderSessionRole.Trade);

        h.Clock += CTraderTradeSession.NotSyncedAlertMs - 1;
        h.Session.Tick();
        Assert.DoesNotContain(h.Events, e => e.Kind == CTraderTradeEventKind.PositionsNotSynced);

        h.Clock += 1;
        h.Session.Tick();
        h.Clock += 5_000;
        h.Session.Tick();

        Assert.Single(h.Events, e => e.Kind == CTraderTradeEventKind.PositionsNotSynced);
    }

    [Fact]
    public async Task R4_PositionFromReport_EncodedTicket_SymbolFromSecurityList_EaTimeStamped()
    {
        var h = new Harness();
        await h.EnsureAsync("ctrader");
        h.Current.Logon(CTraderSessionRole.Trade);
        h.Current.Receive(CTraderSessionRole.Trade, FixTestSupport.Parse(SecurityList));
        h.Clock = 777_000;
        h.Current.Receive(CTraderSessionRole.Trade, FixTestSupport.Parse(
            $"35=AP|{Header}|710={h.LastPosReqId()}|721=987654|727=1|728=0|55=41|702=1|704=0|705=100|730=4310.5"));

        var record = Assert.Single(h.Read().Records);

        Assert.Equal(CTraderTicketCodec.Encode(987654), record.Ticket);
        Assert.True(CTraderTicketCodec.TryDecode(record.Ticket, out var positionId));
        Assert.Equal(987654, positionId);
        Assert.Equal("XAUUSD", record.Symbol);
        Assert.Equal(1, record.TradeType);
        Assert.Equal(1.0, record.Lot, 6);
        Assert.Equal(4310.5, record.Price, 6);
        Assert.Equal(777_000UL, record.OpenEaTimeLocal);
        Assert.Equal(0, record.Sl);
        Assert.Equal(0, record.Tp);
    }

    [Fact]
    public async Task SwitchAway_StopsTrade_MapNotFound()
    {
        var h = new Harness();
        var transport = await h.SyncedEmptyAsync();

        await h.EnsureAsync("mt5");

        Assert.Contains("stop:Trade", transport.Calls);
        Assert.True(transport.Disposed);
        Assert.False(h.Read().IsMapAvailable);
        Assert.DoesNotContain(h.Events, e => e.Kind == CTraderTradeEventKind.LoggedOut);
    }

    [Fact]
    public async Task Logs_NeverContainPassword()
    {
        var h = new Harness();
        var transport = await h.SyncedEmptyAsync();
        transport.Logout(CTraderSessionRole.Trade);
        await h.EnsureAsync("mt5");

        Assert.NotEmpty(h.Logs);
        Assert.All(h.Logs, l => Assert.DoesNotContain("Pw-Test-Only", l));
    }

    [Fact]
    public void Cache_ReconciliationKeepsEaStampOfKnownPosition_StampsNewOnes()
    {
        long clock = 1_000;
        var cache = new CTraderPositionCache(() => clock);
        cache.ApplyPositionReport(new CTraderPositionReport("r1", 1, 0, new CTraderPosition(1, 41, true, 100, 4300, 0, 0)));
        Assert.Equal(1_000UL, cache.Positions.Single().OpenEaTimeLocal);

        clock = 9_000;
        cache.ApplyPositionReport(new CTraderPositionReport("r2", 2, 0, new CTraderPosition(1, 41, true, 100, 4300, 0, 0)));
        cache.ApplyPositionReport(new CTraderPositionReport("r2", 2, 0, new CTraderPosition(2, 41, false, 100, 4301, 0, 0)));

        Assert.Equal(1_000UL, cache.Positions.Single(p => p.PositionId == 1).OpenEaTimeLocal);
        Assert.Equal(9_000UL, cache.Positions.Single(p => p.PositionId == 2).OpenEaTimeLocal);
    }

    [Fact]
    public void Decorator_NonCTraderMapOrPlatform_PassesThroughToMmf()
    {
        var inner = new SpyInner();
        var trade = new SpyTradeSession();
        var provider = new Provider { PlatformB = "mt5" };
        var reader = new CTraderAwareTradesReader(inner, provider, trade, new QuoteStub());

        reader.ReadTrades("Local\\MT_A_Trades");
        reader.ReadTrades(MapName);
        provider.PlatformB = "ctrader";
        reader.ReadTrades("Local\\MT_A_Trades");

        Assert.Equal(["Local\\MT_A_Trades", MapName, "Local\\MT_A_Trades"], inner.Calls);
        Assert.Equal(0, trade.ReadCalls);
        Assert.Equal(["mt5", "mt5", "ctrader"], trade.EnsureCalls);
    }

    [Fact]
    public void Decorator_CTraderMap_UsesSessionWithQuoteState()
    {
        var inner = new SpyInner();
        var trade = new SpyTradeSession();
        var reader = new CTraderAwareTradesReader(inner, new Provider { PlatformB = "ctrader" }, trade, new QuoteStub { LoggedOn = true });

        var result = reader.ReadTrades(MapName);

        Assert.Empty(inner.Calls);
        Assert.Equal(1, trade.ReadCalls);
        Assert.True(trade.LastQuoteLoggedOn);
        Assert.Equal("session", result.ErrorMessage);
    }

    private sealed class SpyInner : ITradesSharedMemoryReader
    {
        public List<string> Calls { get; } = [];

        public SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName)
        {
            Calls.Add(mapName);
            return SharedMapReadResult<TradeSharedRecord>.MapNotFound(mapName);
        }
    }

    internal sealed class SpyTradeSession : ICTraderTradeSession
    {
        public event Action<CTraderTradeSessionEvent>? EventRaised { add { } remove { } }
        public event Action<string>? LogLine { add { } remove { } }
        public string StatusText => "spy";
        public List<string> EnsureCalls { get; } = [];
        public int ReadCalls { get; private set; }
        public bool LastQuoteLoggedOn { get; private set; }

        public void EnsureState(string platformB, CTraderFixConfig config) => EnsureCalls.Add(platformB);

        public SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName, bool quoteLoggedOn)
        {
            ReadCalls++;
            LastQuoteLoggedOn = quoteLoggedOn;
            return SharedMapReadResult<TradeSharedRecord>.ParseError("session");
        }

        public int HistoryReadCalls { get; private set; }
        public int LastPoint { get; private set; }

        public SharedMapReadResult<HistorySharedRecord> ReadHistory(string mapName, bool quoteLoggedOn, int point)
        {
            HistoryReadCalls++;
            LastQuoteLoggedOn = quoteLoggedOn;
            LastPoint = point;
            return SharedMapReadResult<HistorySharedRecord>.ParseError("history-session");
        }
    }

    internal sealed class QuoteStub : ICTraderQuoteSession
    {
        public bool LoggedOn { get; init; }
        public event Action<CTraderQuoteSessionEvent>? EventRaised { add { } remove { } }
        public event Action<string>? LogLine { add { } remove { } }
        public string StatusText => "stub";
        public bool IsQuoteLoggedOn => LoggedOn;
        public void EnsureState(string platformB, CTraderFixConfig config) { }
        public ExchangeMetrics Read(ExchangeMetrics exchangeA, int point, int confirmLatencyMs) => exchangeA;
    }

    internal sealed class Provider : IRuntimeConfigProvider
    {
        public string PlatformB { get; set; } = "mt5";
        public string CurrentPlatformB => PlatformB;
        public CTraderFixConfig CurrentCTraderFixConfig => CTraderQuoteSessionTests.ValidConfig();
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
        public string CurrentMapName1 => "Local\\MT_A_Tick";
        public string CurrentMapName2 => "CTRADER_B";
        public DashboardMetrics? CurrentDashboardMetrics => null;
    }
}
