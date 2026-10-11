using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TradeDesktop.Infrastructure.PrimeXbt.Browser;

// Client CDP tối thiểu (không thêm package): một websocket, gửi `{id, method, params}` rồi chờ đúng response theo id.
// Event CDP bị bỏ qua. KHÔNG gửi header Origin (ClientWebSocket mặc định không gửi) ⇒ hợp lệ với Chrome 111+ mà không
// cần `--remote-allow-origins` — cờ đó sẽ cho phép trang web mở websocket vào cổng debug, cấm dùng.
public sealed class ChromeDevToolsClient : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly ClientWebSocket _socket = new();
    private int _nextId;

    private ChromeDevToolsClient()
    {
    }

    public static async Task<ChromeDevToolsClient> ConnectAsync(string webSocketUrl, CancellationToken cancellationToken)
    {
        var client = new ChromeDevToolsClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DefaultTimeout);
            await client._socket.ConnectAsync(new Uri(webSocketUrl), timeout.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // Trả `result` của response; CDP trả `error` ⇒ InvalidOperationException (chỉ message lỗi CDP, không có dữ liệu phiên).
    public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);
        await _socket.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);

        while (true)
        {
            var message = await ReceiveMessageAsync(timeout.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idElement) || idElement.GetInt32() != id)
            {
                continue; // event hoặc response của lệnh khác
            }

            if (root.TryGetProperty("error", out var error))
            {
                var text = error.TryGetProperty("message", out var m) ? m.GetString() : "unknown";
                throw new InvalidOperationException($"CDP {method} error: {text}");
            }

            return root.TryGetProperty("result", out var result) ? result.Clone() : default;
        }
    }

    private async Task<byte[]> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var received = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("CDP socket closed");
            }

            stream.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
            {
                return stream.ToArray();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Đóng best-effort.
        }
        finally
        {
            _socket.Dispose();
        }
    }
}

// Endpoint HTTP của cổng debug (chỉ 127.0.0.1): /json/version, /json/list, /json/activate, /json/new.
public static class ChromeDevToolsHttp
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private static string BaseUrl(int port) => $"http://127.0.0.1:{port}";

    // null ⇒ cổng không trả lời (Chrome đã tắt, file DevToolsActivePort sót lại).
    public static async Task<string?> TryGetBrowserWebSocketUrlAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            var json = await Http.GetStringAsync($"{BaseUrl(port)}/json/version", cancellationToken).ConfigureAwait(false);
            return JsonNode.Parse(json)?["webSocketDebuggerUrl"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string> GetTargetsJsonAsync(int port, CancellationToken cancellationToken)
        => await Http.GetStringAsync($"{BaseUrl(port)}/json/list", cancellationToken).ConfigureAwait(false);

    public static async Task ActivateTargetAsync(int port, string targetId, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync($"{BaseUrl(port)}/json/activate/{Uri.EscapeDataString(targetId)}", cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // Chrome 111+ bắt buộc PUT cho /json/new.
    public static async Task OpenNewTabAsync(int port, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{BaseUrl(port)}/json/new?{url}");
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
