using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.CTrader;
using TradeDesktop.Infrastructure.CTrader;

namespace TradeDesktop.Tests.CTrader;

// Phase 6 — lịch sử sàn B qua TRADE session: R2 (cùng bảng sức khoẻ trades), R3 (version riêng), R4 (ticket trùng
// trades), R10 (Profit tính lại, Commission 0), câu 2 (chỉ đóng hẳn), câu 4 (mất qua AP không fill → chỉ WARN).
public sealed class CTraderHistoryTests
{
    private const string HistoryMap = CTraderRoutingRules.HistoryMapName;
    private const string TradeMap = CTraderRoutingRules.TradeMapName;
    private const int Point = 100;
    private const string Header = "34=7|49=cServer|50=TRADE|52=20260919-10:00:00.000|56=live.fxpro.8220816|57=TRADE";

    private static readonly string SecurityList =
        $"35=y|{Header}|320=sec|322=responce:sec|560=0|146=1|55=41|1007=XAUUSD|1008=2";

    private static string Fill(string execId, long positionId, char side, decimal qty, decimal price, string time = "20260919-10:00:01.250")
        => $"35=8|{Header}|37=900|11=c-{execId}|17={execId}|150=F|39=2|55=41|54={side}|38={qty}|32={qty}|31={price}|151=0|14={qty}|6={price}|60={time}|721={positionId}";

    private static string NoPositions(string id) => $"35=AP|{Header}|710={id}|727=0|728=2";

    private sealed class Harness
    {
        public long Clock = 100_000;
        public long UnixMs = 1_789_800_000_000;
        public List<string> Logs { get; } = [];
        public List<FakeCTraderTransport> Transports { get; } = [];
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
        }

        public FakeCTraderTransport T => Transports[^1];

        public string LastPosReqId() => T.Sent.Last(s => s.Message.Header.GetString(35) == "AN").Message.GetString(710);

        public async Task SyncedEmptyAsync()
        {
            Session.EnsureState("ctrader", CTraderQuoteSessionTests.ValidConfig());
            await Session.WaitForLifecycleAsync();
            T.Logon(CTraderSessionRole.Trade);
            T.Receive(CTraderSessionRole.Trade, FixTestSupport.Parse(SecurityList));
            T.Receive(CTraderSessionRole.Trade, FixTestSupport.Parse(NoPositions(LastPosReqId())));
        }

        public void Receive(string body) => T.Receive(CTraderSessionRole.Trade, FixTestSupport.Parse(body));

        public SharedMapReadResult<HistorySharedRecord> History() => Session.ReadHistory(HistoryMap, true, Point);

        public SharedMapReadResult<TradeSharedRecord> Trades() => Session.ReadTrades(TradeMap, true);
    }

    [Fact]
    public async Task R2_HistoryMapNotFoundUntilSynced_ThenAvailableEmpty()
    {
        var h = new Harness();
        h.Session.EnsureState("ctrader", CTraderQuoteSessionTests.ValidConfig());
        await h.Session.WaitForLifecycleAsync();
        Assert.False(h.History().IsMapAvailable);

        h.T.Logon(CTraderSessionRole.Trade);
        h.Receive(SecurityList);
        var unsynced = h.History();
        Assert.False(unsynced.IsMapAvailable);
        Assert.Equal(0UL, unsynced.Timestamp);

        h.Receive(NoPositions(h.LastPosReqId()));
        var synced = h.History();
        Assert.True(synced.IsMapAvailable);
        Assert.True(synced.IsParseSuccess);
        Assert.Equal(0, synced.Count);
        Assert.Equal(0UL, synced.Timestamp);
        Assert.Equal(1, synced.Connected);
        Assert.Contains(h.Logs, l => l.Contains("history map AVAILABLE count=0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenThenFullClose_OneRecord_WithSyntheticFieldsAndStampedEaTime()
    {
        var h = new Harness();
        await h.SyncedEmptyAsync();

        h.Receive(Fill("e1", 4242, '1', 100, 4300.00m, "20260919-10:00:01.000"));
        Assert.Equal(0, h.History().Count);

        h.Clock = 123_456;
        h.Receive(Fill("e2", 4242, '2', 100, 4301.50m, "20260919-10:05:00.500"));

        var record = Assert.Single(h.History().Records);
        Assert.Equal(CTraderTicketCodec.Encode(4242), record.Ticket);
        Assert.Equal(0, record.TradeType);
        Assert.Equal(1.0, record.Volume, 6);
        Assert.Equal(4300.00, record.OpenPrice, 6);
        Assert.Equal(4301.50, record.ClosePrice, 6);
        Assert.Equal(0, record.Commission);
        Assert.Equal(150, record.Profit, 6);
        Assert.Equal(123_456UL, record.CloseEaTimeLocal);
        Assert.Equal((ulong)new DateTimeOffset(2026, 9, 19, 10, 5, 0, 500, TimeSpan.Zero).ToUnixTimeMilliseconds(), record.CloseTimeMsc);
        Assert.Equal("XAUUSD", record.Symbol);
        Assert.Empty(h.Trades().Records);
        Assert.Contains(h.Logs, l => l.Contains("History record positionId=4242", StringComparison.Ordinal) && l.Contains("TÍNH LẠI", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SellClosedHigher_ProfitNegative()
    {
        var h = new Harness();
        await h.SyncedEmptyAsync();

        h.Receive(Fill("e1", 7, '2', 100, 4300.00m));
        h.Receive(Fill("e2", 7, '1', 100, 4300.40m));

        var record = Assert.Single(h.History().Records);
        Assert.Equal(1, record.TradeType);
        Assert.Equal(-40, record.Profit, 6);
    }

    [Fact]
    public async Task R4_HistoryTicketEqualsTradeTicket()
    {
        var h = new Harness();
        await h.SyncedEmptyAsync();

        h.Receive(Fill("e1", 9001, '1', 100, 4300m));
        var tradeTicket = Assert.Single(h.Trades().Records).Ticket;
        h.Receive(Fill("e2", 9001, '2', 100, 4301m));

        Assert.Equal(tradeTicket, Assert.Single(h.History().Records).Ticket);
    }

    [Fact]
    public async Task PartialClose_NoRecordUntilFullyClosed()
    {
        var h = new Harness();
        await h.SyncedEmptyAsync();

        h.Receive(Fill("e1", 11, '1', 200, 4300m));
        h.Receive(Fill("e2", 11, '2', 100, 4301m));
        Assert.Empty(h.History().Records);
        Assert.Equal(1.0, Assert.Single(h.Trades().Records).Lot, 6);

        h.Receive(Fill("e3", 11, '2', 100, 4302m));
        Assert.Single(h.History().Records);
    }

    [Fact]
    public async Task FillForUnknownPosition_OpensNewPosition_NoHistory()
    {
        var h = new Harness();
        await h.SyncedEmptyAsync();

        h.Receive(Fill("e1", 55, '2', 100, 4300m));

        Assert.Empty(h.History().Records);
        Assert.Single(h.Trades().Records);
    }

    [Fact]
    public async Task DuplicateClosingFill_DoesNotDuplicateRecord()
    {
        var h = new Harness();
        await h.SyncedEmptyAsync();

        h.Receive(Fill("e1", 12, '1', 100, 4300m));
        h.Receive(Fill("e2", 12, '2', 100, 4301m));
        h.Receive(Fill("e2", 12, '2', 100, 4301m));

        Assert.Single(h.History().Records);
    }

    [Fact]
    public async Task R3_HistoryVersionIndependentFromTradesAndReconciliation()
    {
        var h = new Harness();
        await h.SyncedEmptyAsync();
        var historyVersion = h.History().Timestamp;

        h.Receive(Fill("e1", 13, '1', 100, 4300m));
        Assert.NotEqual(0UL, h.Trades().Timestamp);
        Assert.Equal(historyVersion, h.History().Timestamp);

        h.Clock += CTraderTradeSession.ReconcileIntervalMs;
        h.Session.Tick();
        h.Receive($"35=AP|{Header}|710={h.LastPosReqId()}|721=13|727=1|728=0|55=41|702=1|704=100|730=4300");
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(historyVersion, h.History().Timestamp);
        }

        h.Receive(Fill("e2", 13, '2', 100, 4301m));
        var afterClose = h.History().Timestamp;
        Assert.NotEqual(historyVersion, afterClose);
        Assert.Equal(afterClose, h.History().Timestamp);
    }

    [Fact]
    public async Task PositionVanishedViaReportWithoutFill_NoRecord_OneWarn()
    {
        var h = new Harness();
        await h.SyncedEmptyAsync();
        h.Receive(Fill("e1", 14, '1', 100, 4300m));

        h.Clock += CTraderTradeSession.ReconcileIntervalMs;
        h.Session.Tick();
        h.Receive(NoPositions(h.LastPosReqId()));

        Assert.Empty(h.History().Records);
        Assert.Empty(h.Trades().Records);
        Assert.Single(h.Logs, l => l.Contains("Position 14 biến mất qua PositionReport", StringComparison.Ordinal));
    }

    [Fact]
    public async Task R2_LogoutMapNotFoundImmediately_RecordsSurviveRelogon()
    {
        var h = new Harness();
        await h.SyncedEmptyAsync();
        h.Receive(Fill("e1", 15, '1', 100, 4300m));
        h.Receive(Fill("e2", 15, '2', 100, 4301m));
        Assert.Single(h.History().Records);

        h.T.Logout(CTraderSessionRole.Trade);
        Assert.False(h.History().IsMapAvailable);

        h.T.Logon(CTraderSessionRole.Trade);
        h.Receive(SecurityList);
        Assert.False(h.History().IsMapAvailable);

        h.Receive(NoPositions(h.LastPosReqId()));
        Assert.Single(h.History().Records);
    }

    [Fact]
    public async Task SwitchAway_HistoryMapNotFound_NewStartHasEmptyHistory()
    {
        var h = new Harness();
        await h.SyncedEmptyAsync();
        h.Receive(Fill("e1", 16, '1', 100, 4300m));
        h.Receive(Fill("e2", 16, '2', 100, 4301m));

        h.Session.EnsureState("mt5", CTraderQuoteSessionTests.ValidConfig());
        await h.Session.WaitForLifecycleAsync();
        Assert.False(h.History().IsMapAvailable);

        await h.SyncedEmptyAsync();
        Assert.Empty(h.History().Records);
    }

    [Fact]
    public void Decorator_NonCTraderMapOrPlatform_PassesThroughToMmf()
    {
        var inner = new SpyHistoryInner();
        var trade = new CTraderTradeSessionTests.SpyTradeSession();
        var provider = new CTraderTradeSessionTests.Provider { PlatformB = "mt5" };
        var reader = new CTraderAwareHistoryReader(inner, provider, trade, new CTraderTradeSessionTests.QuoteStub());

        reader.ReadHistory("Local\\MT_A_History");
        reader.ReadHistory(HistoryMap);
        provider.PlatformB = "ctrader";
        reader.ReadHistory("Local\\MT_A_History");

        Assert.Equal(["Local\\MT_A_History", HistoryMap, "Local\\MT_A_History"], inner.Calls);
        Assert.Equal(0, trade.HistoryReadCalls);
        Assert.Empty(trade.EnsureCalls);
    }

    [Fact]
    public void Decorator_CTraderMap_UsesSessionWithPointAndQuoteState()
    {
        var inner = new SpyHistoryInner();
        var trade = new CTraderTradeSessionTests.SpyTradeSession();
        var reader = new CTraderAwareHistoryReader(
            inner, new CTraderTradeSessionTests.Provider { PlatformB = "ctrader" }, trade,
            new CTraderTradeSessionTests.QuoteStub { LoggedOn = true });

        var result = reader.ReadHistory(HistoryMap);

        Assert.Empty(inner.Calls);
        Assert.Equal(1, trade.HistoryReadCalls);
        Assert.Equal(100, trade.LastPoint);
        Assert.True(trade.LastQuoteLoggedOn);
        Assert.Equal("history-session", result.ErrorMessage);
    }

    private sealed class SpyHistoryInner : IHistorySharedMemoryReader
    {
        public List<string> Calls { get; } = [];

        public SharedMapReadResult<HistorySharedRecord> ReadHistory(string mapName)
        {
            Calls.Add(mapName);
            return SharedMapReadResult<HistorySharedRecord>.MapNotFound(mapName);
        }
    }
}
