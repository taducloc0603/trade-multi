using QuickFix;
using TradeDesktop.Application.Services.CTrader;

namespace TradeDesktop.Infrastructure.CTrader;

// ILogFactory chẩn đoán cho QuickFIX/n (bật bằng CTRADER_FIX_RAW_LOG=1, mặc định TẮT).
//
// Vì sao cần: message bị QuickFIX/n loại ở tầng parse/verify (vd. sự cố 2026-09-21 19:23 — client tự gửi
// Logout `58=Incorrect BeginString`) KHÔNG BAO GIỜ tới IApplication, nên log raw ở tầng app không thấy gì.
// ILog thì nhận cả message hỏng lẫn dòng OnEvent của thư viện.
//
// KHÔNG dùng FileLogFactory của QuickFIX/n: nó ghi Logon nguyên văn ra đĩa, tức ghi luôn mật khẩu tag 554.
// Ở đây mọi dòng đi qua CTraderFixLogMasker trước khi ra ngoài.
public sealed class MaskedFixLogFactory(Action<string> write) : ILogFactory
{
    public ILog Create(SessionID sessionID) => new MaskedFixLog(write, sessionID);

    private sealed class MaskedFixLog(Action<string> write, SessionID sessionID) : ILog
    {
        private readonly string _prefix = $"[FIXLOG][{sessionID.SenderSubID}]";

        public void Clear()
        {
        }

        public void OnIncoming(string msg) => Emit("IN ", msg);

        public void OnOutgoing(string msg) => Emit("OUT", msg);

        public void OnEvent(string s) => Emit("EVT", s);

        public void Dispose()
        {
        }

        private void Emit(string direction, string text)
        {
            try
            {
                write($"{_prefix}[{direction}] {CTraderFixLogMasker.Apply((text ?? string.Empty).Replace('\u0001', '|'))}");
            }
            catch
            {
                // Log chẩn đoán không được làm hỏng session.
            }
        }
    }
}
