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

    public static string Serialize(IEnumerable<PositionSlot> liveSlots, RandomQuotaState? randomQuota = null)
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
                HwndProfileIndex = s.HwndProfileIndex,
                ChartHwndA = s.ChartHwndA,
                TradeHwndA = s.TradeHwndA,
                ChartHwndB = s.ChartHwndB,
                TradeHwndB = s.TradeHwndB,
            })
            .ToList();

        if (randomQuota is null)
        {
            return JsonSerializer.Serialize(dtos, JsonOptions);
        }

        return JsonSerializer.Serialize(new PortfolioPersistenceDto
        {
            Slots = dtos,
            RandomQuota = randomQuota
        }, JsonOptions);
    }

    public static IReadOnlyList<RecoveredSlotData> Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<RecoveredSlotData>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var dtos = document.RootElement.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<SlotPersistenceDto>>(json, JsonOptions) ?? []
                : JsonSerializer.Deserialize<PortfolioPersistenceDto>(json, JsonOptions)?.Slots ?? [];

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
                    HoldingSeconds: d.HoldingSeconds,
                    HwndProfileIndex: d.HwndProfileIndex,
                    ChartHwndA: d.ChartHwndA,
                    TradeHwndA: d.TradeHwndA,
                    ChartHwndB: d.ChartHwndB,
                    TradeHwndB: d.TradeHwndB))
                .Where(r => r.Side != TradingPositionSide.None && r.OpenMode != TradingOpenMode.None)
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<RecoveredSlotData>();
        }
    }

    public static RandomQuotaState? DeserializeRandomQuota(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return JsonSerializer.Deserialize<PortfolioPersistenceDto>(json, JsonOptions)?.RandomQuota;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed class PortfolioPersistenceDto
{
    [JsonPropertyName("slots")]
    public List<SlotPersistenceDto> Slots { get; set; } = [];

    [JsonPropertyName("randomQuota")]
    public RandomQuotaState? RandomQuota { get; set; }
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

    [JsonPropertyName("hwndProfileIndex")]
    public int? HwndProfileIndex { get; set; }

    [JsonPropertyName("chartHwndA")]
    public string ChartHwndA { get; set; } = string.Empty;

    [JsonPropertyName("tradeHwndA")]
    public string TradeHwndA { get; set; } = string.Empty;

    [JsonPropertyName("chartHwndB")]
    public string ChartHwndB { get; set; } = string.Empty;

    [JsonPropertyName("tradeHwndB")]
    public string TradeHwndB { get; set; } = string.Empty;
}
