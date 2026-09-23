using System.Reflection;
using QuickFix;
using QuickFix.DataDictionary;

namespace TradeDesktop.Tests.CTrader;

// P5-A1 — nguyên nhân gốc của P5-O1 (chẩn đoán 2026-09-23).
//
// `new DefaultMessageFactory()` KHÔNG tham số quét file DLL trong thư mục app (`LoadLocalDlls`) để dựng bảng
// factory, và mỗi session dựng một bảng RIÊNG. Lần quét nào không bắt được `QuickFix.FIX44.dll` thì bảng thiếu
// khoá "FIX.4.4"; khi đó MỌI message có repeating group ném `UnsupportedVersion` ngay trong `Message.SetGroup`,
// QuickFIX/n tự gửi `Logout 58=Incorrect BeginString` → vòng lặp logout/logon (live 2026-09-22 20:43: QUOTE
// dính 6 lần trong 11 s, TRADE cùng tiến trình vẫn parse đúng cùng message đó).
//
// Test này khoá hai điều: (1) bảng thiếu khoá thì hỏng ĐÚNG như live — tức chẩn đoán đúng; (2) factory khai báo
// tường minh (cái transport đang dùng) parse được cả SecurityList lẫn MarketData.
public sealed class CTraderMessageFactoryWiringTests
{
    private const string SecurityList =
        "35=y|34=2|49=cServer|50=QUOTE|52=20260922-13:43:08.122|56=live.fxpro.8220816|57=QUOTE|320=sec-407565281|322=responce:sec-407565281|560=0|146=1|55=41|1007=XAUUSD|1008=2";

    private const string SpotW =
        "35=W|34=3|49=cServer|50=QUOTE|52=20260922-13:43:11.000|56=live.fxpro.8220816|57=QUOTE|55=41|268=2|269=0|270=4350.77|271=1000000|269=1|270=4350.93|271=1000000";

    private static IMessageFactory TransportFactory()
    {
        // Đúng factory mà QuickFixCTraderTransport.CreateMessageFactory() dựng.
        var method = typeof(TradeDesktop.Infrastructure.CTrader.QuickFixCTraderTransport)
            .GetMethod("CreateMessageFactory", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (IMessageFactory)method!.Invoke(null, null)!;
    }

    [Theory]
    [InlineData(SecurityList, 146)]
    [InlineData(SpotW, 268)]
    public void TransportFactory_ParsesRepeatingGroups(string body, int groupTag)
    {
        var dictionary = FixTestSupport.CServerDictionary;
        var raw = FixTestSupport.RawString(body);

        var message = new Message();
        message.FromString(raw, true, dictionary, dictionary, TransportFactory());

        Assert.True(message.IsSetField(groupTag));
    }

    [Fact]
    public void FactoryWithoutFix44_ReproducesLiveFailure()
    {
        var dictionary = FixTestSupport.CServerDictionary;
        var raw = FixTestSupport.RawString(SecurityList);
        var empty = new DefaultMessageFactory(Array.Empty<IMessageFactory>());

        var ex = Assert.Throws<UnsupportedVersion>(() =>
        {
            var message = new Message();
            message.FromString(raw, true, dictionary, dictionary, empty);
        });

        Assert.Contains("FIX.4.4", ex.Message, StringComparison.Ordinal);
    }
}
