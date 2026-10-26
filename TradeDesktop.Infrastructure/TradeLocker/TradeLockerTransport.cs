using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace TradeDesktop.Infrastructure.TradeLocker;

// Kênh WebSocket thô tới wss://api.<cluster>.tradelocker.com/hermes: mỗi binary message là một frame RSocket.
// Không parse giao thức (việc của session/Application). Mọi lỗi mạng ⇒ Disconnected(reason); không phương thức nào
// throw ra caller. DÙNG MỘT LẦN: mỗi kết nối tạo instance mới (factory trong session).
public interface ITradeLockerTransport : IDisposable
{
    event Action? Connected;
    event Action<string>? Disconnected;
    event Action<byte[]>? FrameReceived;

    bool IsConnected { get; }

    // Bất đồng bộ: trả về ngay, kết quả báo qua Connected/Disconnected.
    void Start(Uri uri);

    // Xếp hàng gửi; false nếu chưa kết nối. Không chặn luồng gọi (luồng poll 50 ms).
    bool Send(byte[] frame);

    void Stop();
}

public sealed class ClientWebSocketTradeLockerTransport : ITradeLockerTransport
{
    public const string Origin = "https://app.tradelocker.com";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36";

    private readonly object _sync = new();
    private readonly BlockingCollection<byte[]> _outbox = new(new ConcurrentQueue<byte[]>(), 1000);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private int _disconnectRaised;
    private volatile bool _connected;

    public event Action? Connected;
    public event Action<string>? Disconnected;
    public event Action<byte[]>? FrameReceived;

    public bool IsConnected => _connected;

    public void Start(Uri uri)
    {
        lock (_sync)
        {
            if (_socket is not null)
            {
                return;
            }

            _socket = new ClientWebSocket();
            // Phase 0: hermes nhận kết nối từ Node khi có header Origin của web app.
            _socket.Options.SetRequestHeader("Origin", Origin);
            _socket.Options.SetRequestHeader("User-Agent", UserAgent);
            // RSocket tự có KEEPALIVE 5 s / lifetime 15 s — tắt ping tầng WebSocket để khỏi lẫn hai cơ chế.
            _socket.Options.KeepAliveInterval = TimeSpan.Zero;
            _cts = new CancellationTokenSource();
        }

        _ = Task.Run(() => RunAsync(uri, _socket, _cts.Token));
    }

    private async Task RunAsync(Uri uri, ClientWebSocket socket, CancellationToken ct)
    {
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
            }

            _connected = true;
            SafeInvoke(() => Connected?.Invoke());
            var sender = Task.Run(() => SendLoopAsync(socket, ct), CancellationToken.None);
            await ReceiveLoopAsync(socket, ct).ConfigureAwait(false);
            await Task.WhenAny(sender, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or HttpRequestException or InvalidOperationException)
        {
            RaiseDisconnected(ct.IsCancellationRequested ? "stopped" : ex.GetType().Name + ": " + ex.Message);
            return;
        }

        RaiseDisconnected(socket.CloseStatus is { } status
            ? $"server close {(int)status} {socket.CloseStatusDescription}"
            : "receive ended");
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            var frame = message.ToArray();
            message.SetLength(0);
            SafeInvoke(() => FrameReceived?.Invoke(frame));
        }
    }

    private async Task SendLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        try
        {
            foreach (var frame in _outbox.GetConsumingEnumerable(ct))
            {
                if (socket.State != WebSocketState.Open)
                {
                    return;
                }

                await socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException)
        {
            // Lỗi gửi: vòng nhận sẽ báo Disconnected.
        }
    }

    public bool Send(byte[] frame) => _connected && _outbox.TryAdd(frame);

    public void Stop()
    {
        CancellationTokenSource? cts;
        ClientWebSocket? socket;
        lock (_sync)
        {
            cts = _cts;
            socket = _socket;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            socket?.Abort();
        }
        catch
        {
            // Abort không throw trong thực tế; nuốt cho chắc.
        }

        RaiseDisconnected("stopped");
    }

    private void RaiseDisconnected(string reason)
    {
        _connected = false;
        if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0)
        {
            SafeInvoke(() => Disconnected?.Invoke(reason));
        }
    }

    private static void SafeInvoke(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Lỗi của handler không được làm chết vòng mạng.
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_sync)
        {
            _socket?.Dispose();
            _cts?.Dispose();
        }

        _outbox.Dispose();
    }
}
