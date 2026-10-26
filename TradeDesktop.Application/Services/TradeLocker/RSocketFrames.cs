using System.Buffers.Binary;
using System.Text;

namespace TradeDesktop.Application.Services.TradeLocker;

// RSocket 1.0 trên WebSocket (quyết định D5): mỗi WS binary message là đúng MỘT frame, KHÔNG có tiền tố độ dài —
// đúng như rsocket-js 1.0.0-alpha.3 mà web TradeLocker dùng. Chỉ các loại frame app cần.
public static class RSocketFrameType
{
    public const int Setup = 0x01;
    public const int KeepAlive = 0x03;
    public const int RequestResponse = 0x04;
    public const int RequestStream = 0x06;
    public const int RequestN = 0x08;
    public const int Cancel = 0x09;
    public const int Payload = 0x0A;
    public const int Error = 0x0B;
}

public static class RSocketFlags
{
    public const int Metadata = 0x100;
    public const int Follows = 0x80;
    public const int Respond = 0x80; // KEEPALIVE dùng cùng bit với FOLLOWS
    public const int Complete = 0x40;
    public const int Next = 0x20;
}

public sealed record RSocketFrame(
    int StreamId,
    int Type,
    int Flags,
    ReadOnlyMemory<byte> Metadata,
    ReadOnlyMemory<byte> Data,
    uint ErrorCode = 0,
    string ErrorMessage = "")
{
    public bool Has(int flag) => (Flags & flag) != 0;
}

public static class RSocketFrameCodec
{
    public const string MetadataMimeType = "message/x.rsocket.composite-metadata.v0";
    public const string DataMimeType = "application/x-protobuf";
    private const byte WellKnownAuthentication = 0x7C;
    private const byte WellKnownRouting = 0x7E;
    private const byte BearerAuthType = 0x01;

    // Frame hỏng/thiếu byte ⇒ false (caller đóng kết nối, không đoán).
    public static bool TryDecode(ReadOnlyMemory<byte> frame, out RSocketFrame decoded)
    {
        decoded = new RSocketFrame(0, 0, 0, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
        if (frame.Length < 6)
        {
            return false;
        }

        var span = frame.Span;
        var streamId = BinaryPrimitives.ReadInt32BigEndian(span) & 0x7FFFFFFF;
        var typeAndFlags = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        var type = typeAndFlags >> 10;
        var flags = typeAndFlags & 0x3FF;
        var body = frame[6..];

        switch (type)
        {
            case RSocketFrameType.Payload:
            {
                var metadata = ReadOnlyMemory<byte>.Empty;
                if ((flags & RSocketFlags.Metadata) != 0)
                {
                    if (body.Length < 3)
                    {
                        return false;
                    }

                    var len = body.Span[0] << 16 | body.Span[1] << 8 | body.Span[2];
                    if (body.Length < 3 + len)
                    {
                        return false;
                    }

                    metadata = body.Slice(3, len);
                    body = body[(3 + len)..];
                }

                decoded = new RSocketFrame(streamId, type, flags, metadata, body);
                return true;
            }
            case RSocketFrameType.Error:
                if (body.Length < 4)
                {
                    return false;
                }

                decoded = new RSocketFrame(streamId, type, flags, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty,
                    BinaryPrimitives.ReadUInt32BigEndian(body.Span), Encoding.UTF8.GetString(body.Span[4..]));
                return true;
            case RSocketFrameType.KeepAlive:
                if (body.Length < 8)
                {
                    return false;
                }

                decoded = new RSocketFrame(streamId, type, flags, ReadOnlyMemory<byte>.Empty, body[8..]);
                return true;
            case RSocketFrameType.RequestN:
                if (body.Length < 4)
                {
                    return false;
                }

                decoded = new RSocketFrame(streamId, type, flags, ReadOnlyMemory<byte>.Empty, body[..4]);
                return true;
            default:
                // Loại frame khác (LEASE, METADATA_PUSH, CANCEL từ server...): giữ thân thô, caller tự quyết bỏ qua.
                decoded = new RSocketFrame(streamId, type, flags, ReadOnlyMemory<byte>.Empty, body);
                return true;
        }
    }

    public static byte[] EncodeSetup(string bearerJwt, int keepAliveMs, int lifetimeMs)
    {
        var token = Encoding.UTF8.GetBytes(bearerJwt);
        var auth = new byte[1 + token.Length];
        auth[0] = 0x80 | BearerAuthType;
        token.CopyTo(auth, 1);
        var metadata = CompositeEntry(WellKnownAuthentication, auth);

        var w = new FrameWriter(0, RSocketFrameType.Setup, RSocketFlags.Metadata);
        w.U16(1);
        w.U16(0);
        w.U32((uint)keepAliveMs);
        w.U32((uint)lifetimeMs);
        w.ShortString(MetadataMimeType);
        w.ShortString(DataMimeType);
        w.U24(metadata.Length);
        w.Bytes(metadata);
        return w.ToArray();
    }

    public static byte[] EncodeRequestStream(int streamId, string route, uint initialRequestN, ReadOnlySpan<byte> data = default)
    {
        var metadata = RouteMetadata(route);
        var w = new FrameWriter(streamId, RSocketFrameType.RequestStream, RSocketFlags.Metadata);
        w.U32(initialRequestN);
        w.U24(metadata.Length);
        w.Bytes(metadata);
        w.Bytes(data);
        return w.ToArray();
    }

    public static byte[] EncodeRequestResponse(int streamId, string route, ReadOnlySpan<byte> data = default)
    {
        var metadata = RouteMetadata(route);
        var w = new FrameWriter(streamId, RSocketFrameType.RequestResponse, RSocketFlags.Metadata);
        w.U24(metadata.Length);
        w.Bytes(metadata);
        w.Bytes(data);
        return w.ToArray();
    }

    public static byte[] EncodeRequestN(int streamId, uint n)
    {
        var w = new FrameWriter(streamId, RSocketFrameType.RequestN, 0);
        w.U32(n);
        return w.ToArray();
    }

    public static byte[] EncodeCancel(int streamId) => new FrameWriter(streamId, RSocketFrameType.Cancel, 0).ToArray();

    public static byte[] EncodeKeepAlive(bool respond, ReadOnlySpan<byte> data = default)
    {
        var w = new FrameWriter(0, RSocketFrameType.KeepAlive, respond ? RSocketFlags.Respond : 0);
        w.U64(0);
        w.Bytes(data);
        return w.ToArray();
    }

    // Composite metadata, well-known "message/x.rsocket.routing.v0": tag có tiền tố độ dài 1 byte.
    public static byte[] RouteMetadata(string route)
    {
        var bytes = Encoding.UTF8.GetBytes(route);
        if (bytes.Length > 255)
        {
            throw new ArgumentException("route quá dài cho một routing tag", nameof(route));
        }

        var tag = new byte[1 + bytes.Length];
        tag[0] = (byte)bytes.Length;
        bytes.CopyTo(tag, 1);
        return CompositeEntry(WellKnownRouting, tag);
    }

    private static byte[] CompositeEntry(byte wellKnownId, byte[] content)
    {
        var e = new byte[4 + content.Length];
        e[0] = (byte)(0x80 | wellKnownId);
        e[1] = (byte)(content.Length >> 16);
        e[2] = (byte)(content.Length >> 8);
        e[3] = (byte)content.Length;
        content.CopyTo(e, 4);
        return e;
    }

    private sealed class FrameWriter
    {
        private readonly MemoryStream _stream = new();

        public FrameWriter(int streamId, int type, int flags)
        {
            Span<byte> header = stackalloc byte[6];
            BinaryPrimitives.WriteInt32BigEndian(header, streamId);
            BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)((type << 10) | (flags & 0x3FF)));
            _stream.Write(header);
        }

        public void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); _stream.Write(b); }
        public void U24(int v) { _stream.WriteByte((byte)(v >> 16)); _stream.WriteByte((byte)(v >> 8)); _stream.WriteByte((byte)v); }
        public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); _stream.Write(b); }
        public void U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); _stream.Write(b); }
        public void Bytes(ReadOnlySpan<byte> b) => _stream.Write(b);

        public void ShortString(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            _stream.WriteByte((byte)b.Length);
            _stream.Write(b);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}

// Ghép PAYLOAD có cờ FOLLOWS theo stream. Phase 0 chưa thấy server fragment (gói 28 KB vẫn một frame) — giữ để phòng xa.
// Không thread-safe: dùng trong vòng nhận đơn luồng của connection.
public sealed class RSocketFragmentAssembler
{
    private readonly Dictionary<int, MemoryStream> _pending = [];

    // Trả data đầy đủ khi frame cuối (không FOLLOWS) tới; null khi còn chờ mảnh tiếp.
    public ReadOnlyMemory<byte>? Feed(RSocketFrame frame)
    {
        if (frame.Has(RSocketFlags.Follows))
        {
            if (!_pending.TryGetValue(frame.StreamId, out var buffer))
            {
                _pending[frame.StreamId] = buffer = new MemoryStream();
            }

            buffer.Write(frame.Data.Span);
            return null;
        }

        if (_pending.Remove(frame.StreamId, out var head))
        {
            head.Write(frame.Data.Span);
            return head.ToArray();
        }

        return frame.Data;
    }

    public void Reset(int streamId) => _pending.Remove(streamId);

    public int PendingStreams => _pending.Count;
}
