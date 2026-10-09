using System.Net.Http.Headers;
using System.Text.Json;
using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.Infrastructure.PrimeXbt;

// Làm mới JWT ngoài trình duyệt (Phase 0 Q2, PASS 2/2): GET /v2/auth/refresh với cookie refresh_token
// (path /v2/auth/refresh) ⇒ {access_token, expires_in}. Cookie KHÔNG xoay. Không cần auth-guard.
// Lỗi bất kỳ ⇒ null (caller fail-closed, nhắc đăng nhập lại). Không bao giờ log token.
public static class PrimeXbtAuthClient
{
    private const string RefreshUrl = "https://api.primexbt.com/v2/auth/refresh";
    private static readonly HttpClient Http = CreateClient();

    public static async Task<PrimeXbtSession?> RefreshAsync(PrimeXbtSession current, CancellationToken cancellationToken)
    {
        if (!current.ApiCookies.TryGetValue(PrimeXbtSession.RefreshCookie, out var refreshToken) ||
            string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, RefreshUrl);
            request.Headers.TryAddWithoutValidation("Cookie", $"{PrimeXbtSession.RefreshCookie}={refreshToken}");
            request.Headers.TryAddWithoutValidation("Origin", "https://primexbt.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://primexbt.com/");
            request.Headers.TryAddWithoutValidation("x-client-time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            var jwt = doc.RootElement.TryGetProperty("access_token", out var token) && token.ValueKind == JsonValueKind.String
                ? token.GetString()
                : null;
            if (!PrimeXbtJwt.LooksLikeJwt(jwt))
            {
                return null;
            }

            var cookies = new Dictionary<string, string>(current.ApiCookies, StringComparer.Ordinal);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var header in setCookies)
                {
                    var pair = header.Split(';', 2)[0];
                    var eq = pair.IndexOf('=');
                    if (eq > 0)
                    {
                        var name = pair[..eq].Trim();
                        var value = pair[(eq + 1)..].Trim();
                        if (cookies.ContainsKey(name) && value.Length > 0)
                        {
                            cookies[name] = value;
                        }
                    }
                }
            }

            return new PrimeXbtSession(jwt!, cookies, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        // UseCookies=false: tự đặt header Cookie đúng refresh_token (container mặc định sẽ lọc theo path).
        var handler = new HttpClientHandler { UseCookies = false };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36");
        return client;
    }
}
