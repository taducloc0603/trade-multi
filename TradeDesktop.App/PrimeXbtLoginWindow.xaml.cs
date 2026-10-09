using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.App;

// docs/plans/primexbt Phase 2: user đăng nhập trên trang PrimeXBT thật (Chromium của WebView2 — qua được Cloudflare,
// khác với HttpClient/curl bị cắt TLS). Cửa sổ chỉ ĐỌC kết quả: localStorage "prm-token" (JWT) + cookie httpOnly của
// api.primexbt.com (fws_token/refresh_token…). Không gõ hộ mật khẩu, không log giá trị.
public partial class PrimeXbtLoginWindow : Window
{
    public const string SignInUrl = "https://primexbt.com/my/id/sign-in";

    // Cookie của api.primexbt.com được đặt theo PATH riêng (đã kiểm 2026-10-09): fws_token=/v2/fws,
    // ws_token=/v2/pws, bws_token=/v2/bws, refresh_token=/v2/auth/refresh (và /v2/auth/check).
    // Hỏi theo URL gốc sẽ KHÔNG trả về cookie nào ⇒ phải hỏi đúng từng path, mỗi path lấy đúng cookie của nó.
    private static readonly (string Url, string Name)[] CookieSources =
    [
        ("https://api.primexbt.com/v2/fws/", "fws_token"),
        ("https://api.primexbt.com/v2/pws/", "ws_token"),
        ("https://api.primexbt.com/v2/bws/", "bws_token"),
        ("https://api.primexbt.com/v2/auth/refresh", "refresh_token")
    ];

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _polling;

    public PrimeXbtLoginWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) => _poll.Stop();
        _poll.Tick += OnPollTick;
    }

    public PrimeXbtSession? CapturedSession { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Profile riêng của app: phiên đăng nhập PrimeXBT được giữ giữa các lần mở cửa sổ.
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TradeDesktop",
                "webview2-primexbt");
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Navigate(SignInUrl);
            StatusText.Text = "Hãy đăng nhập tài khoản PrimeXBT trong khung bên dưới. Cửa sổ tự đóng khi đã lấy được phiên.";
            _poll.Start();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            StatusText.Text = "Máy chưa có Microsoft Edge WebView2 Runtime — cài từ https://developer.microsoft.com/microsoft-edge/webview2/ rồi mở lại.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Không khởi động được WebView2: " + ex.Message;
        }
    }

    private async void OnPollTick(object? sender, EventArgs e)
    {
        if (_polling || Browser.CoreWebView2 is null)
        {
            return;
        }

        _polling = true;
        try
        {
            if (!Browser.CoreWebView2.Source.StartsWith("https://primexbt.com", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var raw = await Browser.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('prm-token')");
            var token = ReadJsonString(raw)?.Trim().Trim('"');
            if (!PrimeXbtJwt.LooksLikeJwt(token))
            {
                return;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (url, name) in CookieSources)
            {
                var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(url);
                var match = cookies.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
                if (match is not null && !string.IsNullOrEmpty(match.Value))
                {
                    map[name] = match.Value;
                }
            }

            var session = new PrimeXbtSession(token!, map, DateTime.UtcNow);
            if (!session.HasRequiredCookies)
            {
                StatusText.Text = "Đã thấy token, đang chờ cookie phiên của api.primexbt.com…";
                return;
            }

            CapturedSession = session;
            _poll.Stop();
            DialogResult = true;
        }
        catch
        {
            // Trang đang chuyển hướng / script chưa chạy được: thử lại ở nhịp sau.
        }
        finally
        {
            _polling = false;
        }
    }

    // ExecuteScriptAsync trả kết quả dạng JSON: "null" hoặc một chuỗi JSON.
    private static string? ReadJsonString(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null")
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(json);
        }
        catch
        {
            return null;
        }
    }
}
