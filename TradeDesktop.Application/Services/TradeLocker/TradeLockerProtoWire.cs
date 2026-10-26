using System.Text;

namespace TradeDesktop.Application.Services.TradeLocker;

// Codec protobuf TỐI THIỂU cho các message hermes app dùng (quyết định D4, docs/plans/tradelocker): không thêm package,
// không sinh code. Chỉ hỗ trợ wire type 0/1/2/5 — đủ cho mọi field string/enum/message/repeated của schema đã trích.
// Decode KHÔNG throw: buffer hỏng ⇒ TryParse trả false (caller fail-closed, ghi [DRIFT]).
public sealed class ProtoMessage
{
    private readonly Dictionary<int, List<ProtoField>> _fields;

    private ProtoMessage(Dictionary<int, List<ProtoField>> fields)
    {
        _fields = fields;
    }

    public static ProtoMessage Empty { get; } = new([]);

    public IEnumerable<int> FieldNumbers => _fields.Keys;

    public static bool TryParse(ReadOnlyMemory<byte> buffer, out ProtoMessage message)
    {
        var fields = new Dictionary<int, List<ProtoField>>();
        var span = buffer.Span;
        var pos = 0;
        try
        {
            while (pos < span.Length)
            {
                var key = ReadVarint(span, ref pos);
                var number = (int)(key >> 3);
                var wire = (int)(key & 7);
                if (number <= 0)
                {
                    message = Empty;
                    return false;
                }

                ProtoField field;
                switch (wire)
                {
                    case 0:
                        field = new ProtoField(number, wire, ReadVarint(span, ref pos), ReadOnlyMemory<byte>.Empty);
                        break;
                    case 1:
                        field = new ProtoField(number, wire, 0, Slice(buffer, ref pos, 8));
                        break;
                    case 2:
                        var len = ReadVarint(span, ref pos);
                        if (len > int.MaxValue)
                        {
                            message = Empty;
                            return false;
                        }

                        field = new ProtoField(number, wire, 0, Slice(buffer, ref pos, (int)len));
                        break;
                    case 5:
                        field = new ProtoField(number, wire, 0, Slice(buffer, ref pos, 4));
                        break;
                    default:
                        message = Empty;
                        return false;
                }

                if (!fields.TryGetValue(number, out var list))
                {
                    fields[number] = list = [];
                }

                list.Add(field);
            }
        }
        catch (IndexOutOfRangeException)
        {
            message = Empty;
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            message = Empty;
            return false;
        }

        message = new ProtoMessage(fields);
        return true;
    }

    public bool Has(int number) => _fields.ContainsKey(number);

    // Proto3: field vắng mặt = giá trị mặc định ("" / 0). Lần xuất hiện cuối thắng (đúng quy tắc protobuf cho scalar).
    public string String(int number)
        => _fields.TryGetValue(number, out var list) && list[^1].WireType == 2
            ? Encoding.UTF8.GetString(list[^1].Bytes.Span)
            : string.Empty;

    public ulong Varint(int number)
        => _fields.TryGetValue(number, out var list) && list[^1].WireType == 0 ? list[^1].Varint : 0UL;

    public int Int(int number) => unchecked((int)Varint(number));

    public IEnumerable<string> Strings(int number)
        => _fields.TryGetValue(number, out var list)
            ? list.Where(f => f.WireType == 2).Select(f => Encoding.UTF8.GetString(f.Bytes.Span))
            : [];

    // Message lồng nhau hỏng ⇒ bỏ qua phần tử đó (caller đếm/ghi drift nếu cần).
    public IEnumerable<ProtoMessage> Messages(int number)
    {
        if (!_fields.TryGetValue(number, out var list))
        {
            yield break;
        }

        foreach (var f in list)
        {
            if (f.WireType == 2 && TryParse(f.Bytes, out var nested))
            {
                yield return nested;
            }
        }
    }

    public ProtoMessage? Message(int number)
        => _fields.TryGetValue(number, out var list) && list[^1].WireType == 2 && TryParse(list[^1].Bytes, out var nested)
            ? nested
            : null;

    private static ReadOnlyMemory<byte> Slice(ReadOnlyMemory<byte> buffer, ref int pos, int length)
    {
        if (length < 0 || pos + length > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var slice = buffer.Slice(pos, length);
        pos += length;
        return slice;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> span, ref int pos)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = span[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(span), "varint quá dài");
    }
}

public readonly record struct ProtoField(int Number, int WireType, ulong Varint, ReadOnlyMemory<byte> Bytes);

// Ghi theo đúng thứ tự gọi; proto3 bỏ field mặc định (0 / "") — giống bộ mã hoá của web (frame BUY khớp từng byte).
public sealed class ProtoWriter
{
    private readonly MemoryStream _stream = new();

    public ProtoWriter Varint(int number, ulong value)
    {
        if (value == 0)
        {
            return this;
        }

        WriteVarint((ulong)(number << 3));
        WriteVarint(value);
        return this;
    }

    public ProtoWriter String(int number, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return this;
        }

        return Bytes(number, Encoding.UTF8.GetBytes(value));
    }

    public ProtoWriter Message(int number, ProtoWriter nested) => Bytes(number, nested.ToArray(), writeEmpty: true);

    public byte[] ToArray() => _stream.ToArray();

    private ProtoWriter Bytes(int number, byte[] bytes, bool writeEmpty = false)
    {
        if (bytes.Length == 0 && !writeEmpty)
        {
            return this;
        }

        WriteVarint((ulong)((number << 3) | 2));
        WriteVarint((ulong)bytes.Length);
        _stream.Write(bytes);
        return this;
    }

    private void WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        _stream.WriteByte((byte)value);
    }
}
