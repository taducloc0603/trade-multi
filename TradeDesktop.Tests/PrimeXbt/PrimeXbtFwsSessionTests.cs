using System.Text.Json;
using System.Text.Json.Nodes;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.PrimeXbt;
using TradeDesktop.Infrastructure.PrimeXbt;

namespace TradeDesktop.Tests.PrimeXbt;

internal sealed class FakePrimeXbtTransport : IPrimeXbtTransport
{
    public List<string> Calls { get; } = new();
    public List<string> Sent { get; } = new();
    public Uri? StartUri { get; private set; }
    public IReadOnlyDictionary<string, string>? Cookies { get; private set; }
    public bool Disposed { get; private set; }
    public bool IsConnected { get; private set; }

    public event Action? Connected;
    public event Action<string>? Disconnected;
    public event Action<string>? MessageReceived;

    public void Start(Uri uri, IReadOnlyDictionary<string, string> cookies)
    {
        Calls.Add("start");
        StartUri = uri;
        Cookies = cookies;
    }

    public bool Send(string text)
    {
        if (!IsConnected)
        {
            return false;
        }

        Sent.Add(text);
        return true;
    }

    public void Stop()
    {
        Calls.Add("stop");
        if (IsConnected)
        {
            Drop("stopped");
        }
    }

    public void Dispose()
    {
        Calls.Add("dispose");
        Disposed = true;
    }

    // --- test drivers (đồng bộ trên luồng test) ---
    public void Connect()
    {
        IsConnected = true;
        Connected?.Invoke();
    }

    public void Drop(string reason)
    {
        IsConnected = false;
        Disconnected?.Invoke(reason);
    }

    public void Receive(string json) => MessageReceived?.Invoke(json);

    public JsonObject? LastSent(string action) => Sent
        .Select(s => JsonNode.Parse(s)!.AsObject())
        .LastOrDefault(o => o["action"]!.GetValue<string>() == action);
}

internal sealed class InMemoryTokenStore : IPrimeXbtTokenStore
{
    public PrimeXbtSession? Current { get; set; }
    public int Saves { get; private set; }

    public PrimeXbtSession? Load() => Current;

    public void Save(PrimeXbtSession session)
    {
        Saves++;
        Current = session;
    }

    public void Clear() => Current = null;
}

public sealed class PrimeXbtFwsSessionTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 7, 0, 0, DateTimeKind.Utc);
    private static readonly ExchangeMetrics ExchangeA = new("XAUUSD.s", 4177.00m, 4177.34m, 0.34m, 1, 5, "t", 1, 1, true, null);
    private static readonly PrimeXbtConfig Config = PrimeXbtConfigTests.Demo();

    private sealed class Harness
    {
        public long Clock = 10_000;
        public readonly List<FakePrimeXbtTransport> Transports = new();
        public readonly List<string> Logs = new();
        public readonly List<PrimeXbtSessionEvent> Events = new();
        public readonly InMemoryTokenStore Store = new() { Current = PrimeXbtTokenStoreTests.Session(Now.AddDays(7)) };
        public Func<PrimeXbtSession, PrimeXbtSession?> Refresh = _ => null;
        public int RefreshCalls;
        public readonly PrimeXbtFwsSession Session;

        public Harness()
        {
            Session = new PrimeXbtFwsSession(
                () => { var t = new FakePrimeXbtTransport(); Transports.Add(t); return t; },
                Store,
                (s, _) => { RefreshCalls++; return Task.FromResult(Refresh(s)); },
                () => Clock,
                () => Now);
            Session.LogLine += l => { lock (Logs) Logs.Add(l); };
            Session.EventRaised += e => { lock (Events) Events.Add(e); };
        }

        public FakePrimeXbtTransport Current => Transports[^1];

        public async Task EnsureAsync(string platformB = "primexbt", PrimeXbtConfig? config = null)
        {
            Session.EnsureState(platformB, config ?? Config);
            await Session.WaitForLifecycleAsync();
        }

        public async Task<ExchangeMetrics> ReadAsync(int point = 100, int confirmLatencyMs = 0)
        {
            var m = Session.Read(ExchangeA, point, confirmLatencyMs);
            await Session.WaitForLifecycleAsync();
            return m;
        }

        public void ReplyMarkets2()
        {
            var rid = Current.LastSent("markets2")!["rid"]!.GetValue<int>();
            var frame = JsonNode.Parse(PrimeXbtFixtures.Raw("quotes.json", "markets2_response_xau_only"))!.AsObject();
            frame["rid"] = rid;
            Current.Receive(frame.ToJsonString());
        }

        public void Quote(decimal bid, decimal ask) =>
            Current.Receive($$"""{"type":"EVENT","action":"fx/market","body":{"symId":1019,"a":{{ask}},"b":{{bid}},"lp":{{bid}}},"sid":6,"aid":1}""");

        public async Task StreamingAsync()
        {
            await EnsureAsync();
            Current.Connect();
            ReplyMarkets2();
            Quote(4177.04m, 4177.23m);
        }
    }

    [Theory]
    [InlineData("mt5")]
    [InlineData("ctrader")]
    [InlineData("")]
    public async Task NotPrimeXbt_NeverOpensTransport(string platformB)
    {
        var h = new Harness();

        await h.EnsureAsync(platformB);
        var m = await h.ReadAsync();

        Assert.Empty(h.Transports);
        Assert.False(m.IsConnected);
    }

    [Fact]
    public async Task MissingConfig_DoesNotConnect_LogsOnce()
    {
        var h = new Harness();
        var incomplete = Config with { AccountId = "" };

        await h.EnsureAsync(config: incomplete);
        await h.EnsureAsync(config: incomplete);
        var m = await h.ReadAsync();

        Assert.Empty(h.Transports);
        Assert.Contains("Account ID", m.Error);
        Assert.Single(h.Logs, l => l.Contains("thiếu", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConnectFlow_UrlCookieAndSubscriptions_ThenStreaming()
    {
        var h = new Harness();

        await h.StreamingAsync();
        var m = await h.ReadAsync();

        var t = h.Current;
        Assert.StartsWith("wss://api.primexbt.com/v2/fws/?accountId=d1282507&jwt=", t.StartUri!.ToString());
        Assert.Equal(new[] { "fws_token" }, t.Cookies!.Keys.ToArray());
        Assert.NotNull(t.LastSent("metrics"));
        Assert.Equal("XAU/USD", t.LastSent("trade-settings")!["body"]!["symbol"]!.GetValue<string>());
        Assert.Equal(1019, t.LastSent("fx/market")!["body"]!["symbolId"]!.GetValue<int>());
        Assert.True(m.IsConnected, m.Error);
        Assert.Equal("XAU/USD", m.Symbol);
        Assert.Equal(4177.04m, m.Bid);
        Assert.Equal(4177.23m, m.Ask);
        Assert.Equal(0.19m, m.Spread);
        Assert.True(h.Session.IsLoggedOn);
    }

    [Fact]
    public async Task FailClosed_BeforeConnect_BeforeSymbol_BeforeQuote()
    {
        var h = new Harness();
        await h.EnsureAsync();

        Assert.Equal("PrimeXBT chưa kết nối", (await h.ReadAsync()).Error);
        h.Current.Connect();
        Assert.Equal("Chưa resolve được symbol PrimeXBT", (await h.ReadAsync()).Error);
        h.ReplyMarkets2();
        var noQuote = await h.ReadAsync();
        Assert.False(noQuote.IsConnected);
        Assert.Equal("XAU/USD", noQuote.Symbol);
        Assert.Equal("Chưa có giá PrimeXBT", noQuote.Error);
    }

    [Fact]
    public async Task QuoteForAnotherSymbolOrBadTick_IsIgnored()
    {
        var h = new Harness();
        await h.EnsureAsync();
        h.Current.Connect();
        h.ReplyMarkets2();

        h.Current.Receive("""{"type":"EVENT","action":"fx/market","body":{"symId":2000,"a":5,"b":4},"sid":6,"aid":1}""");
        h.Quote(4177.30m, 4177.20m); // ask < bid

        Assert.False((await h.ReadAsync()).IsConnected);
    }

    [Fact]
    public async Task LatencyMs_IsTickAge_IntervalAndAvgMaxSampledPerTick()
    {
        var h = new Harness();
        await h.StreamingAsync();

        h.Clock += 100;
        Assert.Equal(100m, (await h.ReadAsync()).LatencyMs);
        h.Clock += 300;
        var older = await h.ReadAsync();
        Assert.Equal(400m, older.LatencyMs);
        Assert.Null(older.TickIntervalMs);

        h.Quote(4177.05m, 4177.24m);
        var fresh = await h.ReadAsync();
        Assert.Equal(0m, fresh.LatencyMs);
        Assert.Equal(400m, fresh.TickIntervalMs);
        Assert.Equal(400m, fresh.MaxLatMs);
        Assert.Equal(400m, fresh.AvgLatMs);
    }

    [Fact]
    public async Task DigitsMismatch_FailsClosed_RaisesOnce_ThenRecovers()
    {
        var h = new Harness();
        await h.StreamingAsync();

        var bad = await h.ReadAsync(point: 1000);
        await h.ReadAsync(point: 1000);

        Assert.False(bad.IsConnected);
        Assert.Contains("Lệch digits", bad.Error);
        Assert.Single(h.Events, e => e.Kind == PrimeXbtSessionEventKind.DigitsMismatch);
        Assert.True((await h.ReadAsync(point: 100)).IsConnected);
    }

    [Fact]
    public async Task Heartbeat_SentEvery20s_AnsweredKeepsConnection()
    {
        var h = new Harness();
        await h.StreamingAsync();

        h.Clock += PrimeXbtFwsSession.HeartbeatIntervalMs;
        await h.ReadAsync();
        var time = h.Current.LastSent("time");
        Assert.NotNull(time);
        h.Current.Receive($$"""{"type":"RESPONSE","action":"time","body":{"time":1791514460424},"rid":{{time!["rid"]!.GetValue<int>()}}}""");
        h.Clock += PrimeXbtFwsSession.HeartbeatTimeoutMs + 1;

        Assert.True((await h.ReadAsync()).IsConnected);
        Assert.DoesNotContain("stop", h.Current.Calls);
    }

    [Fact]
    public async Task Heartbeat_Unanswered_FailsClosed_DropsSocket_ThenReconnectsAfterBackoff()
    {
        var h = new Harness();
        await h.StreamingAsync();
        var first = h.Current;

        h.Clock += PrimeXbtFwsSession.HeartbeatIntervalMs;
        await h.ReadAsync();
        h.Clock += PrimeXbtFwsSession.HeartbeatTimeoutMs;
        var stale = await h.ReadAsync();

        Assert.False(stale.IsConnected);
        Assert.Null(stale.Bid);
        Assert.Contains("stop", first.Calls);
        Assert.Single(h.Logs, l => l.Contains("heartbeat quá", StringComparison.Ordinal));

        h.Clock += PrimeXbtFwsSession.ReconnectBackoffMs[0];
        await h.ReadAsync();
        Assert.Equal(2, h.Transports.Count);
        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task Disconnect_ClearsPriceImmediately_OldTransportEventsIgnored()
    {
        var h = new Harness();
        await h.StreamingAsync();
        var first = h.Current;

        first.Drop("network");
        var m = await h.ReadAsync();
        Assert.False(m.IsConnected);
        Assert.Null(m.Bid);
        Assert.Single(h.Events, e => e.Kind == PrimeXbtSessionEventKind.Disconnected);

        h.Clock += PrimeXbtFwsSession.ReconnectBackoffMs[0];
        await h.ReadAsync();
        var second = h.Current;
        Assert.NotSame(first, second);

        first.Receive("""{"type":"EVENT","action":"fx/market","body":{"symId":1019,"a":9999,"b":9998},"sid":6,"aid":9}""");
        Assert.False((await h.ReadAsync()).IsConnected);

        second.Connect();
        h.ReplyMarkets2();
        h.Quote(4177.10m, 4177.29m);
        Assert.Equal(4177.10m, (await h.ReadAsync()).Bid);
    }

    [Fact]
    public async Task FailedConnectAttempts_BackOffExponentially_AndDoNotCountAsStorm()
    {
        var h = new Harness();
        await h.EnsureAsync();

        for (var i = 0; i < 4; i++)
        {
            h.Current.Drop("connect failed");
            await h.ReadAsync();
            h.Clock += PrimeXbtFwsSession.ReconnectBackoffMs[i] - 1;
            await h.ReadAsync();
            Assert.Equal(i + 1, h.Transports.Count); // chưa tới hạn
            h.Clock += 1;
            await h.ReadAsync();
            Assert.Equal(i + 2, h.Transports.Count);
        }

        Assert.DoesNotContain(h.Events, e => e.Kind == PrimeXbtSessionEventKind.ReconnectStorm);
    }

    [Fact]
    public async Task Storm_SixRealDisconnectsInAMinute_TripsAndStops()
    {
        var h = new Harness();
        await h.EnsureAsync();

        for (var i = 0; i <= PrimeXbtFwsSession.StormThreshold; i++)
        {
            h.Current.Connect();
            h.ReplyMarkets2(); // phiên thật (resolve symbol) ⇒ backoff về 500 ms
            h.Current.Drop("flap");
            await h.ReadAsync();
            h.Clock += 2_000;
            await h.ReadAsync();
        }

        var count = h.Transports.Count;
        Assert.Single(h.Events, e => e.Kind == PrimeXbtSessionEventKind.ReconnectStorm);
        Assert.Contains("NGẮT MẠCH", h.Session.StatusText);
        h.Clock += 60_000;
        await h.ReadAsync();
        Assert.Equal(count, h.Transports.Count); // không tự mở lại trước cooldown

        await h.EnsureAsync("mt5");
        await h.EnsureAsync("primexbt");
        Assert.Equal(count + 1, h.Transports.Count); // can thiệp tay (đổi cấu hình) reset ngắt mạch
    }

    [Fact]
    public async Task NoToken_FailsClosedWithAuthRequired_ThenPicksUpNewLogin()
    {
        var h = new Harness();
        h.Store.Current = null;

        await h.EnsureAsync();
        var m = await h.ReadAsync();

        Assert.Empty(h.Transports);
        Assert.Contains("Chưa đăng nhập", m.Error);
        Assert.Single(h.Events, e => e.Kind == PrimeXbtSessionEventKind.AuthRequired);

        h.Store.Current = PrimeXbtTokenStoreTests.Session(Now.AddDays(7)); // user đăng nhập trong Config
        h.Clock += PrimeXbtFwsSession.AuthRetryMs;
        await h.ReadAsync();
        Assert.Single(h.Transports);
        h.Current.Connect();
        Assert.Equal("Chưa resolve được symbol PrimeXBT", (await h.ReadAsync()).Error);
    }

    [Fact]
    public async Task TokenNearExpiry_IsRefreshedBeforeConnect_AndSaved()
    {
        var h = new Harness();
        h.Store.Current = PrimeXbtTokenStoreTests.Session(Now.AddHours(2));
        var fresh = PrimeXbtTokenStoreTests.Session(Now.AddDays(7));
        h.Refresh = _ => fresh;

        await h.EnsureAsync();

        Assert.Equal(1, h.RefreshCalls);
        Assert.Equal(1, h.Store.Saves);
        Assert.Contains(Uri.EscapeDataString(fresh.Jwt), h.Current.StartUri!.ToString());
    }

    [Fact]
    public async Task ExpiredToken_RefreshFails_AuthRequired()
    {
        var h = new Harness();
        h.Store.Current = PrimeXbtTokenStoreTests.Session(Now.AddMinutes(-5));
        h.Refresh = _ => null;

        await h.EnsureAsync();

        Assert.Empty(h.Transports);
        Assert.Contains("hết hạn", (await h.ReadAsync()).Error);
    }

    [Fact]
    public async Task ConfigChange_RestartsSession_SwitchAway_StopsAndDisposes()
    {
        var h = new Harness();
        await h.StreamingAsync();
        var first = h.Current;

        await h.EnsureAsync(config: Config with { VolumeBOz = 2m });
        Assert.Equal(2, h.Transports.Count);
        Assert.True(first.Disposed);

        h.Current.Connect();
        await h.EnsureAsync("mt5");
        Assert.True(h.Current.Disposed);
        Assert.Contains(h.Events, e => e.Kind == PrimeXbtSessionEventKind.Stopped);
        Assert.DoesNotContain(h.Events, e => e.Kind == PrimeXbtSessionEventKind.Disconnected); // tự dừng không phải mất phiên
    }

    [Fact]
    public async Task RepeatedEnsure_IsIdempotent()
    {
        var h = new Harness();

        for (var i = 0; i < 5; i++)
        {
            await h.EnsureAsync();
        }

        Assert.Single(h.Transports);
    }

    [Fact]
    public async Task BlockedAccount_FailsClosed()
    {
        var h = new Harness();
        await h.StreamingAsync();

        h.Current.Receive("""{"type":"EVENT","action":"metrics","body":{"demo":true,"positionMode":"HEDGE","blocked":true,"closed":false},"sid":2,"aid":1}""");

        Assert.Contains("khoá", (await h.ReadAsync()).Error);
    }

    [Fact]
    public async Task Metrics_DemoMismatchAndNettingMode_AreWarned()
    {
        var h = new Harness();
        await h.StreamingAsync();

        h.Current.Receive("""{"type":"RESPONSE","action":"metrics","body":{"demo":false,"positionMode":"NETTING","blocked":false,"closed":false},"rid":2,"sid":2}""");

        Assert.Contains(h.Logs, l => l.Contains("[WARN]") && l.Contains("demo=false"));
        Assert.Contains(h.Logs, l => l.Contains("[WARN]") && l.Contains("NETTING"));
    }

    [Fact]
    public async Task StatsLine_EveryMinute_WithTickAgeAndWouldSkip()
    {
        var h = new Harness();
        await h.StreamingAsync();

        await h.ReadAsync(confirmLatencyMs: 2000);
        h.Clock += 2_500;
        await h.ReadAsync(confirmLatencyMs: 2000);
        h.Clock += PrimeXbtFwsSession.StatsWindowMs;
        h.Quote(4177.05m, 4177.24m);
        await h.ReadAsync(confirmLatencyMs: 2000);

        var stats = Assert.Single(h.Logs, l => l.Contains("[STATS]", StringComparison.Ordinal));
        Assert.Contains("confirm_latency_ms=2000", stats);
        Assert.Contains("would_skip_latency_b=1", stats);
        Assert.Contains("gap_buy_pts=", stats);
    }

    [Fact]
    public async Task StatsTicks_AfterSessionRestart_DoNotIncludePreviousSessionTicks()
    {
        var h = new Harness();
        await h.StreamingAsync();
        for (var i = 0; i < 9; i++)
        {
            h.Quote(4177.04m + i * 0.01m, 4177.23m + i * 0.01m); // 10 tick ở phiên cũ, chưa Read lần nào
        }

        await h.EnsureAsync(config: Config with { VolumeBOz = 2m });
        h.Current.Connect();
        h.ReplyMarkets2();
        h.Quote(4177.50m, 4177.69m);
        await h.ReadAsync();
        h.Clock += PrimeXbtFwsSession.StatsWindowMs;
        h.Quote(4177.51m, 4177.70m);
        await h.ReadAsync();

        var stats = Assert.Single(h.Logs, l => l.Contains("[STATS]", StringComparison.Ordinal));
        Assert.Contains(" ticks=2 ", stats);
    }

    // ---------------- Phase 5: Trades map B ----------------

    private const string TradesMap = "PRIMEXBT_B_Trades";

    private static string PositionsFrame(string key) => PrimeXbtFixtures.Raw("account-positions.json", key);

    [Fact]
    public async Task ReadTrades_MapNotFound_UntilPositionsSnapshot_ThenEmptyMap()
    {
        var h = new Harness();
        Assert.False(h.Session.ReadTrades(TradesMap).IsMapAvailable); // chưa bật

        await h.EnsureAsync();
        Assert.False(h.Session.ReadTrades(TradesMap).IsMapAvailable); // chưa kết nối
        h.Current.Connect();
        Assert.NotNull(h.Current.LastSent("positions"));
        h.Current.Receive(PositionsFrame("positions_response_empty"));
        Assert.False(h.Session.ReadTrades(TradesMap).IsMapAvailable); // chưa resolve symbol
        h.ReplyMarkets2();

        var result = h.Session.ReadTrades(TradesMap);
        Assert.True(result.IsMapAvailable);
        Assert.True(result.IsParseSuccess);
        Assert.Equal(0, result.Count);
        Assert.Equal(1, result.Connected);
        Assert.Contains(h.Logs, l => l.Contains("positions synced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadTrades_HedgeSnapshot_GivesSubPositions_AndOpenPositionLookup()
    {
        var h = new Harness();
        await h.StreamingAsync();

        h.Current.Receive(PositionsFrame("positions_event_hedge_two_subs"));
        var result = h.Session.ReadTrades(TradesMap);

        Assert.True(result.IsMapAvailable);
        Assert.Equal(2, result.Count);
        Assert.True(result.Timestamp > 0);
        Assert.Equal((PrimeXbtSide.Buy, 0.01m), h.Session.TryGetOpenPosition(10680072));
        Assert.Null(h.Session.TryGetOpenPosition(42));

        h.Current.Receive(PositionsFrame("positions_event_hedge_two_subs"));
        Assert.Equal(result.Timestamp, h.Session.ReadTrades(TradesMap).Timestamp); // R3: snapshot y hệt không đổi version
        Assert.Single(h.Logs, l => l.Contains("count=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadTrades_Disconnect_MapNotFoundImmediately_ReconnectNeedsNewSnapshot()
    {
        var h = new Harness();
        await h.StreamingAsync();
        h.Current.Receive(PositionsFrame("positions_event_one_buy"));
        Assert.True(h.Session.ReadTrades(TradesMap).IsMapAvailable);

        h.Current.Drop("network");
        Assert.False(h.Session.ReadTrades(TradesMap).IsMapAvailable);
        Assert.Null(h.Session.TryGetOpenPosition(10680072));
        await h.ReadAsync(); // chờ lifecycle dọn transport chết trước khi tới hạn backoff (tránh race trong test)

        h.Clock += PrimeXbtFwsSession.ReconnectBackoffMs[0];
        await h.ReadAsync();
        h.Current.Connect();
        h.ReplyMarkets2();
        Assert.False(h.Session.ReadTrades(TradesMap).IsMapAvailable); // chưa có snapshot của kết nối mới

        h.Current.Receive(PositionsFrame("positions_event_one_buy"));
        Assert.Equal(1, h.Session.ReadTrades(TradesMap).Count);
    }

    [Fact]
    public async Task ReadTrades_HeartbeatStale_FailsClosed()
    {
        var h = new Harness();
        await h.StreamingAsync();
        h.Current.Receive(PositionsFrame("positions_response_empty"));

        h.Clock += PrimeXbtFwsSession.HeartbeatIntervalMs;
        await h.ReadAsync();
        h.Clock += PrimeXbtFwsSession.HeartbeatTimeoutMs;
        await h.ReadAsync();

        Assert.False(h.Session.ReadTrades(TradesMap).IsMapAvailable);
    }

    [Fact]
    public async Task ReadTrades_NettingMode_FailsClosed_RaisesOnce()
    {
        var h = new Harness();
        await h.StreamingAsync();
        var netting = PositionsFrame("positions_response_empty").Replace("\"HEDGE\"", "\"NETTING\"");

        h.Current.Receive(netting);
        h.Current.Receive(netting);

        Assert.False(h.Session.ReadTrades(TradesMap).IsMapAvailable);
        Assert.Single(h.Events, e => e.Kind == PrimeXbtSessionEventKind.PositionModeInvalid);
        Assert.Single(h.Logs, l => l.Contains("[ERROR]") && l.Contains("NETTING"));

        h.Current.Receive(PositionsFrame("positions_response_empty"));
        Assert.True(h.Session.ReadTrades(TradesMap).IsMapAvailable);
    }

    [Fact]
    public async Task ReadTrades_InvalidSnapshot_FailsClosed_UntilNextValid()
    {
        var h = new Harness();
        await h.StreamingAsync();
        h.Current.Receive(PositionsFrame("positions_event_one_buy"));

        h.Current.Receive("""{"type":"EVENT","action":"positions","body":{"positionMode":"HEDGE","data":[{"id":0,"symbol":"XAU/USD","qty":0.01}]},"sid":3,"aid":7}""");
        Assert.False(h.Session.ReadTrades(TradesMap).IsMapAvailable);
        Assert.Single(h.Logs, l => l.Contains("không hợp lệ", StringComparison.Ordinal));

        h.Current.Receive(PositionsFrame("positions_event_flat"));
        var flat = h.Session.ReadTrades(TradesMap);
        Assert.True(flat.IsMapAvailable);
        Assert.Equal(0, flat.Count);
    }

    [Fact]
    public async Task ReadTrades_NotPrimeXbt_MapNotFound()
    {
        var h = new Harness();
        await h.EnsureAsync("mt5");

        Assert.False(h.Session.ReadTrades(TradesMap).IsMapAvailable);
    }

    [Fact]
    public async Task Logs_NeverContainJwtOrCookie()
    {
        var h = new Harness();
        await h.StreamingAsync();
        h.Current.Drop("server close 1006 jwt=" + h.Store.Current!.Jwt);
        await h.ReadAsync();

        var all = string.Join("\n", h.Logs) + string.Join("\n", h.Events.Select(e => e.Message));
        Assert.DoesNotContain("eyJ", all);
        Assert.DoesNotContain("fws-secret-value", all);
    }

    [Fact]
    public async Task Dispose_StopsActiveSession()
    {
        var h = new Harness();
        await h.StreamingAsync();

        h.Session.Dispose();
        h.Session.EnsureState("primexbt", Config);
        await h.Session.WaitForLifecycleAsync();

        Assert.True(h.Current.Disposed);
        Assert.Single(h.Transports);
    }
}

public sealed class PrimeXbtLogMaskerTests
{
    [Theory]
    [InlineData("jwt=eyJhbGciOi.eyJzdWIi.sig&x=1", "jwt=<JWT>&x=1")]
    [InlineData("token eyJabc.def.ghi end", "token <JWT> end")]
    [InlineData("Cookie: fws_token=abc123; refresh_token=zzz", "Cookie: fws_token=<COOKIE>; refresh_token=<COOKIE>")]
    [InlineData("auth-guard=S3U8Yk85&a=b", "auth-guard=<AG>&a=b")]
    [InlineData("plain text", "plain text")]
    [InlineData(null, "")]
    public void Apply_MasksSecrets(string? input, string expected)
    {
        Assert.Equal(expected, PrimeXbtLogMasker.Apply(input));
    }
}
