using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TradeDesktop.Application.Services.PrimeXbt;

// Lõi giao thức WebSocket `fws` của PrimeXBT (docs/plans/primexbt Phase 3). Thuần — không I/O; test bằng fixture thật.
// Envelope: client gửi {type:SUBSCRIPTION|REQUEST, rid, action, body}; server trả {type:RESPONSE, action, body, rid, sid}
// hoặc đẩy {type:EVENT, action, body, sid, aid}. Lỗi có HAI dạng (Phase 0 Q4):
//   nghiệp vụ  — body:{id:null, error:"TOO_LOW_AMOUNT"}           (vẫn có body)
//   tham số    — error:{code:"WRONG_ARGS", description:"…"}       (KHÔNG có body)

public enum PrimeXbtFrameType
{
    Unknown = 0,
    Response,
    Event
}

public enum PrimeXbtSide
{
    Buy,
    Sell
}

public sealed record PrimeXbtError(string Code, string Description, bool IsArgumentError);

public sealed record PrimeXbtFrame(
    PrimeXbtFrameType Type,
    string Action,
    int? Rid,
    int? Sid,
    long? Aid,
    JsonElement? Body,
    PrimeXbtError? Error);

public static class PrimeXbtEnvelope
{
    public const string TypeSubscription = "SUBSCRIPTION";
    public const string TypeRequest = "REQUEST";

    public static string Build(string type, int rid, string action, JsonObject? body)
    {
        if (type is not (TypeSubscription or TypeRequest))
        {
            throw new ArgumentException($"type phải là {TypeSubscription} hoặc {TypeRequest}", nameof(type));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        var frame = new JsonObject
        {
            ["type"] = type,
            ["rid"] = rid,
            ["action"] = action,
            ["body"] = body ?? new JsonObject()
        };
        return frame.ToJsonString();
    }

    // Không throw: frame hỏng ⇒ false. Body được Clone để tách khỏi JsonDocument.
    public static bool TryParse(string? text, out PrimeXbtFrame frame)
    {
        frame = new PrimeXbtFrame(PrimeXbtFrameType.Unknown, string.Empty, null, null, null, null, null);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var typeText = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var type = typeText switch
            {
                "RESPONSE" => PrimeXbtFrameType.Response,
                "EVENT" => PrimeXbtFrameType.Event,
                _ => PrimeXbtFrameType.Unknown
            };
            var action = root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? string.Empty : string.Empty;
            JsonElement? body = root.TryGetProperty("body", out var b) && b.ValueKind != JsonValueKind.Null ? b.Clone() : null;

            frame = new PrimeXbtFrame(
                type,
                action,
                ReadInt(root, "rid"),
                ReadInt(root, "sid"),
                root.TryGetProperty("aid", out var aid) && aid.TryGetInt64(out var aidValue) ? aidValue : null,
                body,
                ReadError(root, body));
            return type != PrimeXbtFrameType.Unknown && action.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int? ReadInt(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : null;

    private static PrimeXbtError? ReadError(JsonElement root, JsonElement? body)
    {
        if (root.TryGetProperty("error", out var top) && top.ValueKind == JsonValueKind.Object)
        {
            var code = top.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "UNKNOWN" : "UNKNOWN";
            var description = top.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? string.Empty : string.Empty;
            return new PrimeXbtError(code, description, IsArgumentError: true);
        }

        if (body is { ValueKind: JsonValueKind.Object } b &&
            b.TryGetProperty("error", out var businessError) &&
            businessError.ValueKind == JsonValueKind.String)
        {
            var code = businessError.GetString() ?? "UNKNOWN";
            return new PrimeXbtError(code, PrimeXbtErrorMapper.Describe(code), IsArgumentError: false);
        }

        return null;
    }
}

public static class PrimeXbtErrorMapper
{
    // Mã đã thấy thật ở Phase 0 (fixtures/trading.json → rejections_phase0). Mã lạ ⇒ trả nguyên mã, không đoán.
    public static string Describe(string? code) => code switch
    {
        "TOO_LOW_AMOUNT" => "Khối lượng nhỏ hơn mức tối thiểu của symbol",
        "WRONG_ORDER_AMOUNT" => "Khối lượng không đúng bước (orderStep)",
        "WRONG_ARGS" => "Tham số lệnh không hợp lệ",
        "POSITION_NOT_FOUND" => "Không tìm thấy vị thế",
        null or "" => "Lỗi không rõ",
        _ => code
    };
}

internal static class PrimeXbtJson
{
    public static decimal? ReadDecimal(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var v) ? v : null;

    public static string? ReadString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    public static long? ReadLong(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : null;

    public static DateTimeOffset? ReadTime(JsonElement parent, string name)
        => ReadString(parent, name) is { } s &&
           DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var v)
            ? v
            : null;

    public static PrimeXbtSide? ReadSide(JsonElement parent, string name) => ReadString(parent, name) switch
    {
        "BUY" => PrimeXbtSide.Buy,
        "SELL" => PrimeXbtSide.Sell,
        _ => null
    };
}
