using System.Text;
using QuickFix;
using QuickFix.Fields;
using QuickFix.Transport;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.CTrader;

namespace TradeDesktop.Infrastructure.CTrader;

// LỚP DUY NHẤT chạm SocketInitiator / Session / IApplication / SessionSettings.
// Hai initiator độc lập (QUOTE, TRADE) theo topology AspNetCoreSample/Services/FixClient.cs của Spotware:
// Phase 4 start riêng QUOTE, Phase 5 thêm TRADE.
//
// Rule E — lớp này THUẦN BỊ ĐỘNG. Báo cáo trạng thái, gửi message được ra lệnh, hết. KHÔNG BAO GIỜ tự
// flatten position, tự retry, tự reconcile bằng cách gửi lệnh — đó là đường open/close bỏ qua signal engine.
//
// Mật khẩu: KHÔNG bật FileLogPath (nó ghi Logon nguyên văn ra đĩa). Khi bật chẩn đoán thì dùng
// MaskedFixLogFactory — che 554 trước khi ghi — để thấy cả message bị QuickFIX/n loại ở tầng parse/verify.
public sealed class QuickFixCTraderTransport : ICTraderFixTransport, IDisposable
{
    private readonly RoleApplication _quoteApp;
    private readonly RoleApplication _tradeApp;
    private readonly SocketInitiator _quoteInitiator;
    private readonly SocketInitiator _tradeInitiator;
    private readonly Action<string>? _log;

    public QuickFixCTraderTransport(CTraderFixConfig config, string dataDictionaryPath, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var fix = config.Normalize();
        _log = log;

        _quoteApp = new RoleApplication(this, CTraderSessionRole.Quote, fix.ResolveUsername(), fix.Password);
        _tradeApp = new RoleApplication(this, CTraderSessionRole.Trade, fix.ResolveUsername(), fix.Password);

        // Chỉ gắn log factory khi chẩn đoán được bật; mặc định giữ nguyên hành vi cũ (không ILogFactory).
        var logFactory = log is null ? null : new MaskedFixLogFactory(log);

        _quoteInitiator = CreateInitiator(_quoteApp, fix, CTraderSessionRole.Quote, dataDictionaryPath, logFactory);
        _tradeInitiator = CreateInitiator(_tradeApp, fix, CTraderSessionRole.Trade, dataDictionaryPath, logFactory);
    }

    private static SocketInitiator CreateInitiator(
        IApplication app,
        CTraderFixConfig fix,
        CTraderSessionRole role,
        string dataDictionaryPath,
        ILogFactory? logFactory)
    {
        var settings = new SessionSettings(new StringReader(BuildSessionSettingsText(fix, role, dataDictionaryPath)));
        return new SocketInitiator(
            app,
            new MemoryStoreFactory(),
            settings,
            logFactory ?? new NullLogFactory(),
            CreateMessageFactory());
    }

    // P5-A1 (nguyên nhân gốc của P5-O1, chẩn đoán 2026-09-23): KHÔNG để QuickFIX/n tự dựng bảng message
    // factory. `DefaultMessageFactory()` không tham số QUÉT FILE DLL trong thư mục app (`LoadLocalDlls`) rồi
    // gom các `IMessageFactory` tìm được; mỗi session dựng một bảng RIÊNG. Lần quét nào không bắt được
    // `QuickFix.FIX44.dll` thì bảng thiếu khoá "FIX.4.4", và khi đó MỌI message có repeating group
    // (`35=y` nhóm 146, `35=W` nhóm 268) ném `UnsupportedVersion: Incorrect BeginString (FIX.4.4)` ngay trong
    // `Message.SetGroup` → thư viện tự gửi `Logout 58=Incorrect BeginString` → vòng lặp logout/logon.
    // Đo trên live 2026-09-22 20:43:10: QUOTE dính 6 lần liên tiếp trong khi TRADE cùng tiến trình vẫn parse
    // đúng cùng một message. Parse tự nó KHÔNG lỗi (80 000 lần, 1/2/8 luồng, 0 lần ném).
    // Ở đây khai báo thẳng factory FIX 4.4 — hết phụ thuộc vào thư mục và thứ tự nạp assembly.
    // Dùng thẳng factory FIX 4.4 (ctor DefaultMessageFactory nhận danh sách đã [Obsolete]); cả phiên chỉ nói
    // FIX 4.4 nên đây là bảng đầy đủ: message admin, message nghiệp vụ và mọi repeating group.
    private static IMessageFactory CreateMessageFactory() => new QuickFix.FIX44.MessageFactory();

    public event Action<CTraderSessionRole, Message>? MessageReceived;
    public event Action<CTraderSessionRole>? LoggedOn;
    public event Action<CTraderSessionRole>? LoggedOut;

    public bool IsLoggedOn(CTraderSessionRole role) => AppFor(role).IsLoggedOn;

    public void Start(CTraderSessionRole role) => InitiatorFor(role).Start();

    public void Stop(CTraderSessionRole role) => InitiatorFor(role).Stop();

    public bool Send(CTraderSessionRole role, Message message)
    {
        var sessionId = AppFor(role).SessionId;
        if (sessionId is null || !AppFor(role).IsLoggedOn)
        {
            return false;
        }

        return Session.SendToTarget(message, sessionId);
    }

    public void Dispose()
    {
        _quoteInitiator.Dispose();
        _tradeInitiator.Dispose();
    }

    // Khoá theo Config.cfg chính thức của Spotware (README §4.6). MemoryStoreFactory + ResetOnLogon/ResetOnDisconnect
    // (Phase 0 câu 7: seqnum về 1, không ResendRequest). SSL qua SSLEnable/SSLServerName (Phase 0: 5212 SSL chạy).
    // KHÔNG chứa mật khẩu, KHÔNG có FileLogPath/FileStorePath.
    public static string BuildSessionSettingsText(CTraderFixConfig config, CTraderSessionRole role, string dataDictionaryPath)
    {
        var fix = config.Normalize();
        var endpoint = role == CTraderSessionRole.Quote ? fix.Quote : fix.Trade;
        var subId = SubIdFor(role);

        var sb = new StringBuilder();
        sb.AppendLine("[DEFAULT]");
        sb.AppendLine("ConnectionType=initiator");
        sb.AppendLine("ReconnectInterval=2");
        sb.AppendLine("StartTime=00:00:00");
        sb.AppendLine("EndTime=00:00:00");
        sb.AppendLine("UseDataDictionary=Y");
        sb.AppendLine($"DataDictionary={dataDictionaryPath}");
        sb.AppendLine($"SocketConnectHost={endpoint.Host}");
        sb.AppendLine($"SocketConnectPort={fix.ActivePort(role)}");
        sb.AppendLine($"SSLEnable={(fix.UseSsl ? "Y" : "N")}");
        if (fix.UseSsl)
        {
            sb.AppendLine($"SSLServerName={endpoint.Host}");
            sb.AppendLine("SSLValidateCertificates=Y");
        }

        sb.AppendLine("LogoutTimeout=100");
        sb.AppendLine("ResetOnLogon=Y");
        sb.AppendLine("ResetOnDisconnect=Y");
        sb.AppendLine("[SESSION]");
        sb.AppendLine("BeginString=FIX.4.4");
        sb.AppendLine($"SenderCompID={fix.SenderCompId}");
        sb.AppendLine($"SenderSubID={subId}");
        sb.AppendLine($"TargetSubID={subId}");
        sb.AppendLine($"TargetCompID={fix.TargetCompId}");
        sb.AppendLine("HeartBtInt=30");
        return sb.ToString();
    }

    public static string SubIdFor(CTraderSessionRole role) => role == CTraderSessionRole.Quote ? "QUOTE" : "TRADE";

    // Pitfall #0: QuickFIX/n KHÔNG tự gắn 553/554 từ cfg — thiếu thì server bỏ qua Logon im lặng.
    // CHỈ gắn vào Logon (35=A): Phase 0 câu 7b đo trên live, cServer reject 553/554 trên Logout bằng 35=3.
    public static bool ApplyLogonCredentials(Message message, string username, string password)
    {
        if (FixFieldReader.MsgType(message) != "A")
        {
            return false;
        }

        message.SetField(new StringField(553, username), true);
        message.SetField(new StringField(554, password), true);
        return true;
    }

    private void RaiseMessage(CTraderSessionRole role, Message message, string direction)
    {
        if (_log is not null)
        {
            try
            {
                _log($"[CTRADER][FIX][{SubIdFor(role)}][{direction}] {CTraderFixLogMasker.Apply(message.ToString().Replace('\u0001', '|'))}");
            }
            catch
            {
                // Log không được làm hỏng session.
            }
        }

        if (direction == "IN")
        {
            MessageReceived?.Invoke(role, message);
        }
    }

    private RoleApplication AppFor(CTraderSessionRole role) => role == CTraderSessionRole.Quote ? _quoteApp : _tradeApp;

    private SocketInitiator InitiatorFor(CTraderSessionRole role)
        => role == CTraderSessionRole.Quote ? _quoteInitiator : _tradeInitiator;

    private sealed class RoleApplication(
        QuickFixCTraderTransport owner,
        CTraderSessionRole role,
        string username,
        string password) : IApplication
    {
        private volatile bool _loggedOn;

        public SessionID? SessionId { get; private set; }

        public bool IsLoggedOn => _loggedOn;

        public void OnCreate(SessionID sessionID) => SessionId = sessionID;

        public void OnLogon(SessionID sessionID)
        {
            _loggedOn = true;
            owner.LoggedOn?.Invoke(role);
        }

        public void OnLogout(SessionID sessionID)
        {
            _loggedOn = false;
            owner.LoggedOut?.Invoke(role);
        }

        public void ToAdmin(Message message, SessionID sessionID)
        {
            ApplyLogonCredentials(message, username, password);
            owner.RaiseMessage(role, message, "OUT");
        }

        public void FromAdmin(Message message, SessionID sessionID) => owner.RaiseMessage(role, message, "IN");

        public void ToApp(Message message, SessionID sessionId) => owner.RaiseMessage(role, message, "OUT");

        public void FromApp(Message message, SessionID sessionID) => owner.RaiseMessage(role, message, "IN");
    }
}
