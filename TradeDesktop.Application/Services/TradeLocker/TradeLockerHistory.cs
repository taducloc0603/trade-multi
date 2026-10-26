using System.Globalization;
using System.Text.Json;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.TradeLocker;

public sealed record TradeLockerClosedPosition(
    string PositionId,
    string InstrumentName,
    TradeLockerSide PositionSide,
    decimal ClosedAmount,
    decimal AverageOpenPrice,
    decimal ClosePrice,
    long OpenTimeMs,
    long CloseTimeMs,
    decimal Commission,
    decimal Swap,
    decimal Pnl);

public sealed record TradeLockerHistoryPage(IReadOnlyList<TradeLockerClosedPosition> Items, string? Next);

// REST hera `reports/closed-positions-history` (Bearer = account JWT): {data:[…], links:{next}}.
// Một position có thể có nhiều dòng (đóng từng phần) ⇒ Project gộp theo positionId. App chỉ đóng toàn phần nên thường 1 dòng.
public static class TradeLockerHistoryProjector
{
    public static TradeLockerHistoryPage ParsePage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var items = new List<TradeLockerClosedPosition>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in data.EnumerateArray())
                {
                    if (TryParseRow(row) is { } parsed)
                    {
                        items.Add(parsed);
                    }
                }
            }

            // Thực tế (2026-10-26): links.next = {"cursorId":"…"} (object), không phải URL; vẫn nhận dạng chuỗi cho chắc.
            string? next = null;
            if (root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object
                && links.TryGetProperty("next", out var n))
            {
                next = n.ValueKind switch
                {
                    JsonValueKind.String => n.GetString(),
                    JsonValueKind.Object when n.TryGetProperty("cursorId", out var cursor) => ReadStringValue(cursor),
                    _ => null
                };
            }

            return new TradeLockerHistoryPage(items, string.IsNullOrWhiteSpace(next) ? null : next);
        }
        catch (JsonException)
        {
            return new TradeLockerHistoryPage([], null);
        }
    }

    // Profit giữ là pnl của sàn (tiền); Commission tách riêng — HistoryProfitCalculator.CalculateMoney cộng hai cột.
    // Swap không có cột riêng trong HistorySharedRecord nên cộng vào Profit (đúng nghĩa tiền thực nhận).
    public static IReadOnlyList<HistorySharedRecord> Project(IEnumerable<TradeLockerClosedPosition> rows)
        => rows
            .GroupBy(r => r.PositionId, StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First();
                var volume = g.Sum(r => r.ClosedAmount);
                var closePrice = volume > 0m ? g.Sum(r => r.ClosePrice * r.ClosedAmount) / volume : first.ClosePrice;
                return TradeLockerTicketCodec.TryEncode(g.Key, out var ticket)
                    ? new HistorySharedRecord(
                        Ticket: ticket,
                        TradeType: first.PositionSide == TradeLockerSide.Buy ? 0 : 1,
                        Volume: (double)volume,
                        OpenPrice: (double)first.AverageOpenPrice,
                        ClosePrice: (double)closePrice,
                        Sl: 0,
                        Tp: 0,
                        Commission: (double)g.Sum(r => r.Commission),
                        Profit: (double)g.Sum(r => r.Pnl + r.Swap),
                        OpenTimeMsc: (ulong)Math.Max(0, g.Min(r => r.OpenTimeMs)),
                        CloseTimeMsc: (ulong)Math.Max(0, g.Max(r => r.CloseTimeMs)),
                        CloseEaTimeLocal: 0,
                        Symbol: first.InstrumentName)
                    : null;
            })
            .Where(r => r is not null)
            .Select(r => r!)
            .OrderBy(r => r.CloseTimeMsc)
            .ToList();

    private static TradeLockerClosedPosition? TryParseRow(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var positionId = ReadString(row, "positionId");
        var side = (ReadString(row, "positionSide") ?? string.Empty).ToUpperInvariant() switch
        {
            "BUY" => TradeLockerSide.Buy,
            "SELL" => TradeLockerSide.Sell,
            _ => TradeLockerSide.Unspecified
        };
        var amount = ReadDecimal(row, "closedAmount");
        var close = ReadDecimal(row, "closePrice");
        if (string.IsNullOrEmpty(positionId) || side == TradeLockerSide.Unspecified || amount is not > 0m || close is not > 0m)
        {
            return null; // thiếu trường bắt buộc ⇒ bỏ dòng, không đoán
        }

        return new TradeLockerClosedPosition(
            positionId,
            ReadString(row, "instrumentName") ?? string.Empty,
            side,
            amount.Value,
            ReadDecimal(row, "averageOpenPrice") ?? 0m,
            close.Value,
            ReadTimeMs(row, "openDateTime"),
            ReadTimeMs(row, "closeDateTime"),
            ReadDecimal(row, "commission") ?? 0m,
            ReadDecimal(row, "swap") ?? 0m,
            ReadDecimal(row, "pnl") ?? 0m);
    }

    private static string? ReadString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? ReadStringValue(v) : null;

    private static string? ReadStringValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null
    };

    private static decimal? ReadDecimal(JsonElement e, string name)
        => TradeLockerMessages.TryDecimal(ReadString(e, name), out var d) ? d : null;

    // Fixture thật (2026-10-26) là ISO-8601 ("2026-10-26T04:29:44.219Z"); vẫn nhận epoch ms cho chắc.
    private static long ReadTimeMs(JsonElement e, string name)
    {
        var text = ReadString(e, name);
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
        {
            return ms;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToUnixTimeMilliseconds()
            : 0L;
    }
}
