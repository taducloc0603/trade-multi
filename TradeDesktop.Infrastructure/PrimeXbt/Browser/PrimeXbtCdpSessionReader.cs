using System.Text.Json;
using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.Infrastructure.PrimeXbt.Browser;

public sealed record PrimeXbtCdpReadResult(PrimeXbtSession? Session, string? Reason)
{
    public const string NoPrimeXbtTab = "NO_PRIMEXBT_TAB";
    public const string NoJwt = "NO_JWT";
    public const string MissingCookies = "MISSING_COOKIES";

    public static PrimeXbtCdpReadResult Fail(string reason) => new(null, reason);
}

// Đọc phiên PrimeXBT từ Chrome (profile riêng của app) qua cổng CDP — tương đương PrimeXbtLoginWindow nhưng trên Chrome
// thật: JWT = localStorage["prm-token"] của tab primexbt.com; cookie httpOnly của api.primexbt.com qua Storage.getCookies
// (cấp browser, trả mọi path nên không vướng chuyện cookie đặt theo path như CookieManager của WebView2).
// CHỈ ĐỌC: không điều hướng, không click, không mở tab. KHÔNG log giá trị token/cookie.
public static class PrimeXbtCdpSessionReader
{
    public const string PrimeXbtOrigin = "https://primexbt.com";
    public const string ApiCookieDomain = "api.primexbt.com";

    // Cùng 4 cookie như PrimeXbtLoginWindow.CookieSources; path ưu tiên khi một tên có ở nhiều path.
    public static readonly IReadOnlyDictionary<string, string> CookiePaths = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["fws_token"] = "/v2/fws",
        ["ws_token"] = "/v2/pws",
        ["bws_token"] = "/v2/bws",
        ["refresh_token"] = "/v2/auth/refresh"
    };

    public static async Task<PrimeXbtCdpReadResult> TryReadAsync(int port, string browserWebSocketUrl, CancellationToken cancellationToken)
    {
        var targetsJson = await ChromeDevToolsHttp.GetTargetsJsonAsync(port, cancellationToken).ConfigureAwait(false);
        var tab = SelectPrimeXbtTab(targetsJson);
        if (tab is null)
        {
            return PrimeXbtCdpReadResult.Fail(PrimeXbtCdpReadResult.NoPrimeXbtTab);
        }

        string? jwt;
        await using (var page = await ChromeDevToolsClient.ConnectAsync(tab.WebSocketUrl, cancellationToken).ConfigureAwait(false))
        {
            var evaluated = await page.SendAsync(
                "Runtime.evaluate",
                new { expression = "localStorage.getItem('prm-token')", returnByValue = true },
                cancellationToken).ConfigureAwait(false);
            jwt = ReadEvaluateString(evaluated)?.Trim().Trim('"');
        }

        if (!PrimeXbtJwt.LooksLikeJwt(jwt))
        {
            return PrimeXbtCdpReadResult.Fail(PrimeXbtCdpReadResult.NoJwt);
        }

        JsonElement cookiesResult;
        await using (var browser = await ChromeDevToolsClient.ConnectAsync(browserWebSocketUrl, cancellationToken).ConfigureAwait(false))
        {
            cookiesResult = await browser.SendAsync("Storage.getCookies", null, cancellationToken).ConfigureAwait(false);
        }

        var session = new PrimeXbtSession(jwt!, ExtractApiCookies(cookiesResult), DateTime.UtcNow);
        return session.HasRequiredCookies
            ? new PrimeXbtCdpReadResult(session, null)
            : PrimeXbtCdpReadResult.Fail(PrimeXbtCdpReadResult.MissingCookies);
    }

    public sealed record CdpTab(string Id, string Url, string WebSocketUrl);

    // /json/list: Chrome liệt kê tab dùng gần nhất trước ⇒ lấy tab page primexbt.com đầu tiên.
    public static CdpTab? SelectPrimeXbtTab(string targetsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(targetsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var target in doc.RootElement.EnumerateArray())
            {
                var type = GetString(target, "type");
                var url = GetString(target, "url");
                var id = GetString(target, "id");
                var ws = GetString(target, "webSocketDebuggerUrl");
                if (type == "page" && IsPrimeXbtUrl(url) && !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(ws))
                {
                    return new CdpTab(id!, url!, ws!);
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    // Tab bất kỳ của primexbt.com (kể cả chưa có websocket) — dùng để quyết định activate hay mở tab mới.
    public static string? FindPrimeXbtTabId(string targetsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(targetsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return doc.RootElement.EnumerateArray()
                .Where(t => GetString(t, "type") == "page" && IsPrimeXbtUrl(GetString(t, "url")))
                .Select(t => GetString(t, "id"))
                .FirstOrDefault(id => !string.IsNullOrEmpty(id));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsPrimeXbtUrl(string? url)
        => url is not null
           && (url.Equals(PrimeXbtOrigin, StringComparison.OrdinalIgnoreCase)
               || url.StartsWith(PrimeXbtOrigin + "/", StringComparison.OrdinalIgnoreCase));

    // `result` của Storage.getCookies: { cookies: [ { name, value, domain, path, ... } ] }.
    public static IReadOnlyDictionary<string, string> ExtractApiCookies(JsonElement storageGetCookiesResult)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var exactPath = new HashSet<string>(StringComparer.Ordinal);
        if (storageGetCookiesResult.ValueKind != JsonValueKind.Object
            || !storageGetCookiesResult.TryGetProperty("cookies", out var cookies)
            || cookies.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (var cookie in cookies.EnumerateArray())
        {
            var name = GetString(cookie, "name");
            var value = GetString(cookie, "value");
            var domain = GetString(cookie, "domain")?.TrimStart('.');
            if (name is null || string.IsNullOrEmpty(value)
                || !CookiePaths.TryGetValue(name, out var preferredPath)
                || !string.Equals(domain, ApiCookieDomain, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isPreferredPath = string.Equals(GetString(cookie, "path")?.TrimEnd('/'), preferredPath, StringComparison.Ordinal);
            if (!map.ContainsKey(name) || (isPreferredPath && !exactPath.Contains(name)))
            {
                map[name] = value;
                if (isPreferredPath)
                {
                    exactPath.Add(name);
                }
            }
        }

        return map;
    }

    // `result` của Runtime.evaluate(returnByValue): { result: { type: "string", value: "..." } } hoặc null object.
    public static string? ReadEvaluateString(JsonElement evaluateResult)
    {
        if (evaluateResult.ValueKind == JsonValueKind.Object
            && evaluateResult.TryGetProperty("result", out var remote)
            && remote.ValueKind == JsonValueKind.Object
            && remote.TryGetProperty("value", out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
