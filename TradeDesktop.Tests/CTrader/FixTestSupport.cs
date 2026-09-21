using QuickFix;
using QuickFix.DataDictionary;
using TradeDesktop.Application.Models;
using TradeDesktop.Infrastructure.CTrader;

namespace TradeDesktop.Tests.CTrader;

internal static class FixTestSupport
{
    private static readonly Lazy<DataDictionary> Dictionary = new(() =>
        new DataDictionary(Path.Combine(AppContext.BaseDirectory, "FIX44-CSERVER.xml")));

    public static DataDictionary CServerDictionary => Dictionary.Value;

    // Chuỗi raw dạng '|' như log Phase 0 → Message typed, parse group theo dictionary cServer.
    // Tự tính lại BodyLength/CheckSum nên có thể viết message tay mà không cần đếm byte.
    public static Message Parse(string pipeSeparatedBody)
    {
        var body = pipeSeparatedBody.Trim('|').Replace('|', '\u0001') + "\u0001";
        var withoutTrailer = $"8=FIX.4.4\u00019={body.Length}\u0001{body}";
        var checksum = withoutTrailer.Sum(c => (byte)c) % 256;
        var raw = $"{withoutTrailer}10={checksum:000}\u0001";
        var message = new DefaultMessageFactory().Create("FIX.4.4", QuickFix.Message.GetMsgType(raw));
        message.FromString(raw, true, CServerDictionary, CServerDictionary, new DefaultMessageFactory());
        return message;
    }

    public static CTraderSessionHealth Health(bool quote = true, bool trade = true, bool symbol = true, bool synced = true, bool top = true)
        => new(quote, trade, symbol, synced, top, 0);
}

// Thay tầng transport trong test: ghi lại message gửi, cho phép bắn sự kiện logon/logout/nhận message.
internal sealed class FakeCTraderTransport : ICTraderFixTransport, IDisposable
{
    public List<string> Calls { get; } = [];

    public bool Disposed { get; private set; }

    public void Start(CTraderSessionRole role) => Calls.Add($"start:{role}");

    // Giống QuickFIX/n: Stop khi đang logon phát OnLogout.
    public void Stop(CTraderSessionRole role)
    {
        Calls.Add($"stop:{role}");
        if (_loggedOn.Contains(role))
        {
            Logout(role);
        }
    }

    public void Dispose()
    {
        Calls.Add("dispose");
        Disposed = true;
    }

    private readonly HashSet<CTraderSessionRole> _loggedOn = [];

    public List<(CTraderSessionRole Role, Message Message)> Sent { get; } = [];

    public event Action<CTraderSessionRole, Message>? MessageReceived;
    public event Action<CTraderSessionRole>? LoggedOn;
    public event Action<CTraderSessionRole>? LoggedOut;

    public bool IsLoggedOn(CTraderSessionRole role) => _loggedOn.Contains(role);

    public bool Send(CTraderSessionRole role, Message message)
    {
        if (!_loggedOn.Contains(role))
        {
            return false;
        }

        Sent.Add((role, message));
        Calls.Add($"send:{role}:{message.Header.GetString(35)}");
        return true;
    }

    public void Logon(CTraderSessionRole role)
    {
        _loggedOn.Add(role);
        LoggedOn?.Invoke(role);
    }

    public void Logout(CTraderSessionRole role)
    {
        _loggedOn.Remove(role);
        LoggedOut?.Invoke(role);
    }

    public void Receive(CTraderSessionRole role, Message message) => MessageReceived?.Invoke(role, message);
}
