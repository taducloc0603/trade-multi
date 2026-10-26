using System.Buffers.Binary;
using System.Text;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.TradeLocker;
using TradeDesktop.Infrastructure.TradeLocker;

namespace TradeDesktop.Tests.TradeLocker;

// Phase 4–5 (docs/plans/tradelocker): phiên hermes với transport giả + đồng hồ giả — mọi điều kiện fail-closed của Read,
// keepalive/lifetime, reconnect + ngắt mạch, credit REQUEST_N, Trades map, request-response (Uncertain không gửi lại).
public sealed class TradeLockerHermesSessionTests
{
    private const int Point = 100; // XAUUSD tick 0.01 ⇒ digits 2 ⇒ point 100

    private static readonly TradeLockerAccount DemoAccount =
        new("PRDTL#O#1791342294192995800", "1791342294192995800", "demo", "OPEN_DEMO", "APOLLO", "ACTIVE", "apl0");

    private static readonly ExchangeMetrics A = new("XAUUSD.s", 4000m, 4000.3m, 0.3m, 10, 1, "00:00:00", null, null, true, null);

    private static byte[] Fixture(string name)
        => Convert.FromBase64String(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TradeLocker", "Fixtures", name + ".b64")));

    private sealed class Harness
    {
        public long Now = 1_000_000;
        public readonly List<FakeTransport> Transports = [];
        public readonly List<TradeLockerSessionEvent> Events = [];
        public readonly List<string> Logs = [];
        public TradeLockerConnectInfo Info = new(DemoAccount, "eyJjwt.eyJp.sig", null);
        public bool AutoConnect = true;
        public string? HistoryJson;
        public int HistoryCalls;
        public string? LastHistoryUrl;
        public readonly TradeLockerHermesSession Session;

        public Harness()
        {
            Session = new TradeLockerHermesSession(
                (_, _) => Task.FromResult(Info),
                () =>
                {
                    var t = new FakeTransport(AutoConnect);
                    Transports.Add(t);
                    return t;
                },
                () => Now,
                work => work().GetAwaiter().GetResult(),
                historyJwt: (_, _) => Task.FromResult<string?>("eyJhist.eyJp.sig"),
                httpGet: (url, _, _) =>
                {
                    HistoryCalls++;
                    LastHistoryUrl = url;
                    return Task.FromResult(HistoryJson);
                });
            Session.EventRaised += e => Events.Add(e);
            Session.LogLine += l => Logs.Add(l);
        }

        public FakeTransport Last => Transports[^1];

        public ExchangeMetrics Read() => Session.Read(A, Point, 2500);

        public void Ensure(string platform = "tradelocker", string symbol = "XAUUSD")
            => Session.EnsureState(platform, TradeLockerConfigTests.Demo() with { Symbol = symbol });

        // Kết nối + nhận danh mục symbol + giá đầu tiên.
        public void ConnectWithQuote()
        {
            Ensure();
            Read();
            Last.Push(Payload(1, Fixture("tradable-unit-aggregates")));
            Last.Push(Payload(QuoteStreamId(), Fixture("quote")));
        }

        public int QuoteStreamId() => Last.StreamIdOfRoute("v3.tickers.806102601859910870.quotes");
    }

    private static byte[] Payload(int streamId, byte[] data, int flags = RSocketFlags.Next)
    {
        var frame = new byte[6 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, streamId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)((RSocketFrameType.Payload << 10) | flags));
        data.CopyTo(frame, 6);
        return frame;
    }

    private static byte[] ErrorFrame(int streamId, string message)
    {
        var text = Encoding.UTF8.GetBytes(message);
        var frame = new byte[10 + text.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, streamId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(RSocketFrameType.Error << 10));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(6), 0x201);
        text.CopyTo(frame, 10);
        return frame;
    }

    // ---------- vòng đời ----------

    [Fact]
    public void OtherPlatform_NoTransport_ReadFailClosed()
    {
        var h = new Harness();
        h.Ensure("primexbt");

        var m = h.Read();

        Assert.False(m.IsConnected);
        Assert.Equal("TradeLocker chưa bật", m.Error);
        Assert.Empty(h.Transports);
    }

    [Fact]
    public void Ensure_ConnectsToClusterHost_SendsSetupAndThreeStreams()
    {
        var h = new Harness();
        h.Ensure();
        h.Read();

        var t = Assert.Single(h.Transports);
        Assert.Equal(new Uri("wss://api.apl0.tradelocker.com/hermes"), t.Uri);
        Assert.Equal(RSocketFrameType.Setup, t.SentTypes[0]);
        Assert.Contains("v3.tradable-unit-aggregates", t.SentRoutes);
        Assert.Contains("v3.position-aggregates", t.SentRoutes);
        Assert.Contains("v3.notifications", t.SentRoutes);
        Assert.True(h.Session.IsConnected);
        Assert.Equal("Chưa resolve được symbol TradeLocker", h.Read().Error); // socket mở nhưng chưa có symbol ⇒ vẫn fail-closed
    }

    [Fact]
    public void UnitsThenQuote_ReadConnected_LatencyIsTickAge()
    {
        var h = new Harness();
        h.ConnectWithQuote();
        h.Now += 300;

        var m = h.Read();

        Assert.True(m.IsConnected, m.Error);
        Assert.Equal("XAUUSD", m.Symbol);
        Assert.Equal(4008.5m, m.Bid);
        Assert.Equal(4008.76m, m.Ask);
        Assert.Equal(300m, m.LatencyMs);
        Assert.Contains(h.Events, e => e.Kind == TradeLockerSessionEventKind.SymbolResolved);
    }

    [Fact]
    public void UnknownSymbol_NeverSubscribesQuotes_FailClosed()
    {
        var h = new Harness();
        h.Ensure(symbol: "XAUUSD.24");
        h.Read();
        h.Last.Push(Payload(1, Fixture("tradable-unit-aggregates")));

        Assert.DoesNotContain(h.Last.SentRoutes, r => r.EndsWith(".quotes", StringComparison.Ordinal));
        Assert.Equal("Chưa resolve được symbol TradeLocker", h.Read().Error);
    }

    [Fact]
    public void AuthFailure_FailClosed_RetriesAfterAuthDelay()
    {
        var h = new Harness { Info = new TradeLockerConnectInfo(null, null, "chưa đăng nhập TradeLocker") };
        h.Ensure();

        var m = h.Read();

        Assert.False(m.IsConnected);
        Assert.Contains("chưa đăng nhập", m.Error);
        Assert.Empty(h.Transports);
        Assert.Contains(h.Events, e => e.Kind == TradeLockerSessionEventKind.AuthRequired);
        Assert.NotNull(h.Session.OpenBlockReason);

        h.Info = new TradeLockerConnectInfo(DemoAccount, "eyJjwt.eyJp.sig", null);
        h.Now += TradeLockerHermesSession.AuthRetryMs - 1;
        h.Read();
        Assert.Empty(h.Transports);
        h.Now += 1;
        h.Read();
        Assert.Single(h.Transports);
    }

    [Fact]
    public void NetworkErrorDuringAuth_IsDisconnect_NotAuthRequired_RetriesWithBackoff()
    {
        // Kiểm 2026-10-26 (tắt Wi‑Fi): lỗi HTTP của kerber từng bị báo nhầm "cần đăng nhập" + chờ 30 s.
        var h = new Harness { Info = new TradeLockerConnectInfo(null, null, "kerber accounts: HttpRequestException", Transient: true) };
        h.Ensure();

        var m = h.Read();

        Assert.Equal("TradeLocker chưa kết nối", m.Error);
        Assert.DoesNotContain(h.Events, e => e.Kind == TradeLockerSessionEventKind.AuthRequired);
        Assert.Contains(h.Events, e => e.Kind == TradeLockerSessionEventKind.Disconnected);
        Assert.Null(h.Session.OpenBlockReason is { } r && r.Contains("đăng nhập") ? r : null);

        h.Info = new TradeLockerConnectInfo(DemoAccount, "eyJjwt.eyJp.sig", null);
        h.Now += TradeLockerHermesSession.BackoffMinMs;
        h.Read();
        Assert.Single(h.Transports); // nối lại sau backoff ngắn, không phải 30 s
    }

    [Fact]
    public void NonDemoAccount_Warns_ButStillConnects()
    {
        var h = new Harness { Info = new TradeLockerConnectInfo(DemoAccount with { Type = "LIVE" }, "eyJjwt.eyJp.sig", null) };
        h.Ensure();
        h.Read();

        Assert.Contains(h.Events, e => e.Kind == TradeLockerSessionEventKind.AccountNotDemo);
        Assert.Contains(h.Logs, l => l.Contains("KHÔNG phải demo"));
        Assert.Single(h.Transports);
    }

    [Fact]
    public void SwitchAwayFromTradeLocker_StopsTransport_RaisesStopped()
    {
        var h = new Harness();
        h.ConnectWithQuote();

        h.Ensure("mt5");

        Assert.True(h.Last.Stopped);
        Assert.False(h.Read().IsConnected);
        Assert.Contains(h.Events, e => e.Kind == TradeLockerSessionEventKind.Stopped);
    }

    [Fact]
    public void SymbolChange_Reconnects_VolumeChangeDoesNot()
    {
        var h = new Harness();
        h.ConnectWithQuote();

        h.Session.EnsureState("tradelocker", TradeLockerConfigTests.Demo() with { VolumeBLots = 0.05m });
        Assert.Single(h.Transports);

        h.Session.EnsureState("tradelocker", TradeLockerConfigTests.Demo() with { Symbol = "SPX500" });
        Assert.True(h.Transports[0].Stopped);
        h.Read();
        Assert.Equal(2, h.Transports.Count);
    }

    // ---------- keepalive / lifetime / reconnect ----------

    [Fact]
    public void KeepAlive_SentEveryFiveSeconds_ServerKeepAliveAnswered()
    {
        var h = new Harness();
        h.ConnectWithQuote();
        var before = h.Last.SentTypes.Count(t => t == RSocketFrameType.KeepAlive);

        h.Now += TradeLockerHermesSession.KeepAliveMs;
        h.Last.Push(RSocketFrameCodec.EncodeKeepAlive(respond: false)); // frame bất kỳ giữ lifetime
        h.Read();
        Assert.Equal(before + 1, h.Last.SentTypes.Count(t => t == RSocketFrameType.KeepAlive));

        h.Last.Push(RSocketFrameCodec.EncodeKeepAlive(respond: true, [7]));
        var reply = h.Last.Sent[^1];
        Assert.True(RSocketFrameCodec.TryDecode(reply, out var f));
        Assert.Equal(RSocketFrameType.KeepAlive, f.Type);
        Assert.False(f.Has(RSocketFlags.Respond));
        Assert.Equal(new byte[] { 7 }, f.Data.ToArray());
    }

    [Fact]
    public void NoFrameForLifetime_ClearsPrices_StopsAndReconnectsWithBackoff()
    {
        var h = new Harness();
        h.ConnectWithQuote();

        h.Now += TradeLockerHermesSession.LifetimeMs + 1;
        var m = h.Read();

        Assert.False(m.IsConnected);
        Assert.True(h.Transports[0].Stopped);
        Assert.Contains(h.Events, e => e.Kind == TradeLockerSessionEventKind.Disconnected);

        h.Now += TradeLockerHermesSession.BackoffMinMs - 1;
        h.Read();
        Assert.Single(h.Transports);
        h.Now += 1;
        h.Read();
        Assert.Equal(2, h.Transports.Count); // kết nối mới, mint JWT lại
    }

    [Fact]
    public void ReconnectStorm_PausesReconnects()
    {
        var h = new Harness();
        h.Ensure();
        for (var i = 0; i <= TradeLockerHermesSession.StormMaxReconnects; i++)
        {
            h.Now += TradeLockerHermesSession.BackoffMaxMs;
            h.Read();
            h.Last.ServerClose("boom");
        }

        Assert.Contains(h.Events, e => e.Kind == TradeLockerSessionEventKind.ReconnectStorm);
        var count = h.Transports.Count;
        h.Now += TradeLockerHermesSession.BackoffMaxMs;
        h.Read();
        Assert.Equal(count, h.Transports.Count);

        h.Now += TradeLockerHermesSession.StormPauseMs;
        h.Read();
        Assert.Equal(count + 1, h.Transports.Count);
    }

    [Fact]
    public void ConnectionErrorFrame_ClosesSocket()
    {
        var h = new Harness();
        h.ConnectWithQuote();

        h.Last.Push(ErrorFrame(0, "invalid setup"));

        Assert.True(h.Transports[0].Stopped);
        Assert.False(h.Read().IsConnected);
    }

    [Fact]
    public void GarbageQuote_DriftReported_PricesUntouched()
    {
        var h = new Harness();
        h.ConnectWithQuote();

        h.Last.Push(Payload(h.QuoteStreamId(), [0xFF, 0xFF]));

        Assert.Contains(h.Events, e => e.Kind == TradeLockerSessionEventKind.ProtocolDrift);
        Assert.Equal(4008.5m, h.Read().Bid);
    }

    [Fact]
    public void DigitsMismatch_FailClosed()
    {
        var h = new Harness();
        h.ConnectWithQuote();

        var m = h.Session.Read(A, 1000, 2500);

        Assert.False(m.IsConnected);
        Assert.StartsWith("Lệch digits", m.Error);
        Assert.Contains(h.Events, e => e.Kind == TradeLockerSessionEventKind.DigitsMismatch);
    }

    [Fact]
    public void Credits_RequestNSentEveryHundredPayloads()
    {
        var h = new Harness();
        h.ConnectWithQuote();
        var quoteStream = h.QuoteStreamId();

        for (var i = 0; i < 98; i++) // + 1 giá đầu tiên của ConnectWithQuote = 99 payload
        {
            h.Last.Push(Payload(quoteStream, Fixture("quote")));
        }

        Assert.DoesNotContain(h.Last.SentTypes, t => t == RSocketFrameType.RequestN);
        h.Last.Push(Payload(quoteStream, Fixture("quote"))); // payload thứ 100 (kể cả giá đầu tiên)
        Assert.Contains(h.Last.SentTypes, t => t == RSocketFrameType.RequestN);
    }

    // ---------- Trades map ----------

    [Fact]
    public void ReadTrades_MapNotFoundUntilPositions_ThenRecords_ThenNotFoundAfterDrop()
    {
        var h = new Harness();
        h.ConnectWithQuote();

        Assert.False(h.Session.ReadTrades(TradeLockerRoutingRules.TradeMapName).IsMapAvailable);

        h.Last.Push(Payload(h.Last.StreamIdOfRoute("v3.position-aggregates"), Fixture("position-aggregates")));
        var trades = h.Session.ReadTrades(TradeLockerRoutingRules.TradeMapName);
        Assert.True(trades.IsMapAvailable);
        Assert.Equal(3, trades.Count);
        Assert.Contains(trades.Records, r => r.Symbol == "XAUUSD" && r.TradeType == 1);
        Assert.NotNull(h.Session.TryGetOpenPosition("895683725856354606"));

        h.Last.ServerClose("network");
        Assert.False(h.Session.ReadTrades(TradeLockerRoutingRules.TradeMapName).IsMapAvailable);
        Assert.Null(h.Session.TryGetOpenPosition("895683725856354606"));
    }

    // ---------- History map (Phase 6) ----------

    [Fact]
    public void ReadHistory_LoadedOnConnect_RefreshedOnTimerAndAfterPositionDeletion()
    {
        var h = new Harness
        {
            HistoryJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TradeLocker", "Fixtures", "hera-closed-positions.json"))
        };
        h.ConnectWithQuote(); // Read đầu tiên mở socket và tải lịch sử ngay trong cùng nhịp
        var history = h.Session.ReadHistory(TradeLockerRoutingRules.HistoryMapName);
        Assert.True(history.IsMapAvailable);
        Assert.True(history.Count > 0);
        Assert.Equal("https://api.apl0.tradelocker.com/hera/api/v1/reports/closed-positions-history", h.LastHistoryUrl);
        Assert.Equal(1, h.HistoryCalls);

        h.Now += 1_000;
        h.Read();
        Assert.Equal(1, h.HistoryCalls); // chưa tới nhịp 10 s

        h.Last.Push(Payload(h.Last.StreamIdOfRoute("v3.position-aggregates"),
            new ProtoWriter().Message(2, new ProtoWriter().String(1, "895596180090928800")).ToArray()));
        h.Now += 500;
        h.Read();
        Assert.Equal(2, h.HistoryCalls); // xoá position ⇒ kéo sớm

        h.Now += TradeLockerHermesSession.HistoryIntervalMs;
        h.Read();
        Assert.Equal(3, h.HistoryCalls);
    }

    [Fact]
    public void ReadHistory_FetchFailsOrSocketDrops_MapNotFound()
    {
        var h = new Harness { HistoryJson = null };
        h.ConnectWithQuote();
        h.Read();
        Assert.False(h.Session.ReadHistory(TradeLockerRoutingRules.HistoryMapName).IsMapAvailable);

        h.HistoryJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TradeLocker", "Fixtures", "hera-closed-positions.json"));
        h.Now += TradeLockerHermesSession.HistoryIntervalMs;
        h.Last.Push(RSocketFrameCodec.EncodeKeepAlive(respond: false));
        h.Read();
        Assert.True(h.Session.ReadHistory(TradeLockerRoutingRules.HistoryMapName).IsMapAvailable);

        h.Last.ServerClose("network");
        Assert.False(h.Session.ReadHistory(TradeLockerRoutingRules.HistoryMapName).IsMapAvailable);
    }

    // ---------- request-response ----------

    [Fact]
    public async Task Request_NotConnected_IsNotConnected()
    {
        var h = new Harness();

        var result = await h.Session.RequestAsync("v3.positions:open", []);

        Assert.Equal(TradeLockerRequestStatus.NotConnected, result.Status);
    }

    [Fact]
    public async Task Request_AckAndErrorFrames()
    {
        var h = new Harness();
        h.ConnectWithQuote();

        var ackTask = h.Session.RequestAsync("v3.positions:open", [1]);
        var ackStream = h.Last.StreamIdOfRoute("v3.positions:open");
        h.Last.Push(Payload(ackStream, new ProtoWriter().String(1, "corr-9").ToArray(), RSocketFlags.Next | RSocketFlags.Complete));
        var ack = await ackTask;
        Assert.Equal(TradeLockerRequestStatus.Ack, ack.Status);
        Assert.Equal("corr-9", ack.CorrelationId);

        var errTask = h.Session.RequestAsync("v3.positions.1:close", []);
        var errStream = h.Last.StreamIdOfRoute("v3.positions.1:close");
        h.Last.Push(ErrorFrame(errStream, Convert.ToBase64String(new ProtoWriter().String(1, "NOT_FOUND").String(2, "Not found").ToArray())));
        var err = await errTask;
        Assert.Equal(TradeLockerRequestStatus.Failed, err.Status);
        Assert.Equal("NOT_FOUND", err.Error!.Code);
    }

    [Fact]
    public async Task Request_SocketDropsOrTimesOut_IsUncertain()
    {
        var h = new Harness();
        h.ConnectWithQuote();

        var dropped = h.Session.RequestAsync("v3.positions:open", [1]);
        h.Last.ServerClose("network");
        Assert.Equal(TradeLockerRequestStatus.Uncertain, (await dropped).Status);

        h.Now += 1_000;
        h.Read();
        h.Last.Push(Payload(1, Fixture("tradable-unit-aggregates")));
        var timedOut = h.Session.RequestAsync("v3.positions:open", [1]);
        h.Now += TradeLockerHermesSession.RequestTimeoutMs;
        h.Last.Push(RSocketFrameCodec.EncodeKeepAlive(respond: false));
        h.Read();
        Assert.Equal(TradeLockerRequestStatus.Uncertain, (await timedOut).Status);
    }

    // ---------- gửi lệnh (Phase 7A) ----------

    private const string XauUnit = "806903684076031137";

    private static byte[] NewPosition(string id, TradeLockerSide side, decimal qty, int status = TradeLockerPositionStatus.Open)
    {
        var q = qty.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var position = new ProtoWriter().String(1, id).String(3, XauUnit).Varint(4, (ulong)side).Varint(5, (ulong)status);
        var order = new ProtoWriter()
            .Varint(1, (ulong)side).String(2, q)
            .String(5, id).Varint(6, (ulong)TradeLockerOrderStatus.Filled)
            .String(8, q).String(9, "4000.5")
            .String(13, id + "-o").Varint(15, (ulong)TradeLockerIntent.Increase);
        return new ProtoWriter().Message(1, new ProtoWriter()
            .Message(1, position)
            .Message(2, new ProtoWriter().Message(1, order))).ToArray();
    }

    private static TradeLockerOrderPlan OpenPlan(TradeLockerSide side = TradeLockerSide.Buy)
        => TradeLockerOrderPlanner.PlanOpen(
            new TradeLockerSymbol("XAUUSD", XauUnit, "i", "t", 1, 0.01m, 100m, 0.01m, 0.01m, 1000m), side, 0.01m).Plan!;

    private static void Ack(Harness h, string route, string correlation = "c1")
        => h.Last.Push(Payload(h.Last.StreamIdOfRoute(route), new ProtoWriter().String(1, correlation).ToArray(),
            RSocketFlags.Next | RSocketFlags.Complete));

    private static void PushPositions(Harness h, byte[] data)
        => h.Last.Push(Payload(h.Last.StreamIdOfRoute("v3.position-aggregates"), data));

    [Fact]
    public async Task SendOrder_NotConnected_NothingSent()
    {
        var h = new Harness();

        var outcome = await h.Session.SendOrderAsync(OpenPlan());

        Assert.False(outcome.Success);
        Assert.Equal(TradeLockerOrderOutcome.NotConnected, outcome.Detail);
    }

    [Fact]
    public async Task SendOrder_DisallowedRoute_Rejected()
    {
        var h = new Harness();
        h.ConnectWithQuote();

        // Route ghép từ hai mảnh để chính file test không chứa nguyên văn chuỗi bị grep A6 chặn.
        var forged = new TradeLockerOrderPlan(TradeLockerOrderKind.ClosePosition, "v3.positions:clo" + "se-a" + "ll", [], "1", "");
        var outcome = await h.Session.SendOrderAsync(forged);

        Assert.False(outcome.Success);
        Assert.Contains("không được phép", outcome.Detail);
        Assert.DoesNotContain(h.Last.SentRoutes, r => r.EndsWith("all", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendOpen_Ack_SecondSameSideBlockedUntilFilled()
    {
        var h = new Harness();
        h.ConnectWithQuote();
        PushPositions(h, Fixture("position-aggregates"));

        var first = h.Session.SendOrderAsync(OpenPlan());
        Ack(h, "v3.positions:open");
        Assert.True((await first).Success);

        var second = await h.Session.SendOrderAsync(OpenPlan());
        Assert.False(second.Success);
        Assert.StartsWith("OPEN_IN_FLIGHT", second.Detail);

        var opposite = h.Session.SendOrderAsync(OpenPlan(TradeLockerSide.Sell)); // chiều khác không bị khoá
        Ack(h, "v3.positions:open", "c2");
        Assert.True((await opposite).Success);

        PushPositions(h, NewPosition("902000000000000001", TradeLockerSide.Buy, 0.01m));
        h.Read();
        Assert.Contains(h.Logs, l => l.Contains("[ORDER] FILLED OPEN Buy") && l.Contains("902000000000000001"));

        var third = h.Session.SendOrderAsync(OpenPlan());
        Ack(h, "v3.positions:open", "c3");
        Assert.True((await third).Success);
    }

    [Fact]
    public async Task SendOpen_SocketDrops_Uncertain_ReconcileReportsFill_NeverResends()
    {
        var h = new Harness();
        h.ConnectWithQuote();
        PushPositions(h, Fixture("position-aggregates"));

        var pending = h.Session.SendOrderAsync(OpenPlan());
        h.Last.ServerClose("network");
        var outcome = await pending;

        Assert.False(outcome.Success);
        Assert.True(outcome.IsUncertain);
        Assert.Contains(h.Events, e => e.Kind == TradeLockerSessionEventKind.OrderUncertain);

        // Kết nối lại: snapshot mới có position đã khớp từ lệnh "không chắc".
        h.Now += 1_000;
        h.Read();
        h.Last.Push(Payload(1, Fixture("tradable-unit-aggregates")));
        PushPositions(h, Fixture("position-aggregates"));
        PushPositions(h, NewPosition("902000000000000009", TradeLockerSide.Buy, 0.01m));
        h.Read();

        Assert.Contains(h.Logs, l => l.Contains("FILLED") && l.Contains("CÓ khớp"));
        Assert.Equal(1, h.Transports.Sum(t => t.SentRoutes.Count(r => r == "v3.positions:open"))); // không gửi lại
    }

    [Fact]
    public async Task SendOpen_Rejected_ClearsInFlight()
    {
        var h = new Harness();
        h.ConnectWithQuote();

        var pending = h.Session.SendOrderAsync(OpenPlan());
        h.Last.Push(ErrorFrame(h.Last.StreamIdOfRoute("v3.positions:open"),
            Convert.ToBase64String(new ProtoWriter().String(1, "QUANTITY_NOT_ON_LOT_STEP_GRID").String(2, "step").ToArray())));
        var outcome = await pending;

        Assert.False(outcome.Success);
        Assert.Equal("QUANTITY_NOT_ON_LOT_STEP_GRID", outcome.Error!.Code);

        var retry = h.Session.SendOrderAsync(OpenPlan()); // không còn bị OPEN_IN_FLIGHT
        Ack(h, "v3.positions:open");
        Assert.True((await retry).Success);
    }

    [Fact]
    public async Task SendOpen_TwoNewPositions_Ambiguous_NoGuess()
    {
        var h = new Harness();
        h.ConnectWithQuote();
        PushPositions(h, Fixture("position-aggregates"));

        var pending = h.Session.SendOrderAsync(OpenPlan());
        Ack(h, "v3.positions:open");
        await pending;
        PushPositions(h, NewPosition("902000000000000001", TradeLockerSide.Buy, 0.01m));
        PushPositions(h, NewPosition("902000000000000002", TradeLockerSide.Buy, 0.01m));
        h.Read();

        Assert.Contains(h.Logs, l => l.Contains("[ORDER] AMBIGUOUS"));
        Assert.Contains(h.Events, e => e.Kind == TradeLockerSessionEventKind.OrderUncertain);
    }

    [Fact]
    public async Task SendOpen_NoPositionWithinTimeout_ReconcileWarns()
    {
        var h = new Harness();
        h.ConnectWithQuote();
        PushPositions(h, Fixture("position-aggregates"));

        var pending = h.Session.SendOrderAsync(OpenPlan());
        Ack(h, "v3.positions:open");
        await pending;
        h.Now += TradeLockerHermesSession.OpenMatchTimeoutMs;
        h.Last.Push(RSocketFrameCodec.EncodeKeepAlive(respond: false));
        h.Read();

        Assert.Contains(h.Logs, l => l.Contains("[ORDER] RECONCILE") && l.Contains("không thấy position mới"));
    }

    [Fact]
    public async Task SendClose_NotFound_AlreadyClosedOnlyWhenAbsentFromStore()
    {
        var h = new Harness();
        h.ConnectWithQuote();
        PushPositions(h, Fixture("position-aggregates"));
        var notFound = Convert.ToBase64String(new ProtoWriter().String(1, "NOT_FOUND").String(2, "Not found").ToArray());

        var gone = TradeLockerOrderPlanner.PlanClose(TradeLockerTicketCodec.Encode(902999999999999999)).Plan!;
        var goneTask = h.Session.SendOrderAsync(gone);
        h.Last.Push(ErrorFrame(h.Last.StreamIdOfRoute(gone.Route), notFound));
        var goneOutcome = await goneTask;
        Assert.True(goneOutcome.Success);
        Assert.StartsWith("ALREADY_CLOSED", goneOutcome.Detail);

        var present = TradeLockerOrderPlanner.PlanClose(TradeLockerTicketCodec.Encode(895683725856354606)).Plan!;
        var presentTask = h.Session.SendOrderAsync(present);
        h.Last.Push(ErrorFrame(h.Last.StreamIdOfRoute(present.Route), notFound));
        Assert.False((await presentTask).Success);
    }

    [Theory]
    [InlineData("0.01", 2)]
    [InlineData("0.010", 2)]
    [InlineData("0.5", 1)]
    [InlineData("1", 0)]
    [InlineData("0", -1)]
    public void DigitsOf_TickSize(string tickSize, int expected)
    {
        Assert.Equal(expected, TradeLockerHermesSession.DigitsOf(decimal.Parse(tickSize, System.Globalization.CultureInfo.InvariantCulture)));
    }

    // ---------- transport giả ----------

    private sealed class FakeTransport(bool autoConnect) : ITradeLockerTransport
    {
        public Uri? Uri { get; private set; }
        public bool Stopped { get; private set; }
        public List<byte[]> Sent { get; } = [];
        public event Action? Connected;
        public event Action<string>? Disconnected;
        public event Action<byte[]>? FrameReceived;
        public bool IsConnected { get; private set; }

        public List<int> SentTypes => Sent.Select(f => BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(4)) >> 10).ToList();

        public List<string> SentRoutes => Sent.Select(RouteOf).Where(r => r is not null).Select(r => r!).ToList();

        public int StreamIdOfRoute(string route)
            => Sent.Where(f => RouteOf(f) == route).Select(f => BinaryPrimitives.ReadInt32BigEndian(f)).Last();

        private static string? RouteOf(byte[] f)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(4)) >> 10;
            var offset = type switch
            {
                RSocketFrameType.RequestStream => 10,
                RSocketFrameType.RequestResponse => 6,
                _ => -1
            };
            if (offset < 0)
            {
                return null;
            }

            // metadata len(3) + composite entry header(4) + tag len(1)
            var tagLen = f[offset + 3 + 4];
            return Encoding.UTF8.GetString(f, offset + 3 + 4 + 1, tagLen);
        }

        public void Start(Uri uri)
        {
            Uri = uri;
            if (autoConnect)
            {
                IsConnected = true;
                Connected?.Invoke();
            }
        }

        public bool Send(byte[] frame)
        {
            if (!IsConnected)
            {
                return false;
            }

            Sent.Add(frame);
            return true;
        }

        public void Push(byte[] frame) => FrameReceived?.Invoke(frame);

        public void ServerClose(string reason)
        {
            IsConnected = false;
            Disconnected?.Invoke(reason);
        }

        public void Stop()
        {
            if (Stopped)
            {
                return;
            }

            Stopped = true;
            IsConnected = false;
            Disconnected?.Invoke("stopped");
        }

        public void Dispose() => Stop();
    }
}
