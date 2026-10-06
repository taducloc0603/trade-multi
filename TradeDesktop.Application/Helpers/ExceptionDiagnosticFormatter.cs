using System.Net.Sockets;

namespace TradeDesktop.Application.Helpers;

// Khách thường chỉ gửi được ẢNH CHỤP hộp thoại lỗi, không gửi được startup.log. Chuỗi này đưa vào hộp
// thoại đủ dữ kiện để xác định nguồn lỗi chỉ từ ảnh: chuỗi loại exception gốc, socket code, vài frame đầu.
public static class ExceptionDiagnosticFormatter
{
    public const int DefaultMaxFrames = 3;

    public static string Describe(Exception exception, int maxFrames = DefaultMaxFrames)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var root = exception is AggregateException aggregate
            && aggregate.Flatten().InnerExceptions is { Count: > 0 } inners
                ? inners[0]
                : exception;

        var chain = new List<string>();
        SocketError? socketCode = null;
        for (var current = root; current is not null; current = current.InnerException)
        {
            chain.Add(current.GetType().Name);
            if (current is SocketException socket)
            {
                socketCode ??= socket.SocketErrorCode;
            }
        }

        var lines = new List<string> { $"Loại: {string.Join(" → ", chain)}" };
        if (socketCode is { } code)
        {
            lines.Add($"Socket: {code} ({(int)code})");
        }

        var frames = (root.StackTrace ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(Math.Max(0, maxFrames))
            .ToList();
        lines.Add(frames.Count == 0 ? "Stack: (không có)" : "Stack:\n" + string.Join("\n", frames));

        return string.Join("\n", lines);
    }
}
