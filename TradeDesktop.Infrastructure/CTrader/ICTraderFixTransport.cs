using QuickFix;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Infrastructure.CTrader;

// Ranh giới duy nhất với runtime QuickFIX/n (SocketInitiator/Session/IApplication/SessionSettings).
// Test thay bằng FakeCTraderTransport; message typed QuickFix.FIX44.* vẫn dùng thật ở tầng trên.
public interface ICTraderFixTransport
{
    event Action<CTraderSessionRole, Message>? MessageReceived;
    event Action<CTraderSessionRole>? LoggedOn;
    event Action<CTraderSessionRole>? LoggedOut;

    bool IsLoggedOn(CTraderSessionRole role);

    // Phase 4: mở/đóng initiator của từng role. Reconnect do QuickFIX/n tự lo (ReconnectInterval) — không tự viết vòng.
    void Start(CTraderSessionRole role);

    void Stop(CTraderSessionRole role);

    // Trả false khi session chưa logon — KHÔNG throw, KHÔNG tự retry (adapter thuần bị động, Rule E).
    bool Send(CTraderSessionRole role, Message message);
}
