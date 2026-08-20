using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeDesktop.Infrastructure.Mt5Bridge;

public static class Mt5BridgeProtocol
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false
    };

    public static string Serialize(Mt5BridgeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Version != Version)
        {
            throw new ArgumentException($"Unsupported MT5 Bridge protocol version {message.Version}.", nameof(message));
        }

        // Serialize using the runtime type; callers commonly hold commands through
        // the Mt5BridgeMessage base type and must not lose derived fields.
        var json = JsonSerializer.Serialize(message, message.GetType(), SerializerOptions);
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(json);
        if (byteCount > Mt5BridgeLayout.MaxPayloadSize)
        {
            throw new InvalidOperationException(
                $"MT5 Bridge message is {byteCount} UTF-8 bytes; maximum is {Mt5BridgeLayout.MaxPayloadSize}.");
        }

        return json;
    }

    public static T? Deserialize<T>(string json)
        where T : Mt5BridgeMessage
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<T>(json, SerializerOptions);
    }

    public static Mt5BridgeInboundMessage DeserializeInbound(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var message = JsonSerializer.Deserialize<Mt5BridgeInboundMessage>(json, SerializerOptions)
            ?? throw new JsonException("MT5 Bridge returned an empty message.");
        if (message.Version != Version || string.IsNullOrWhiteSpace(message.Type))
        {
            throw new JsonException("MT5 Bridge message has an invalid version or type.");
        }

        return message;
    }
}

public abstract record Mt5BridgeMessage
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = Mt5BridgeProtocol.Version;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
}

public sealed record Mt5BridgeOpenCommand : Mt5BridgeMessage
{
    public Mt5BridgeOpenCommand() => Type = "open";

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("account")]
    public required long Account { get; init; }

    [JsonPropertyName("symbol")]
    public required string Symbol { get; init; }

    [JsonPropertyName("side")]
    public required string Side { get; init; }

    [JsonPropertyName("volume")]
    public required double Volume { get; init; }

    [JsonPropertyName("created_ms")]
    public required long CreatedMilliseconds { get; init; }

    [JsonPropertyName("expires_ms")]
    public required long ExpiresMilliseconds { get; init; }

    [JsonPropertyName("pair_id")]
    public string? PairId { get; init; }

    [JsonPropertyName("leg")]
    public string? Leg { get; init; }
}

public sealed record Mt5BridgeCloseCommand : Mt5BridgeMessage
{
    public Mt5BridgeCloseCommand() => Type = "close";

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("account")]
    public required long Account { get; init; }

    [JsonPropertyName("ticket")]
    public required ulong Ticket { get; init; }

    [JsonPropertyName("symbol")]
    public required string Symbol { get; init; }

    [JsonPropertyName("volume")]
    public required double Volume { get; init; }

    [JsonPropertyName("created_ms")]
    public required long CreatedMilliseconds { get; init; }

    [JsonPropertyName("expires_ms")]
    public required long ExpiresMilliseconds { get; init; }
}

public sealed record Mt5BridgePing : Mt5BridgeMessage
{
    public Mt5BridgePing() => Type = "ping";

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }
}

public sealed record Mt5BridgeExecutionResult : Mt5BridgeMessage
{
    public Mt5BridgeExecutionResult() => Type = "execution_result";

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("ticket")]
    public ulong? Ticket { get; init; }

    [JsonPropertyName("deal")]
    public ulong? Deal { get; init; }

    [JsonPropertyName("price")]
    public double? Price { get; init; }

    [JsonPropertyName("volume")]
    public double? Volume { get; init; }

    [JsonPropertyName("retcode")]
    public uint? Retcode { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

public sealed record Mt5BridgeInboundMessage : Mt5BridgeMessage
{
    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("account")]
    public long? Account { get; init; }

    [JsonPropertyName("generation")]
    public ulong? Generation { get; init; }

    [JsonPropertyName("ea_ms")]
    public ulong? EaMilliseconds { get; init; }

    [JsonPropertyName("writer_uid")]
    public uint? WriterUid { get; init; }

    [JsonPropertyName("lane")]
    public int? Lane { get; init; }

    [JsonPropertyName("manual_ui_ready")]
    public bool? ManualUiReady { get; init; }

    [JsonPropertyName("manual_ui_code")]
    public string? ManualUiCode { get; init; }

    [JsonPropertyName("chart_symbol")]
    public string? ChartSymbol { get; init; }

    [JsonPropertyName("chart_hwnd")]
    public long? ChartHwnd { get; init; }

    [JsonPropertyName("panel_found")]
    public bool? PanelFound { get; init; }

    [JsonPropertyName("dpi")]
    public int? Dpi { get; init; }

    [JsonPropertyName("coordinate_source")]
    public string? CoordinateSource { get; init; }

    [JsonPropertyName("ticket")]
    public ulong? Ticket { get; init; }

    [JsonPropertyName("deal")]
    public ulong? Deal { get; init; }

    [JsonPropertyName("price")]
    public double? Price { get; init; }

    [JsonPropertyName("volume")]
    public double? Volume { get; init; }

    [JsonPropertyName("retcode")]
    public uint? Retcode { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

public static class Mt5BridgeExecutionStatuses
{
    public const string Received = "received";
    public const string Dispatched = "dispatched";
    public const string Confirmed = "confirmed";
    public const string Rejected = "rejected";
    public const string Invalid = "invalid";
    public const string Expired = "expired";
    public const string Duplicate = "duplicate";
    public const string AlreadyClosed = "already_closed";
    public const string Timeout = "timeout";
    public const string Unknown = "unknown";

    public static bool IsFinal(string? status)
    {
        return status is Confirmed or Rejected or Invalid or Expired
            or AlreadyClosed or Timeout or Unknown;
    }
}
