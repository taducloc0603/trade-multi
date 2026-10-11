using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Services.PrimeXbt;
using TradeDesktop.Infrastructure.PrimeXbt.Browser;

namespace TradeDesktop.App.Services;

// Cửa sổ Chrome PrimeXBT luôn mở để xử lý tay (docs/plans/primexbt — phương án B "Chrome + CDP").
// - Chrome dùng profile RIÊNG + `--remote-debugging-port=0` (Chrome 136+ chặn cờ debug trên profile mặc định).
// - Chrome sống độc lập với app: app thoát/crash thì Chrome vẫn mở; bấm lại nút sẽ ATTACH qua DevToolsActivePort.
// - Vòng sync chỉ ĐỌC JWT/cookie qua CDP và ghi kho DPAPI khi PrimeXbtSessionSyncDecision cho phép; không bao giờ Clear.
// - Sync chỉ chạy sau khi user bấm nút (không tự attach lúc khởi động) ⇒ Logout trong app không bị ghi đè sau restart.
// KHÔNG thêm `--remote-allow-origins` / `--enable-automation`.
public interface IPrimeXbtBrowserService
{
    string Status { get; }

    bool IsSyncing { get; }

    event Action? StatusChanged;

    Task OpenOrFocusAsync();

    void StopSync();
}

public sealed class PrimeXbtChromeBrowserService : IPrimeXbtBrowserService, IDisposable
{
    public const string SignInUrl = "https://primexbt.com/my/id/sign-in";

    private static readonly TimeSpan PendingInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SyncedInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WarnThrottle = TimeSpan.FromMinutes(5);
    // Chrome bận có thể trễ một lần probe /json/version (timeout 3 s) — chỉ coi là đã đóng sau N lần liên tiếp.
    private const int ClosedAfterConsecutiveProbeFailures = 3;

    private readonly IPrimeXbtTokenStore _tokenStore;
    private readonly ITradeSessionFileLogger _logger;
    private readonly string _profileDir;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _syncCts;
    // Tăng mỗi lần StopSync (Logout): luồng mở/attach đang chạy dở thuộc "thế hệ" cũ không được tự StartSync lại —
    // nếu không, Logout trong lúc Chrome đang mở sẽ bị một lần sync sau đó ghi token trở lại.
    private int _generation;
    private DateTime _lastWarnUtc = DateTime.MinValue;
    private string _status = "Chưa mở Chrome PrimeXBT";

    public PrimeXbtChromeBrowserService(IPrimeXbtTokenStore tokenStore, ITradeSessionFileLogger logger)
    {
        _tokenStore = tokenStore;
        _logger = logger;
        _profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TradeDesktop",
            "chrome-primexbt");
    }

    public event Action? StatusChanged;

    public string Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    public bool IsSyncing
    {
        get
        {
            lock (_sync)
            {
                return _syncCts is not null;
            }
        }
    }

    public async Task OpenOrFocusAsync()
    {
        if (!await _openGate.WaitAsync(0).ConfigureAwait(false))
        {
            return; // đang mở/attach — bấm liên tục không mở thêm Chrome
        }

        try
        {
            var ct = CancellationToken.None;
            var generation = Volatile.Read(ref _generation);

            // 1) Chrome của profile này đang chạy có cổng debug ⇒ attach.
            if (await TryGetLiveBrowserAsync(ct).ConfigureAwait(false) is { } live)
            {
                await FocusPrimeXbtTabAsync(live.Port, live.WsUrl, ct).ConfigureAwait(false);
                Log($"[PRIMEXBT][INFO] Chrome attached port={live.Port}");
                WarnDebugPortExposure(live.Port);
                StartSync(generation);
                return;
            }

            // 2) Profile đang bị Chrome giữ nhưng không có cổng ⇒ user đã mở tay không kèm cờ debug.
            if (IsProfileLocked())
            {
                SetStatus("Chrome PrimeXBT đang mở mà không có cổng đồng bộ — đóng cửa sổ Chrome đó rồi bấm lại");
                return;
            }

            // 3) Mở Chrome mới.
            var exe = ChromeExecutableLocator.Find();
            if (exe is null)
            {
                SetStatus("Không tìm thấy Chrome/Edge — dùng nút Đăng nhập (WebView2)");
                return;
            }

            Directory.CreateDirectory(_profileDir);
            DevToolsActivePortFile.TryDelete(_profileDir); // file sót lại từ lần trước không được nhầm là cổng mới
            var startInfo = new ProcessStartInfo(exe) { UseShellExecute = false };
            startInfo.ArgumentList.Add($"--user-data-dir={_profileDir}");
            startInfo.ArgumentList.Add("--remote-debugging-port=0");
            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.ArgumentList.Add("--no-default-browser-check");
            startInfo.ArgumentList.Add(SignInUrl);
            using (Process.Start(startInfo))
            {
            }

            SetStatus("Đang mở Chrome PrimeXBT…");
            var deadline = DateTime.UtcNow + LaunchWait;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _generation))
                {
                    return; // Logout trong lúc đang chờ Chrome khởi động ⇒ không tự bật sync
                }

                if (await TryGetLiveBrowserAsync(ct).ConfigureAwait(false) is { } started)
                {
                    Log($"[PRIMEXBT][INFO] Chrome launched port={started.Port} exe={Path.GetFileName(exe)}");
                    WarnDebugPortExposure(started.Port);
                    StartSync(generation);
                    return;
                }
            }

            SetStatus("Chrome đã mở nhưng không thấy cổng đồng bộ sau 10 s — đóng Chrome PrimeXBT rồi bấm lại");
            Log("[PRIMEXBT][WARN] Chrome launched but DevToolsActivePort not ready");
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi mở Chrome PrimeXBT: " + ex.Message);
            Log($"[PRIMEXBT][WARN] Chrome open failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _openGate.Release();
        }
    }

    public void StopSync()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            Interlocked.Increment(ref _generation);
            cts = _syncCts;
            _syncCts = null; // ApplyReadResult kiểm "còn là cts hiện hành" dưới cùng lock ⇒ không Save sau điểm này
        }

        // Cancel ngoài lock: callback/continuation chạy inline không bị kéo vào vùng khoá.
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (cts is not null)
        {
            SetStatus("Đã dừng đồng bộ token từ Chrome");
        }
    }

    public void Dispose() => StopSync();

    private void StartSync(int generation)
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_syncCts is not null || generation != _generation)
            {
                return; // một vòng sync duy nhất; luồng mở thuộc thế hệ cũ (đã Logout) thì không bật
            }

            cts = _syncCts = new CancellationTokenSource();
        }

        SetStatus("Chrome PrimeXBT đang mở — chờ đăng nhập…");
        _ = Task.Run(() => SyncLoopAsync(cts));
    }

    private async Task SyncLoopAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var probeFailures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var live = await TryGetLiveBrowserAsync(ct).ConfigureAwait(false);
                if (live is null)
                {
                    if (++probeFailures < ClosedAfterConsecutiveProbeFailures)
                    {
                        await Task.Delay(PendingInterval, ct).ConfigureAwait(false);
                        continue;
                    }

                    SetStatus("Chrome PrimeXBT đã đóng — token đã lưu vẫn giữ nguyên");
                    Log("[PRIMEXBT][INFO] Chrome closed, sync stopped");
                    break;
                }

                probeFailures = 0;
                try
                {
                    var result = await PrimeXbtCdpSessionReader.TryReadAsync(live.Port, live.WsUrl, ct).ConfigureAwait(false);
                    ApplyReadResult(result, cts);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetStatus("Lỗi đọc phiên từ Chrome — sẽ thử lại");
                    if (DateTime.UtcNow - _lastWarnUtc >= WarnThrottle)
                    {
                        _lastWarnUtc = DateTime.UtcNow;
                        Log($"[PRIMEXBT][WARN] Chrome CDP read failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                var interval = _tokenStore.Load()?.IsUsableAt(DateTime.UtcNow) == true ? SyncedInterval : PendingInterval;
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_syncCts, cts))
                {
                    _syncCts = null;
                }
            }

            cts.Dispose();
            StatusChanged?.Invoke();
        }
    }

    private void ApplyReadResult(PrimeXbtCdpReadResult result, CancellationTokenSource owner)
    {
        var ct = owner.Token;
        if (result.Session is not { } session)
        {
            SetStatus(result.Reason switch
            {
                PrimeXbtCdpReadResult.NoPrimeXbtTab => "Không có tab primexbt.com trong Chrome — mở lại trang để đồng bộ",
                PrimeXbtCdpReadResult.MissingCookies => "Đã thấy token, đang chờ cookie phiên của api.primexbt.com…",
                _ => "Chrome PrimeXBT đang mở — chờ đăng nhập…"
            });
            return;
        }

        bool saved;
        lock (_sync)
        {
            // StopSync bỏ _syncCts dưới cùng lock ⇒ Logout (StopSync rồi Clear) không thể bị một lần Save dở dang ghi đè.
            saved = ReferenceEquals(_syncCts, owner) && !ct.IsCancellationRequested
                && PrimeXbtSessionSyncDecision.ShouldSave(_tokenStore.Load(), session);
            if (saved)
            {
                _tokenStore.Save(session);
            }
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (saved)
        {
            // PrimeXbtSession.ToString chỉ có hạn + tên cookie.
            Log($"[PRIMEXBT][INFO] Browser session synced: {session}");
        }

        var expires = session.JwtExpiresUtc?.ToLocalTime();
        SetStatus($"Chrome PrimeXBT đang mở — đồng bộ lúc {DateTime.Now:HH:mm:ss}, JWT web hết hạn {expires:dd/MM HH:mm}");
    }

    private sealed record LiveBrowser(int Port, string WsUrl);

    private async Task<LiveBrowser?> TryGetLiveBrowserAsync(CancellationToken ct)
    {
        if (!DevToolsActivePortFile.TryRead(_profileDir, out var port, out _))
        {
            return null;
        }

        var wsUrl = await ChromeDevToolsHttp.TryGetBrowserWebSocketUrlAsync(port, ct).ConfigureAwait(false);
        return string.IsNullOrEmpty(wsUrl) ? null : new LiveBrowser(port, wsUrl);
    }

    private async Task FocusPrimeXbtTabAsync(int port, string browserWsUrl, CancellationToken ct)
    {
        var tabId = PrimeXbtCdpSessionReader.FindPrimeXbtTabId(
            await ChromeDevToolsHttp.GetTargetsJsonAsync(port, ct).ConfigureAwait(false));
        if (tabId is null)
        {
            await ChromeDevToolsHttp.OpenNewTabAsync(port, SignInUrl, ct).ConfigureAwait(false);
            return;
        }

        await ChromeDevToolsHttp.ActivateTargetAsync(port, tabId, ct).ConfigureAwait(false);

        // Best-effort: cửa sổ đang thu nhỏ thì khôi phục. Lỗi ở đây không ảnh hưởng sync.
        try
        {
            await using var browser = await ChromeDevToolsClient.ConnectAsync(browserWsUrl, ct).ConfigureAwait(false);
            var window = await browser.SendAsync("Browser.getWindowForTarget", new { targetId = tabId }, ct).ConfigureAwait(false);
            if (window.TryGetProperty("windowId", out var windowId)
                && window.TryGetProperty("bounds", out var bounds)
                && bounds.TryGetProperty("windowState", out var state)
                && state.ValueKind == JsonValueKind.String
                && state.GetString() == "minimized")
            {
                await browser.SendAsync(
                    "Browser.setWindowBounds",
                    new { windowId = windowId.GetInt32(), bounds = new { windowState = "normal" } },
                    ct).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    // Chrome giữ khoá độc quyền file `lockfile` trong user-data-dir khi profile đang mở.
    private bool IsProfileLocked()
    {
        var lockFile = Path.Combine(_profileDir, "lockfile");
        if (!File.Exists(lockFile))
        {
            return false;
        }

        try
        {
            using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false; // file sót lại, không ai giữ
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Cổng CDP chỉ nghe 127.0.0.1 nhưng KHÔNG xác thực: mọi process và mọi user Windows trên máy (VPS nhiều user RDP)
    // đều đọc được cookie / điều khiển tab PrimeXBT đã đăng nhập. Chrome sống cả sau khi app thoát.
    private void WarnDebugPortExposure(int port)
        => Log($"[PRIMEXBT][WARN] Chrome PrimeXBT mở cổng debug 127.0.0.1:{port} không xác thực — mọi process/user trên máy " +
               "truy cập được phiên đăng nhập. Đóng cửa sổ Chrome PrimeXBT khi không dùng; không chạy trên máy nhiều user.");

    private void SetStatus(string status)
    {
        lock (_sync)
        {
            if (_status == status)
            {
                return;
            }

            _status = status;
        }

        StatusChanged?.Invoke();
    }

    private void Log(string line)
    {
        try
        {
            _logger.Log(line);
        }
        catch
        {
            // ignored by design
        }
    }
}
