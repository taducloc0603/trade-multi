using System.Text.Json;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Infrastructure.PrimeXbt.Browser;

namespace TradeDesktop.Infrastructure.TradeLocker.Browser;

public sealed record TradeLockerCdpReadResult(TradeLockerSession? Session, string? Reason)
{
    public const string NoTradeLockerTab = "NO_TRADELOCKER_TAB";
    public const string NoToken = "NO_TOKEN";

    public static TradeLockerCdpReadResult Fail(string reason) => new(null, reason);
}

// Đọc phiên đăng nhập từ tab app.tradelocker.com qua CDP (Chrome profile riêng, docs/plans/tradelocker Phase 2).
// Web (oidc-client) giữ token Keycloak trong sessionStorage dưới key cố định — đúng như scan\trade-locker login.ts.
// Chỉ ĐỌC; dùng lại các helper CDP chung (ChromeDevToolsClient/Http) của PrimeXBT mà không sửa chúng.
public static class TradeLockerCdpSessionReader
{
    public const string AppOrigin = "https://app.tradelocker.com";
    public const string SessionStorageKey = "tlx-oidc:user:https://auth.tradelocker.com/realms/tradelocker:frontend-app";

    public static async Task<TradeLockerCdpReadResult> TryReadAsync(int port, CancellationToken cancellationToken)
    {
        var targetsJson = await ChromeDevToolsHttp.GetTargetsJsonAsync(port, cancellationToken).ConfigureAwait(false);
        var tabs = SelectTradeLockerTabs(targetsJson);
        if (tabs.Count == 0)
        {
            return TradeLockerCdpReadResult.Fail(TradeLockerCdpReadResult.NoTradeLockerTab);
        }

        // sessionStorage là theo tab: thử lần lượt các tab app.tradelocker.com, lấy tab đầu tiên có token.
        foreach (var tab in tabs)
        {
            string? raw;
            await using (var page = await ChromeDevToolsClient.ConnectAsync(tab.WebSocketUrl, cancellationToken).ConfigureAwait(false))
            {
                var evaluated = await page.SendAsync(
                    "Runtime.evaluate",
                    new { expression = $"sessionStorage.getItem('{SessionStorageKey}')", returnByValue = true },
                    cancellationToken).ConfigureAwait(false);
                raw = ReadEvaluateString(evaluated);
            }

            if (ParseOidcUser(raw, DateTime.UtcNow) is { } session)
            {
                return new TradeLockerCdpReadResult(session, null);
            }
        }

        return TradeLockerCdpReadResult.Fail(TradeLockerCdpReadResult.NoToken);
    }

    public sealed record CdpTab(string Id, string Url, string WebSocketUrl);

    // /json/list: Chrome liệt kê tab dùng gần nhất trước.
    public static IReadOnlyList<CdpTab> SelectTradeLockerTabs(string targetsJson)
    {
        var tabs = new List<CdpTab>();
        try
        {
            using var doc = JsonDocument.Parse(targetsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return tabs;
            }

            foreach (var target in doc.RootElement.EnumerateArray())
            {
                var type = GetString(target, "type");
                var url = GetString(target, "url");
                var id = GetString(target, "id");
                var ws = GetString(target, "webSocketDebuggerUrl");
                if (type == "page" && IsTradeLockerUrl(url) && !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(ws))
                {
                    tabs.Add(new CdpTab(id!, url!, ws!));
                }
            }
        }
        catch (JsonException)
        {
        }

        return tabs;
    }

    public static bool IsTradeLockerUrl(string? url)
        => url is not null
           && (url.Equals(AppOrigin, StringComparison.OrdinalIgnoreCase)
               || url.StartsWith(AppOrigin + "/", StringComparison.OrdinalIgnoreCase));

    // Giá trị sessionStorage là JSON của oidc-client: { access_token, refresh_token, expires_at, ... }.
    // Không có refresh token đọc được hạn ⇒ null (chưa đăng nhập xong hoặc dạng lạ).
    public static TradeLockerSession? ParseOidcUser(string? raw, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var refresh = GetString(doc.RootElement, "refresh_token");
            var access = GetString(doc.RootElement, "access_token") ?? string.Empty;
            if (!PrimeXbtJwt.LooksLikeJwt(refresh))
            {
                return null;
            }

            return new TradeLockerSession(refresh!, access, nowUtc);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // `result` của Runtime.evaluate(returnByValue): { result: { type: "string", value: "..." } } hoặc null object.
    private static string? ReadEvaluateString(JsonElement evaluateResult)
        => evaluateResult.ValueKind == JsonValueKind.Object
           && evaluateResult.TryGetProperty("result", out var remote)
           && remote.ValueKind == JsonValueKind.Object
           && remote.TryGetProperty("value", out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
