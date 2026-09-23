using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.CTrader;
using TradeDesktop.Infrastructure.CTrader;

namespace TradeDesktop.Tests.CTrader;

// Ngắt mạch chống bão reconnect. Sự cố live 2026-09-21 19:23: QUOTE bị logout ~29 lần/phút suốt 18 phút
// (~500 lần logon lên broker) vì client tự gửi Logout `58=Incorrect BeginString`. Vượt ngưỡng thì phải DỪNG HẲN,
// không tự nối lại; sàn B fail-closed cho tới khi có can thiệp (đổi platform_b / sửa config / mở lại app).
public sealed class CTraderReconnectStormTests
{
    private const string TradeMap = CTraderRoutingRules.TradeMapName;
    private const string Header = "34=5|49=cServer|50=TRADE|52=20260921-12:00:00.000|56=live.fxpro.8220816|57=TRADE";

    private sealed class QuoteHarness
    {
        public long Clock = 500_000;
        public List<FakeCTraderTransport> Transports { get; } = [];
        public List<string> Logs { get; } = [];
        public List<CTraderQuoteSessionEvent> Events { get; } = [];
        public CTraderQuoteSession Session { get; }

        public QuoteHarness()
        {
            Session = new CTraderQuoteSession(_ =>
            {
                var t = new FakeCTraderTransport();
                Transports.Add(t);
                return t;
            }, () => Clock);
            Session.LogLine += l => { lock (Logs) Logs.Add(l); };
            Session.EventRaised += e => { lock (Events) Events.Add(e); };
        }

        public FakeCTraderTransport Current => Transports[^1];

        public async Task EnsureAsync(string platformB)
        {
            Session.EnsureState(platformB, CTraderQuoteSessionTests.ValidConfig());
            await Session.WaitForLifecycleAsync();
        }

        // Read() là nơi vòng poll 50 ms gọi tới — cũng là chỗ kiểm tra hết cooldown ngắt mạch.
        public async Task ReadAsync()
        {
            Session.Read(new ExchangeMetrics("A", 1m, 1m, 0m, 0, 1, "t", 0, 0, true, null), 100, 0);
            await Session.WaitForLifecycleAsync();
        }

        public async Task FlapAsync(int times, long stepMs = 2_000)
        {
            for (var i = 0; i < times; i++)
            {
                Current.Logon(CTraderSessionRole.Quote);
                Clock += stepMs;
                Current.Logout(CTraderSessionRole.Quote);
            }

            await Session.WaitForLifecycleAsync();
        }
    }

    [Fact]
    public async Task Quote_StormTripsAndStopsSession_NoAutoRestart()
    {
        var h = new QuoteHarness();
        await h.EnsureAsync("ctrader");
        var transport = h.Current;

        await h.FlapAsync(CTraderQuoteSession.StormThreshold + 1);

        var storm = Assert.Single(h.Events, e => e.Kind == CTraderQuoteEventKind.ReconnectStorm);
        Assert.Contains("NGẮT MẠCH", storm.Message);
        Assert.True(transport.Disposed);
        Assert.Contains("stop:Quote", transport.Calls);
        Assert.Contains(h.Logs, l => l.StartsWith("[CTRADER][ERROR]", StringComparison.Ordinal) && l.Contains("NGẮT MẠCH", StringComparison.Ordinal));
        // Sau khi dừng, StatusText là "Đã dừng: ngắt mạch: …" (StopTransport ghi đè trạng thái cảnh báo).
        Assert.Contains("ngắt mạch", h.Session.StatusText, StringComparison.OrdinalIgnoreCase);

        // EnsureState lặp lại với đúng trạng thái cũ (vòng poll 50 ms gọi liên tục) KHÔNG được mở lại.
        for (var i = 0; i < 5; i++)
        {
            await h.EnsureAsync("ctrader");
        }

        Assert.Single(h.Transports);
        Assert.False(h.Session.Read(new ExchangeMetrics("A", 1m, 1m, 0m, 0, 1, "t", 0, 0, true, null), 100, 0).IsConnected);
    }

    [Fact]
    public async Task Quote_StormResetsOnlyAfterUserIntervention()
    {
        var h = new QuoteHarness();
        await h.EnsureAsync("ctrader");
        await h.FlapAsync(CTraderQuoteSession.StormThreshold + 1);
        Assert.Single(h.Transports);

        // Đổi khỏi cTrader rồi quay lại = can thiệp của người dùng → được mở lại.
        await h.EnsureAsync("mt5");
        await h.EnsureAsync("ctrader");

        Assert.Equal(2, h.Transports.Count);
        Assert.Contains("start:Quote", h.Current.Calls);
    }

    [Fact]
    public async Task Quote_FlapsSpreadOverTime_DoNotTrip()
    {
        var h = new QuoteHarness();
        await h.EnsureAsync("ctrader");

        // Mỗi lần mất phiên cách nhau hơn cửa sổ trượt → cửa sổ luôn chỉ có 1 lần, không bao giờ vượt ngưỡng.
        await h.FlapAsync(CTraderQuoteSession.StormThreshold + 3, stepMs: CTraderQuoteSession.StormWindowMs + 1_000);

        Assert.DoesNotContain(h.Events, e => e.Kind == CTraderQuoteEventKind.ReconnectStorm);
        Assert.Single(h.Transports);
        Assert.False(h.Transports[0].Disposed);
    }

    // Sự cố live 2026-09-23 05:36: sàn reset phiên hằng ngày, TRADE gửi Logon 5 lần không ai trả lời.
    // QuickFIX/n phát OnLogout cho MỖI lần thử hỏng → bị tính thành bão → TRADE dừng hẳn, nằm chết 3 h 17 m.
    // Lần thử logon hỏng KHÔNG được tính là mất phiên.
    [Fact]
    public async Task FailedLogonAttempts_DoNotTripBreaker()
    {
        var h = new QuoteHarness();
        await h.EnsureAsync("ctrader");

        for (var i = 0; i < CTraderQuoteSession.StormThreshold + 5; i++)
        {
            h.Current.Logout(CTraderSessionRole.Quote);
            h.Clock += 2_000;
        }

        await h.Session.WaitForLifecycleAsync();

        Assert.DoesNotContain(h.Events, e => e.Kind == CTraderQuoteEventKind.ReconnectStorm);
        Assert.Single(h.Transports);
        Assert.False(h.Transports[0].Disposed);
    }

    // Sau ngắt mạch: tự thử lại tối đa StormMaxRetries lần, mỗi lần cách StormRetryCooldownMs; hết lượt thì
    // dừng hẳn nhưng vẫn nhắc định kỳ (chết thầm là chế độ hỏng nguy hiểm nhất).
    [Fact]
    public async Task Quote_BreakerAutoRetriesAfterCooldown_ThenStopsForGood()
    {
        var h = new QuoteHarness();
        await h.EnsureAsync("ctrader");
        await h.FlapAsync(CTraderQuoteSession.StormThreshold + 1);
        Assert.Single(h.Transports);

        for (var attempt = 1; attempt <= CTraderQuoteSession.StormMaxRetries; attempt++)
        {
            h.Clock += CTraderQuoteSession.StormRetryCooldownMs;
            await h.ReadAsync();

            Assert.Equal(attempt + 1, h.Transports.Count);
            Assert.Contains("start:Quote", h.Current.Calls);

            // Lần thử nào cũng lại dính bão ngay (kịch bản xấu nhất).
            await h.FlapAsync(CTraderQuoteSession.StormThreshold + 1);
        }

        // Hết lượt: không mở transport mới nữa, chỉ nhắc lại mỗi DeadAlertIntervalMs.
        var transportsAfter = h.Transports.Count;
        h.Clock += CTraderQuoteSession.StormRetryCooldownMs;
        await h.ReadAsync();
        Assert.Equal(transportsAfter, h.Transports.Count);

        var remindersBefore = h.Events.Count(e => e.Kind == CTraderQuoteEventKind.ReconnectStorm);
        h.Clock += CTraderQuoteSession.DeadAlertIntervalMs;
        await h.ReadAsync();

        var reminders = h.Events.Where(e => e.Kind == CTraderQuoteEventKind.ReconnectStorm).ToList();
        Assert.Equal(remindersBefore + 1, reminders.Count);
        Assert.Contains("cần can thiệp tay", reminders[^1].Message);
    }

    [Fact]
    public async Task Trade_BreakerAutoRetriesAfterCooldown()
    {
        var transports = new List<FakeCTraderTransport>();
        long clock = 500_000;
        var session = new CTraderTradeSession(_ =>
        {
            var t = new FakeCTraderTransport();
            transports.Add(t);
            return t;
        }, () => clock, () => 1_789_000_000_000, startTimer: false);

        session.EnsureState("ctrader", CTraderQuoteSessionTests.ValidConfig());
        await session.WaitForLifecycleAsync();

        for (var i = 0; i < CTraderTradeSession.StormThreshold + 1; i++)
        {
            transports[^1].Logon(CTraderSessionRole.Trade);
            clock += 2_000;
            transports[^1].Logout(CTraderSessionRole.Trade);
        }

        await session.WaitForLifecycleAsync();
        Assert.Single(transports);

        clock += CTraderTradeSession.StormRetryCooldownMs;
        session.Tick();
        await session.WaitForLifecycleAsync();

        Assert.Equal(2, transports.Count);
        Assert.Contains("start:Trade", transports[^1].Calls);
    }

    [Fact]
    public async Task Trade_StormTripsAndStopsSession()
    {
        var transports = new List<FakeCTraderTransport>();
        var events = new List<CTraderTradeSessionEvent>();
        long clock = 500_000;
        var session = new CTraderTradeSession(_ =>
        {
            var t = new FakeCTraderTransport();
            transports.Add(t);
            return t;
        }, () => clock, () => 1_789_000_000_000, startTimer: false);
        session.EventRaised += events.Add;

        session.EnsureState("ctrader", CTraderQuoteSessionTests.ValidConfig());
        await session.WaitForLifecycleAsync();

        for (var i = 0; i < CTraderTradeSession.StormThreshold + 1; i++)
        {
            transports[^1].Logon(CTraderSessionRole.Trade);
            clock += 2_000;
            transports[^1].Logout(CTraderSessionRole.Trade);
        }

        await session.WaitForLifecycleAsync();

        Assert.Single(events, e => e.Kind == CTraderTradeEventKind.ReconnectStorm);
        Assert.True(transports[0].Disposed);
        Assert.False(session.ReadTrades(TradeMap, quoteLoggedOn: true).IsMapAvailable);
        Assert.False(session.ReadHistory(CTraderRoutingRules.HistoryMapName, true, 100).IsMapAvailable);
    }
}
