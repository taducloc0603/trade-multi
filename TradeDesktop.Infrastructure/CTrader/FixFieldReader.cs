using System.Globalization;
using QuickFix;

namespace TradeDesktop.Infrastructure.CTrader;

// Đọc field an toàn trên FieldMap (Message hoặc Group). cTrader drop im lặng message sai định dạng và
// dictionary có thể chưa parse group khi message dựng từ chuỗi thô — nên mọi truy cập đều kiểu Try, không throw.
internal static class FixFieldReader
{
    public static string MsgType(Message message)
        => message.Header.IsSetField(35) ? message.Header.GetString(35) : string.Empty;

    public static string? String(FieldMap map, int tag)
        => map.IsSetField(tag) ? map.GetString(tag) : null;

    public static int? Int(FieldMap map, int tag)
        => map.IsSetField(tag) && int.TryParse(map.GetString(tag), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    public static long? Long(FieldMap map, int tag)
        => map.IsSetField(tag) && long.TryParse(map.GetString(tag), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    public static decimal? Decimal(FieldMap map, int tag)
        => map.IsSetField(tag) && decimal.TryParse(map.GetString(tag), NumberStyles.Number, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    public static char? Char(FieldMap map, int tag)
    {
        var s = String(map, tag);
        return string.IsNullOrEmpty(s) ? null : s[0];
    }

    // UTCTimestamp "yyyyMMdd-HH:mm:ss[.fff]" → epoch ms. Sai định dạng → 0 (R10: không có thì để 0).
    public static ulong UtcTimestampMs(FieldMap map, int tag)
    {
        var s = String(map, tag);
        if (string.IsNullOrEmpty(s))
        {
            return 0;
        }

        string[] formats = ["yyyyMMdd-HH:mm:ss.fff", "yyyyMMdd-HH:mm:ss", "yyyyMMdd-HH:mm:ss.ffffff"];
        return DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
            ? (ulong)new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeMilliseconds()
            : 0;
    }

    // Duyệt repeating group theo số đếm; message dựng từ chuỗi thô không có dictionary thì không có group → rỗng.
    public static IEnumerable<Group> Groups(Message message, int countTag)
    {
        var count = Int(message, countTag) ?? 0;
        for (var i = 1; i <= count; i++)
        {
            Group? group;
            try
            {
                group = message.GetGroup(i, countTag);
            }
            catch (FieldNotFoundException)
            {
                yield break;
            }

            if (group is not null)
            {
                yield return group;
            }
        }
    }
}
