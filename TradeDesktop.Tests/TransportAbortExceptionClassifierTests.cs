using System.Net.Sockets;
using TradeDesktop.Application.Helpers;

namespace TradeDesktop.Tests;

// Unobserved task exception của socket FIX (QuickFIX/n 1.10 bỏ rơi BeginRead khi ngắt kết nối) chỉ được
// nuốt khi CHẮC CHẮN là abort/reset của transport; mọi lỗi khác vẫn phải hiện hộp thoại.
public sealed class TransportAbortExceptionClassifierTests
{
    private static IOException ReadAborted(SocketError code)
        => new("Unable to read data from the transport connection: The I/O operation has been aborted because of either a thread exit or an application request..",
            new SocketException((int)code));

    [Theory]
    [InlineData(SocketError.OperationAborted)]
    [InlineData(SocketError.ConnectionAborted)]
    [InlineData(SocketError.ConnectionReset)]
    public void IoExceptionWithAbortSocketCode_IsBenign(SocketError code)
    {
        Assert.True(TransportAbortExceptionClassifier.IsBenignTransportAbort(new AggregateException(ReadAborted(code))));
    }

    [Fact]
    public void ObjectDisposed_IsBenign()
    {
        var ex = new AggregateException(new ObjectDisposedException("SslStream"));

        Assert.True(TransportAbortExceptionClassifier.IsBenignTransportAbort(ex));
    }

    [Fact]
    public void NestedAggregate_IsFlattened()
    {
        var ex = new AggregateException(new AggregateException(ReadAborted(SocketError.OperationAborted)));

        Assert.True(TransportAbortExceptionClassifier.IsBenignTransportAbort(ex));
    }

    [Fact]
    public void IoExceptionWithoutSocketInner_IsNotBenign()
    {
        var ex = new AggregateException(new IOException("disk full"));

        Assert.False(TransportAbortExceptionClassifier.IsBenignTransportAbort(ex));
    }

    [Fact]
    public void OtherSocketError_IsNotBenign()
    {
        var ex = new AggregateException(ReadAborted(SocketError.HostUnreachable));

        Assert.False(TransportAbortExceptionClassifier.IsBenignTransportAbort(ex));
    }

    [Fact]
    public void UnrelatedException_IsNotBenign()
    {
        var ex = new AggregateException(new InvalidOperationException("bug"));

        Assert.False(TransportAbortExceptionClassifier.IsBenignTransportAbort(ex));
    }

    [Fact]
    public void MixedBenignAndRealError_IsNotBenign()
    {
        var ex = new AggregateException(ReadAborted(SocketError.OperationAborted), new NullReferenceException());

        Assert.False(TransportAbortExceptionClassifier.IsBenignTransportAbort(ex));
    }

    [Fact]
    public void NullOrEmpty_IsNotBenign()
    {
        Assert.False(TransportAbortExceptionClassifier.IsBenignTransportAbort(null));
        Assert.False(TransportAbortExceptionClassifier.IsBenignTransportAbort(new AggregateException()));
    }
}
