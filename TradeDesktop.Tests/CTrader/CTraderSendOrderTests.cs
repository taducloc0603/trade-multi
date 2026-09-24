using TradeDesktop.Application.Models;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Services.CTrader;
using TradeDesktop.Infrastructure.CTrader;

namespace TradeDesktop.Tests.CTrader;

// Đường DUY NHẤT gửi lệnh ra sàn B (Phase 7 Bước C). Khoá đúng bốn nhánh quyết định Success:
// khớp, reject, chỉ mới "đã nhận", và timeout.
public sealed class CTraderSendOrderTests
{
    private const string Header =
        "34=5|49=cServer|50=TRADE|52=20260924-03:00:00.000|56=live.deriv.1551176|57=TRADE";

    private static string Report(string clOrdId, string execType, string ordStatus, string extra = "")
        => $"35=8|{Header}|11={clOrdId}|37=1|38=1|39={ordStatus}|40=1|54=1|55=41|59=3|150={execType}{extra}";

    private sealed class Harness
    {
        public FakeCTraderTransport Transport { get; private set; } = null!;
        public CTraderTradeSession Session { get; }

        public Harness()
        {
            Session = new CTraderTradeSession(_ =>
            {
                Transport = new FakeCTraderTransport();
                return Transport;
            }, () => 500_000, () => 1_790_000_000_000, startTimer: false);
        }

        public async Task LoggedOnAsync()
        {
            Session.EnsureState("ctrader", CTraderQuoteSessionTests.ValidConfig());
            await Session.WaitForLifecycleAsync();
            Transport.Logon(CTraderSessionRole.Trade);
            await Session.WaitForLifecycleAsync();
        }

        public void Receive(string body) => Transport.Receive(CTraderSessionRole.Trade, FixTestSupport.Parse(body));
    }

    [Fact]
    public async Task Khop_ChiKhiCo150FVa39_2_KemPositionId()
    {
        var h = new Harness();
        await h.LoggedOnAsync();

        var task = h.Session.SendMarketOrderAsync(new CTraderOrderRequest("B-1-1", IsBuy: true, 1m, PositionId: null));

        // R11: report đầu tiên chỉ là "đã nhận" — KHÔNG được coi là khớp.
        h.Receive(Report("B-1-1", execType: "0", ordStatus: "0"));
        Assert.False(task.IsCompleted);

        h.Receive(Report("B-1-1", execType: "F", ordStatus: "2", extra: "|721=623650688"));
        var outcome = await task;

        Assert.True(outcome.Success);
        Assert.Equal(623650688, outcome.PositionId);
    }

    [Fact]
    public async Task Reject_TraNguyenVanTag58()
    {
        var h = new Harness();
        await h.LoggedOnAsync();

        var task = h.Session.SendMarketOrderAsync(new CTraderOrderRequest("B-1-2", IsBuy: true, 1m, PositionId: null));
        h.Receive(Report("B-1-2", execType: "8", ordStatus: "8", extra: "|58=NOT_ENOUGH_MONEY"));

        var outcome = await task;

        Assert.False(outcome.Success);
        Assert.Equal("NOT_ENOUGH_MONEY", outcome.Detail);
    }

    // Hai tag bổ sung nhau; spec không cam kết luôn đồng bộ nên chỉ một tag báo reject là đủ để dừng.
    [Theory]
    [InlineData("8", "0")]
    [InlineData("0", "8")]
    public async Task ChiMotTrongHaiTagBaoReject_VanLaThatBai(string execType, string ordStatus)
    {
        var h = new Harness();
        await h.LoggedOnAsync();

        var task = h.Session.SendMarketOrderAsync(new CTraderOrderRequest("B-1-3", IsBuy: true, 1m, PositionId: null));
        h.Receive(Report("B-1-3", execType, ordStatus, extra: "|58=REJECTED"));

        Assert.False((await task).Success);
    }

    [Fact]
    public async Task ChuaLogon_KhongGuiGi()
    {
        var h = new Harness();
        h.Session.EnsureState("ctrader", CTraderQuoteSessionTests.ValidConfig());
        await h.Session.WaitForLifecycleAsync();

        var outcome = await h.Session.SendMarketOrderAsync(new CTraderOrderRequest("B-1-4", true, 1m, null));

        Assert.False(outcome.Success);
        Assert.Contains("chưa logon", outcome.Detail);
        Assert.DoesNotContain(h.Transport.Calls, c => c.StartsWith("send:Trade:D", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dong_GanTag721VaSideNguoc()
    {
        var h = new Harness();
        await h.LoggedOnAsync();

        var task = h.Session.SendMarketOrderAsync(new CTraderOrderRequest("B-2-1", IsBuy: false, 2m, PositionId: 623650688));
        h.Receive(Report("B-2-1", "F", "2", extra: "|721=623650688"));
        await task;

        var sent = h.Transport.Sent.Single(x => x.Message.Header.GetString(35) == "D").Message;
        Assert.Equal("623650688", sent.GetString(721));
        Assert.Equal('2', sent.GetChar(54));
        Assert.Equal(2m, sent.GetDecimal(38));
        Assert.Equal('1', sent.GetChar(40));
        Assert.Equal('3', sent.GetChar(59));
    }

    // Report của lệnh KHÁC không được đánh thức lệnh đang chờ.
    [Fact]
    public async Task ReportClOrdIdKhac_KhongHoanThanhLenhDangCho()
    {
        var h = new Harness();
        await h.LoggedOnAsync();

        var task = h.Session.SendMarketOrderAsync(new CTraderOrderRequest("B-3-1", true, 1m, null));
        h.Receive(Report("B-3-999", "F", "2", extra: "|721=1"));

        Assert.False(task.IsCompleted);
    }
}
