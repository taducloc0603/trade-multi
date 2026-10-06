using System.Net.Sockets;

namespace TradeDesktop.Application.Helpers;

// QuickFIX/n 1.10 đọc socket bằng BeginRead + WaitOne. Khi phiên FIX bị ngắt (mạng, heartbeat, Logout, Stop),
// thư viện đóng stream lúc lệnh đọc còn treo và không bao giờ gọi EndRead → Task bên dưới lỗi
// "I/O operation has been aborted" (995) mà không ai observe, rồi nổi lên ở finalizer thread.
// Đây là dấu vết của một lần ngắt kết nối đã được QuickFIX tự xử lý, không phải lỗi mới.
public static class TransportAbortExceptionClassifier
{
    // true chỉ khi MỌI inner exception đều là abort/reset/dispose của transport. Trộn với lỗi khác → false.
    public static bool IsBenignTransportAbort(AggregateException? exception)
    {
        if (exception is null)
        {
            return false;
        }

        var inners = exception.Flatten().InnerExceptions;
        return inners.Count > 0 && inners.All(IsTransportAbort);
    }

    private static bool IsTransportAbort(Exception ex)
        => ex switch
        {
            ObjectDisposedException => true,
            IOException { InnerException: SocketException socket } => IsAbortCode(socket.SocketErrorCode),
            SocketException socket => IsAbortCode(socket.SocketErrorCode),
            _ => false
        };

    private static bool IsAbortCode(SocketError code)
        => code is SocketError.OperationAborted
            or SocketError.ConnectionAborted
            or SocketError.ConnectionReset;
}
