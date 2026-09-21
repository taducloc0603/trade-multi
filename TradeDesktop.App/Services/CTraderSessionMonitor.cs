using System.IO;
using System.Text;
using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.App.Services;

// Phase 4/5: cầu nối sự kiện FIX QUOTE + TRADE session → file log riêng + log phiên + Telegram.
//
// File riêng `Desktop/trade-log/{yyyyMMdd}-ctrader.log` (chủ dự án duyệt #5 Phase 4): TradeSessionFileLogger bỏ MỌI dòng
// khi chưa bấm Start, mà soak chạy KHÔNG bấm Start — thiếu file này thì mất dấu logon/logout/giờ nghỉ và số liệu R8.
// Lưu lượng thấp (sự kiện vòng đời + [STATS]/phút), ghi đồng bộ dưới lock riêng, không chạm logger phiên.
//
// Telegram (Phase 4 câu 3, áp cho cả TRADE theo Phase 5 #5): logout mà reconnect trong 30 s thì im; kéo dài > 30 s bắn
// 1 lần; logon lại sau lần bắn đó bắn 1 lần. Dừng chủ động không bắn. Lệch digits bắn ngay. Phase 5 câu 1: chưa sync
// positions sau 60 s → CTRADER_POSITIONS_NOT_SYNCED (session tự đo 60 s và phát đúng một lần mỗi lần logon).
public sealed class CTraderSessionMonitor : IDisposable
{
    private static readonly TimeSpan LogoutAlertDelay = TimeSpan.FromSeconds(30);

    private readonly ICTraderQuoteSession _session;
    private readonly ICTraderTradeSession? _tradeSession;
    private readonly ITradeSessionFileLogger _sessionLogger;
    private readonly ITelegramNotifier _telegram;
    private readonly object _fileLock = new();
    private readonly LogoutAlert _quoteAlert;
    private readonly LogoutAlert _tradeAlert;

    public CTraderSessionMonitor(
        ICTraderQuoteSession session,
        ITradeSessionFileLogger sessionLogger,
        ITelegramNotifier telegram,
        ICTraderTradeSession? tradeSession = null)
    {
        _session = session;
        _tradeSession = tradeSession;
        _sessionLogger = sessionLogger;
        _telegram = telegram;
        _quoteAlert = new LogoutAlert(this, "QUOTE");
        _tradeAlert = new LogoutAlert(this, "TRADE");
        _session.LogLine += OnLogLine;
        _session.EventRaised += OnEvent;
        if (_tradeSession is not null)
        {
            _tradeSession.LogLine += OnLogLine;
            _tradeSession.EventRaised += OnTradeEvent;
        }
    }

    // KHÔNG gỡ LogLine: monitor phụ thuộc session nên DI dispose monitor TRƯỚC session. Gỡ ở đây thì các dòng
    // unsubscribe/stopped lúc thoát app bị mất (đo trên live 2026-09-17 16:52). Chỉ dừng Telegram.
    public void Dispose()
    {
        _session.EventRaised -= OnEvent;
        if (_tradeSession is not null)
        {
            _tradeSession.EventRaised -= OnTradeEvent;
        }

        _quoteAlert.Cancel();
        _tradeAlert.Cancel();
    }

    private void OnLogLine(string line)
    {
        WriteDailyFile(line);

        try
        {
            // Log phiên tự no-op khi chưa Start. [STATS] mỗi phút chỉ vào file, không lên panel realtime.
            if (line.Contains("[STATS]", StringComparison.Ordinal))
            {
                _sessionLogger.LogFileOnly(line);
            }
            else
            {
                _sessionLogger.Log(line);
            }
        }
        catch
        {
            // ignored by design
        }
    }

    private void OnEvent(CTraderQuoteSessionEvent evt)
    {
        switch (evt.Kind)
        {
            case CTraderQuoteEventKind.LoggedOut:
                _quoteAlert.OnLoggedOut(evt.Message);
                break;
            case CTraderQuoteEventKind.LoggedOn:
                _quoteAlert.OnLoggedOn();
                break;
            case CTraderQuoteEventKind.Stopped:
                _quoteAlert.Cancel();
                break;
            case CTraderQuoteEventKind.DigitsMismatch:
                Notify("CTRADER_DIGITS_MISMATCH", "ERROR", $"Fail-closed sàn B: {evt.Message}");
                break;
        }
    }

    private void OnTradeEvent(CTraderTradeSessionEvent evt)
    {
        switch (evt.Kind)
        {
            case CTraderTradeEventKind.LoggedOut:
                _tradeAlert.OnLoggedOut(evt.Message);
                break;
            case CTraderTradeEventKind.LoggedOn:
                _tradeAlert.OnLoggedOn();
                break;
            case CTraderTradeEventKind.Stopped:
                _tradeAlert.Cancel();
                break;
            case CTraderTradeEventKind.PositionsNotSynced:
                Notify("CTRADER_POSITIONS_NOT_SYNCED", "WARN", evt.Message);
                break;
        }
    }

    private void Notify(string eventCode, string severity, string detail)
    {
        WriteDailyFile($"[CTRADER][TELEGRAM] {eventCode} {severity} {detail}");
        _ = _telegram.NotifyAsync(eventCode, severity, detail);
    }

    private void WriteDailyFile(string line)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "trade-log");
            var path = Path.Combine(directory, $"{now:yyyyMMdd}-ctrader.log");
            lock (_fileLock)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(path, $"[{now:yyyy-MM-dd HH:mm:ss.fff}] {line}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // Lỗi ghi file không được làm hỏng session.
        }
    }

    // Debounce logout/logon cho một kênh (QUOTE hoặc TRADE).
    private sealed class LogoutAlert(CTraderSessionMonitor owner, string channel)
    {
        private readonly object _lock = new();
        private CancellationTokenSource? _pending;
        private bool _sent;

        public void OnLoggedOut(string detail)
        {
            CancellationTokenSource cts;
            lock (_lock)
            {
                if (_pending is not null || _sent)
                {
                    return;
                }

                cts = new CancellationTokenSource();
                _pending = cts;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(LogoutAlertDelay, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                lock (_lock)
                {
                    if (!ReferenceEquals(_pending, cts))
                    {
                        return;
                    }

                    _pending = null;
                    _sent = true;
                }

                owner.Notify($"CTRADER_{channel}_LOGOUT", "WARN", $"Mất {channel} session > 30 s ({detail}) — sàn B disconnected");
            });
        }

        public void OnLoggedOn()
        {
            bool notifyRecovered;
            lock (_lock)
            {
                _pending?.Cancel();
                _pending = null;
                notifyRecovered = _sent;
                _sent = false;
            }

            if (notifyRecovered)
            {
                owner.Notify($"CTRADER_{channel}_LOGON", "INFO", $"{channel} session đã logon lại");
            }
        }

        public void Cancel()
        {
            lock (_lock)
            {
                _pending?.Cancel();
                _pending = null;
                _sent = false;
            }
        }
    }
}
