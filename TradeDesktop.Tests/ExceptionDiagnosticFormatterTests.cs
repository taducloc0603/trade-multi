using System.Net.Sockets;
using TradeDesktop.Application.Helpers;

namespace TradeDesktop.Tests;

// Chuỗi chẩn đoán đưa vào hộp thoại lỗi để chỉ cần ẢNH CHỤP là đủ xác định nguồn lỗi.
public sealed class ExceptionDiagnosticFormatterTests
{
    private static Exception Thrown(Exception ex)
    {
        try
        {
            throw ex;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    [Fact]
    public void UnobservedSslAbort_ShowsTypeChainAndSocketCode()
    {
        var io = Thrown(new IOException("Unable to read data from the transport connection",
            new SocketException((int)SocketError.OperationAborted)));

        var text = ExceptionDiagnosticFormatter.Describe(new AggregateException(io));

        Assert.Contains("Loại: IOException → SocketException", text, StringComparison.Ordinal);
        Assert.Contains("Socket: OperationAborted (995)", text, StringComparison.Ordinal);
        Assert.Contains("Stack:", text, StringComparison.Ordinal);
        Assert.Contains(nameof(Thrown), text, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainException_NoSocketLine_AndNoStackWhenNotThrown()
    {
        var text = ExceptionDiagnosticFormatter.Describe(new InvalidOperationException("bug"));

        Assert.Contains("Loại: InvalidOperationException", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Socket:", text, StringComparison.Ordinal);
        Assert.Contains("Stack: (không có)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StackFrames_AreCappedAtMaxFrames()
    {
        var ex = Thrown(new InvalidOperationException("bug"));

        var text = ExceptionDiagnosticFormatter.Describe(ex, maxFrames: 1);
        var frameLines = text.Split('\n').SkipWhile(l => l != "Stack:").Skip(1).ToList();

        Assert.Single(frameLines);
    }

    [Fact]
    public void EmptyAggregate_DescribesAggregateItself()
    {
        var text = ExceptionDiagnosticFormatter.Describe(new AggregateException());

        Assert.Contains("Loại: AggregateException", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ExceptionDiagnosticFormatter.Describe(null!));
    }
}
