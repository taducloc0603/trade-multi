using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Infrastructure.TradeLocker;

public sealed record TradeLockerTokens(string AccessToken, string RefreshToken);

public sealed record TradeLockerAccountJwt(string Jwt, DateTime ExpiresUtc);

// HTTP tới Keycloak (auth.tradelocker.com) và kerber (api.tradelocker.com), đúng như scan\trade-locker src/auth đã
// kiểm chứng ở Phase 0. Lỗi ⇒ trả null kèm lý do ngắn (không token), caller fail-closed. Không bao giờ log token.
public sealed class TradeLockerAuthClient
{
    public const string KeycloakTokenUrl = "https://auth.tradelocker.com/realms/tradelocker/protocol/openid-connect/token";
    public const string KeycloakClientId = "frontend-app";
    public const string KerberApi = "https://api.tradelocker.com/kerber-api";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;

    public TradeLockerAuthClient()
        : this(new HttpClient())
    {
    }

    // Cho test: HttpClient với handler giả.
    public TradeLockerAuthClient(HttpClient http)
    {
        _http = http;
    }

    public string? LastError { get; private set; }

    // true khi lỗi cuối là lỗi mạng/tạm thời (timeout, không kết nối được, HTTP 5xx/429) — KHÔNG phải vấn đề đăng nhập.
    // Kiểm 2026-10-26 (tắt Wi‑Fi): lỗi mạng từng bị báo nhầm là "cần đăng nhập".
    public bool LastErrorIsTransient { get; private set; }

    // grant_type=refresh_token. Keycloak có thể xoay refresh token; không có trong response ⇒ giữ token cũ.
    public async Task<TradeLockerTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Fail<TradeLockerTokens>("refresh token trống");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, KeycloakTokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = KeycloakClientId,
                    ["refresh_token"] = refreshToken,
                })
            };
            using var doc = await SendJsonAsync(request, "keycloak refresh", cancellationToken).ConfigureAwait(false);
            if (doc is null)
            {
                return null;
            }

            var access = ReadString(doc.RootElement, "access_token");
            if (!PrimeXbtJwt.LooksLikeJwt(access))
            {
                return Fail<TradeLockerTokens>("keycloak refresh: không có access_token");
            }

            var rotated = ReadString(doc.RootElement, "refresh_token");
            LastError = null;
            return new TradeLockerTokens(access!, string.IsNullOrWhiteSpace(rotated) ? refreshToken : rotated);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return Fail<TradeLockerTokens>($"keycloak refresh: {ex.GetType().Name}", transient: true);
        }
    }

    // GET v1/accounts — bắt buộc header X-ODA-Brand (thiếu ⇒ 400, Phase 0).
    public async Task<IReadOnlyList<TradeLockerAccount>?> ListAccountsAsync(string accessToken, string brand, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{KerberApi}/v1/accounts");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("X-ODA-Brand", brand);
            using var doc = await SendJsonAsync(request, "kerber accounts", cancellationToken).ConfigureAwait(false);
            if (doc is null)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return Fail<IReadOnlyList<TradeLockerAccount>>("kerber accounts: thiếu mảng data");
            }

            var list = new List<TradeLockerAccount>();
            foreach (var a in data.EnumerateArray())
            {
                var cluster = a.TryGetProperty("environmentInfo", out var env) && env.ValueKind == JsonValueKind.Object
                    ? ReadString(env, "cluster") ?? string.Empty
                    : string.Empty;
                list.Add(new TradeLockerAccount(
                    ReadString(a, "id") ?? string.Empty,
                    ReadString(a, "accId") ?? string.Empty,
                    ReadString(a, "accName") ?? string.Empty,
                    ReadString(a, "type") ?? string.Empty,
                    ReadString(a, "engine") ?? string.Empty,
                    ReadString(a, "status") ?? string.Empty,
                    cluster));
            }

            LastError = null;
            return list;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return Fail<IReadOnlyList<TradeLockerAccount>>($"kerber accounts: {ex.GetType().Name}", transient: true);
        }
    }

    // POST v1/auth/accounts/jwt {"accountId":"<accId số>"} ⇒ JWT ~5'. Hạn lấy từ expireDate, thiếu thì từ claim exp.
    public async Task<TradeLockerAccountJwt?> MintAccountJwtAsync(string accessToken, string accId, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{KerberApi}/v1/auth/accounts/jwt")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { accountId = accId }), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var doc = await SendJsonAsync(request, "kerber jwt", cancellationToken).ConfigureAwait(false);
            if (doc is null)
            {
                return null;
            }

            var body = doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                ? data
                : doc.RootElement;
            string? jwt = null;
            foreach (var p in body.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } s && s.StartsWith("eyJ", StringComparison.Ordinal))
                {
                    jwt = s;
                    break;
                }
            }

            if (jwt is null)
            {
                return Fail<TradeLockerAccountJwt>("kerber jwt: response không có token");
            }

            DateTime? expires = ReadString(body, "expireDate") is { } expireText &&
                                DateTimeOffset.TryParse(expireText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.UtcDateTime
                : PrimeXbtJwt.ReadExpiryUtc(jwt);
            if (expires is null)
            {
                return Fail<TradeLockerAccountJwt>("kerber jwt: không đọc được hạn");
            }

            LastError = null;
            return new TradeLockerAccountJwt(jwt, expires.Value);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return Fail<TradeLockerAccountJwt>($"kerber jwt: {ex.GetType().Name}", transient: true);
        }
    }

    private async Task<JsonDocument?> SendJsonAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            LastError = $"{what}: HTTP {code}";
            LastErrorIsTransient = code >= 500 || code == 429;
            return null;
        }

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
    }

    private T? Fail<T>(string reason, bool transient = false) where T : class
    {
        LastError = reason;
        LastErrorIsTransient = transient;
        return null;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsTransient(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException;
}
