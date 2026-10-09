using System.IO;
using System.Text;
using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.App.Services;

// docs/plans/primexbt Phase 4: cầu nối sự kiện phiên `fws` PrimeXBT → file `Desktop/trade-log/{yyyyMMdd}-primexbt.log`
// + log phiên + Telegram. Soi gương CTraderSessionMonitor: file riêng vì log phiên bỏ mọi dòng khi chưa bấm Start;
// mất kết nối > 30 s mới bắn Telegram (reconnect nhanh thì im), có lại sau khi đã bắn thì báo 1 lần;
// cần đăng nhập / ngắt mạch / lệch digits bắn ngay. Dòng log đã được session che JWT/cookie.
public sealed class PrimeXbtSessionMonitor : IDisposable
{
    private static readonly TimeSpan DisconnectAlertDelay = TimeSpan.FromSeconds(30);

    private readonly IPrimeXbtQuoteSession _session;
    private readonly ITradeSessionFileLogger _sessionLogger;
    private readonly ITelegramNotifier _telegram;
    private readonly object _fileLock = new();
    private readonly object _alertLock = new();
    private CancellationTokenSource? _pendingDisconnect;
    private bool _disconnectSent;

    public PrimeXbtSessionMonitor(IPrimeXbtQuoteSession session, ITradeSessionFileLogger sessionLogger, ITelegramNotifier telegram)
    {
        _session = session;
        _sessionLogger = sessionLogger;
        _telegram = telegram;
        _session.LogLine += OnLogLine;
        _session.EventRaised += OnEvent;
    }

    // Giữ LogLine như CTraderSessionMonitor: các dòng "stopped" lúc thoát app vẫn vào file.
    public void Dispose()
    {
        _session.EventRaised -= OnEvent;
        CancelDisconnectAlert();
    }

    private void OnLogLine(string line)
    {
        WriteDailyFile(line);
        try
        {
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

    private void OnEvent(PrimeXbtSessionEvent evt)
    {
        switch (evt.Kind)
        {
            case PrimeXbtSessionEventKind.Disconnected:
                ScheduleDisconnectAlert(evt.Message);
                break;
            case PrimeXbtSessionEventKind.Connected:
                OnReconnected();
                break;
            case PrimeXbtSessionEventKind.Stopped:
                CancelDisconnectAlert();
                break;
            case PrimeXbtSessionEventKind.AuthRequired:
                Notify("PRIMEXBT_AUTH_REQUIRED", "ERROR", evt.Message);
                break;
            case PrimeXbtSessionEventKind.DigitsMismatch:
                Notify("PRIMEXBT_DIGITS_MISMATCH", "ERROR", $"Fail-closed sàn B: {evt.Message}");
                break;
            case PrimeXbtSessionEventKind.PositionModeInvalid:
                Notify("PRIMEXBT_NOT_HEDGE", "ERROR", $"Fail-closed vị thế sàn B: {evt.Message}");
                break;
            case PrimeXbtSessionEventKind.ReconnectStorm:
                CancelDisconnectAlert();
                Notify("PRIMEXBT_RECONNECT_STORM", "ERROR", evt.Message);
                break;
        }
    }

    private void ScheduleDisconnectAlert(string detail)
    {
        CancellationTokenSource cts;
        lock (_alertLock)
        {
            if (_pendingDisconnect is not null || _disconnectSent)
            {
                return;
            }

            cts = new CancellationTokenSource();
            _pendingDisconnect = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DisconnectAlertDelay, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_alertLock)
            {
                if (!ReferenceEquals(_pendingDisconnect, cts))
                {
                    return;
                }

                _pendingDisconnect = null;
                _disconnectSent = true;
            }

            Notify("PRIMEXBT_DISCONNECTED", "WARN", $"Mất kết nối PrimeXBT > 30 s ({detail}) — sàn B disconnected");
        });
    }

    private void OnReconnected()
    {
        bool notify;
        lock (_alertLock)
        {
            _pendingDisconnect?.Cancel();
            _pendingDisconnect = null;
            notify = _disconnectSent;
            _disconnectSent = false;
        }

        if (notify)
        {
            Notify("PRIMEXBT_RECONNECTED", "INFO", "Đã kết nối lại PrimeXBT");
        }
    }

    private void CancelDisconnectAlert()
    {
        lock (_alertLock)
        {
            _pendingDisconnect?.Cancel();
            _pendingDisconnect = null;
            _disconnectSent = false;
        }
    }

    private void Notify(string eventCode, string severity, string detail)
    {
        WriteDailyFile($"[PRIMEXBT][TELEGRAM] {eventCode} {severity} {detail}");
        _ = _telegram.NotifyAsync(eventCode, severity, detail);
    }

    private void WriteDailyFile(string line)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "trade-log");
            var path = Path.Combine(directory, $"{now:yyyyMMdd}-primexbt.log");
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
}
