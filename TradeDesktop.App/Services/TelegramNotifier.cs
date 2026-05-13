using System.Net.Http;
using System.Text;

namespace TradeDesktop.App.Services;

public sealed class TelegramNotifier(HttpClient httpClient, ITradeSessionFileLogger logger) : ITelegramNotifier
{
    // TODO: điền BotToken và ChatId trước khi bật Telegram notify.
    // Hiện đang để rỗng → NotifyAsync sẽ early-return ở guard bên dưới, không gửi gì.
    private const string BotToken = "";
    private const string ChatId = "";
    private const string GroupName = "SCALP_GAP";

    private readonly HttpClient _httpClient = httpClient;
    private readonly ITradeSessionFileLogger _logger = logger;

    public async Task NotifyAsync(
        string eventCode,
        string severity,
        string detail,
        string? pairId = null,
        IReadOnlyDictionary<string, string?>? meta = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(BotToken) || string.IsNullOrWhiteSpace(ChatId))
            {
                return;
            }

            var text = BuildMessage(eventCode, severity, detail, pairId, meta);
            var endpoint = $"https://api.telegram.org/bot{BotToken}/sendMessage";
            var payload = $"chat_id={Uri.EscapeDataString(ChatId)}&text={Uri.EscapeDataString(text)}";

            using var content = new StringContent(payload, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            using var response = await _httpClient.PostAsync(endpoint, content, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                SafeLog($"[NOTIFY][WARN] Telegram send failed: status={(int)response.StatusCode} event={eventCode}");
            }
        }
        catch (Exception ex)
        {
            // fail-safe: tuyệt đối không ảnh hưởng logic trade
            SafeLog($"[NOTIFY][WARN] Telegram send exception: event={eventCode} error={ex.Message}");
        }
    }

    private static string BuildMessage(
        string eventCode,
        string severity,
        string detail,
        string? pairId,
        IReadOnlyDictionary<string, string?>? meta)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{GroupName}] [{severity}] [{eventCode}]");
        sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"Pair: {pairId ?? "N/A"}");
        sb.AppendLine($"Detail: {detail}");

        if (meta is { Count: > 0 })
        {
            var metaText = string.Join(", ", meta.Select(x => $"{x.Key}={x.Value ?? "-"}"));
            sb.AppendLine($"Meta: {metaText}");
        }

        return sb.ToString();
    }

    private void SafeLog(string message)
    {
        try
        {
            _logger.Log(message);
        }
        catch
        {
            // ignore
        }
    }
}
