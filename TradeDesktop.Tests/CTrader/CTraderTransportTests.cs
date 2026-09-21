using QuickFix;
using QuickFix.Fields;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.CTrader;
using TradeDesktop.Infrastructure.CTrader;

namespace TradeDesktop.Tests.CTrader;

public sealed class CTraderTransportTests
{
    private const string Password = "Pw-Test-Only$$";

    private static CTraderFixConfig Config() => CTraderFixConfig.Empty with
    {
        Quote = new CTraderEndpoint("live-uk-eqx-01.p.c-trader.com", 5211, 5201),
        Trade = new CTraderEndpoint("live-uk-eqx-01.p.c-trader.com", 5212, 5202),
        SenderCompId = "live.fxpro.8220816",
        Password = Password,
        SymbolId = 41,
    };

    // Round-trip qua chuỗi FIX + validate bằng FIX44-CSERVER.xml: cTrader drop im lặng message sai.
    private static void AssertValidAgainstDictionary(Message message)
    {
        message.Header.SetField(new SenderCompID("live.fxpro.8220816"));
        message.Header.SetField(new TargetCompID("cServer"));
        message.Header.SetField(new MsgSeqNum(2));
        message.Header.SetField(new SendingTime(DateTime.UtcNow));

        var raw = message.ToString();
        var parsed = new Message();
        parsed.FromString(raw, true, FixTestSupport.CServerDictionary, FixTestSupport.CServerDictionary, new DefaultMessageFactory());
        FixTestSupport.CServerDictionary.Validate(parsed, "FIX.4.4", parsed.Header.GetString(35));
    }

    [Fact]
    public void SessionSettings_ContainNoPasswordAndNoFileLog()
    {
        foreach (var role in new[] { CTraderSessionRole.Quote, CTraderSessionRole.Trade })
        {
            var text = QuickFixCTraderTransport.BuildSessionSettingsText(Config(), role, "FIX44-CSERVER.xml");

            Assert.DoesNotContain(Password, text);
            Assert.DoesNotContain("FileLogPath", text);
            Assert.DoesNotContain("FileStorePath", text);
            Assert.Contains("TargetCompID=cServer", text);
            Assert.Contains("SSLEnable=Y", text);
            Assert.Contains("ResetOnLogon=Y", text);
            Assert.Contains($"SenderSubID={QuickFixCTraderTransport.SubIdFor(role)}", text);
            _ = new SessionSettings(new StringReader(text));
        }

        Assert.Contains("SocketConnectPort=5211", QuickFixCTraderTransport.BuildSessionSettingsText(Config(), CTraderSessionRole.Quote, "d"));
        Assert.Contains("SocketConnectPort=5212", QuickFixCTraderTransport.BuildSessionSettingsText(Config(), CTraderSessionRole.Trade, "d"));
    }

    [Fact]
    public void SessionSettings_PlainPortWhenSslOff()
    {
        var text = QuickFixCTraderTransport.BuildSessionSettingsText(Config() with { UseSsl = false }, CTraderSessionRole.Trade, "d");

        Assert.Contains("SocketConnectPort=5202", text);
        Assert.Contains("SSLEnable=N", text);
        Assert.DoesNotContain("SSLServerName", text);
    }

    [Fact]
    public void LogonCredentials_AddedOnlyToLogon_AndMaskedInLog()
    {
        var logon = new QuickFix.FIX44.Logon();

        Assert.True(QuickFixCTraderTransport.ApplyLogonCredentials(logon, "8220816", Password));
        Assert.Equal("8220816", logon.GetString(553));
        Assert.Equal(Password, logon.GetString(554));

        var logged = CTraderFixLogMasker.Apply(logon.ToString().Replace('\u0001', '|'));
        Assert.DoesNotContain(Password, logged);
        Assert.Contains("554=***", logged);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("2")]
    [InlineData("4")]
    public void LogonCredentials_NeverOnOtherAdminMessages(string msgType)
    {
        var message = new Message();
        message.Header.SetField(new MsgType(msgType));

        Assert.False(QuickFixCTraderTransport.ApplyLogonCredentials(message, "8220816", Password));
        Assert.False(message.IsSetField(553));
        Assert.False(message.IsSetField(554));
    }

    [Fact]
    public void Factory_MarketDataRequest_SpotAndValid()
    {
        var subscribe = CTraderMessageFactory.MarketDataRequest(41, subscribe: true);

        Assert.Equal("MARKETDATAID", subscribe.MDReqID.getValue());
        Assert.Equal(1, subscribe.MarketDepth.getValue());
        Assert.Equal('1', subscribe.SubscriptionRequestType.getValue());
        Assert.Contains("\u0001267=2\u0001269=0\u0001269=1\u0001", subscribe.ToString());
        Assert.Contains("\u0001146=1\u000155=41\u0001", subscribe.ToString());
        AssertValidAgainstDictionary(subscribe);

        var unsubscribe = CTraderMessageFactory.MarketDataRequest(41, subscribe: false);
        Assert.Equal('2', unsubscribe.SubscriptionRequestType.getValue());
        AssertValidAgainstDictionary(unsubscribe);
    }

    [Fact]
    public void Factory_SecurityListAndPositionsRequest_Valid()
    {
        var securityList = CTraderMessageFactory.SecurityListRequest("sec-1");
        Assert.Equal(0, securityList.SecurityListRequestType.getValue());
        AssertValidAgainstDictionary(securityList);

        var positions = CTraderMessageFactory.RequestForPositions("pos-1");
        Assert.False(positions.IsSetField(721));
        Assert.Equal("pos-1", positions.GetString(710));
        AssertValidAgainstDictionary(positions);
    }

    [Fact]
    public void Factory_MarketOrder_OpenHasNo721_CloseHas721()
    {
        var open = CTraderMessageFactory.MarketOrder("o1", 41, isBuy: true, 100m, positionId: null, DateTime.UtcNow);
        Assert.False(open.IsSetField(721));
        Assert.Equal(TimeInForce.IMMEDIATE_OR_CANCEL, open.TimeInForce.getValue());
        Assert.Equal(100m, open.OrderQty.getValue());
        Assert.Equal(OrdType.MARKET, open.OrdType.getValue());
        Assert.Equal(Side.BUY, open.Side.getValue());
        Assert.Matches(@"(^|\u0001)55=41\u0001", open.ToString());
        Assert.Matches(@"(^|\u0001)60=\d{8}-\d{2}:\d{2}:\d{2}(\.\d{3})?\u0001", open.ToString());
        AssertValidAgainstDictionary(open);

        var close = CTraderMessageFactory.MarketOrder("c1", 41, isBuy: false, 100m, positionId: 123456, DateTime.UtcNow);
        Assert.Equal("123456", close.GetString(721));
        Assert.Equal(Side.SELL, close.Side.getValue());
        AssertValidAgainstDictionary(close);
    }

    [Fact]
    public void FakeTransport_SendFailsWhenLoggedOut_AndEventsFlow()
    {
        var transport = new FakeCTraderTransport();
        var book = new CTraderQuoteBook(41);
        transport.MessageReceived += (_, m) => book.Apply(m);
        transport.LoggedOut += _ => book.Clear();

        Assert.False(transport.Send(CTraderSessionRole.Quote, CTraderMessageFactory.MarketDataRequest(41, true)));

        transport.Logon(CTraderSessionRole.Quote);
        Assert.True(transport.Send(CTraderSessionRole.Quote, CTraderMessageFactory.MarketDataRequest(41, true)));
        transport.Receive(CTraderSessionRole.Quote, FixTestSupport.Parse(
            "35=W|34=3|49=cServer|52=20260916-14:57:52.978|56=live.fxpro.8220816|55=41|262=MARKETDATAID|268=2|269=0|270=4350.77|269=1|270=4350.93"));
        Assert.True(book.HasTopOfBook);

        transport.Logout(CTraderSessionRole.Quote);
        Assert.False(book.HasTopOfBook);
        Assert.Single(transport.Sent);
    }
}
