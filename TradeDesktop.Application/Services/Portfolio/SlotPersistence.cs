using System.Text.Json;
using System.Text.Json.Serialization;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.Portfolio;

/// <summary>
/// Phase 5: JSON serialization helpers for slot persistence.
/// Used by SupabaseConfigRepository (Infrastructure layer) to save/load `current_slots` JSONB column.
/// Kept in Application layer so business model stays pure.
/// </summary>
public static class SlotPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string Serialize(IEnumerable<PositionSlot> liveSlots)
    {
        var dtos = liveSlots
            .Where(s => s.Status == PositionSlotStatus.Live)
            .Where(s => s.TicketA.HasValue && s.TicketB.HasValue)
            .Where(s => s.OpenConfirmedAtUtc.HasValue)
            .Select(s => new SlotPersistenceDto
            {
                SlotId = s.SlotId,
                PairId = s.PairId,
                Side = s.Side.ToString(),
                OpenMode = s.OpenMode.ToString(),
                TicketA = s.TicketA!.Value,
                TicketB = s.TicketB!.Value,
                OpenConfirmedAtUtc = s.OpenConfirmedAtUtc!.Value,
                HoldingSeconds = s.HoldingSeconds,
            })
            .ToList();

        return JsonSerializer.Serialize(dtos, JsonOptions);
    }

    public static IReadOnlyList<RecoveredSlotData> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<RecoveredSlotData>();
        }

        try
        {
            var dtos = JsonSerializer.Deserialize<List<SlotPersistenceDto>>(json, JsonOptions)
                       ?? new List<SlotPersistenceDto>();

            return dtos
                .Select(d => new RecoveredSlotData(
                    SlotId: d.SlotId,
                    PairId: d.PairId,
                    Side: Enum.TryParse<TradingPositionSide>(d.Side, ignoreCase: true, out var side)
                        ? side : TradingPositionSide.None,
                    OpenMode: Enum.TryParse<TradingOpenMode>(d.OpenMode, ignoreCase: true, out var mode)
                        ? mode : TradingOpenMode.None,
                    TicketA: d.TicketA,
                    TicketB: d.TicketB,
                    OpenConfirmedAtUtc: d.OpenConfirmedAtUtc,
                    HoldingSeconds: d.HoldingSeconds))
                .Where(r => r.Side != TradingPositionSide.None && r.OpenMode != TradingOpenMode.None)
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<RecoveredSlotData>();
        }
    }
}

internal sealed class SlotPersistenceDto
{
    [JsonPropertyName("slotId")]
    public int SlotId { get; set; }

    [JsonPropertyName("pairId")]
    public string PairId { get; set; } = string.Empty;

    [JsonPropertyName("side")]
    public string Side { get; set; } = string.Empty;

    [JsonPropertyName("openMode")]
    public string OpenMode { get; set; } = string.Empty;

    [JsonPropertyName("ticketA")]
    public ulong TicketA { get; set; }

    [JsonPropertyName("ticketB")]
    public ulong TicketB { get; set; }

    [JsonPropertyName("openConfirmedAtUtc")]
    public DateTime OpenConfirmedAtUtc { get; set; }

    [JsonPropertyName("holdingSeconds")]
    public int HoldingSeconds { get; set; }
}
