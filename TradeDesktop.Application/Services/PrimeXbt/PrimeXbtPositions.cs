using System.Text.Json;

namespace TradeDesktop.Application.Services.PrimeXbt;

public sealed record PrimeXbtSubPosition(
    long Id,
    PrimeXbtSide Side,
    decimal Qty,
    decimal OpenPrice,
    DateTimeOffset OpenTime,
    decimal? Upl,
    string Symbol);

public sealed record PrimeXbtPositionsSnapshot(
    bool IsValid,
    string PositionMode,
    IReadOnlyList<PrimeXbtSubPosition> Positions,
    string? Error)
{
    public bool IsHedgeMode => string.Equals(PositionMode, "HEDGE", StringComparison.Ordinal);
}

public static class PrimeXbtPositionsParser
{
    // body của `positions` (RESPONSE/EVENT) là SNAPSHOT ĐẦY ĐỦ: data[] gồm các DÒNG GỘP theo symbol (id 0, side/qty
    // ròng — hedge Buy+Sell 0.01 ra qty 0) và vị thế thật nằm trong subPositions[] (P3). KHÔNG BAO GIỜ trả dòng gộp
    // làm vị thế: đọc nhầm sẽ thấy hedge = 0 lot ⇒ coi như đã đóng ⇒ đóng nhầm chân A.
    // Dòng gộp có qty ≠ 0 mà thiếu subPositions ⇒ snapshot không hợp lệ (fail-closed). Chỉ giữ symbol cấu hình.
    public static PrimeXbtPositionsSnapshot Parse(JsonElement body, string symbol)
    {
        if (body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return Invalid("positions body thiếu data[]");
        }

        var mode = PrimeXbtJson.ReadString(body, "positionMode") ?? string.Empty;
        var result = new List<PrimeXbtSubPosition>();
        foreach (var aggregate in data.EnumerateArray())
        {
            if (aggregate.ValueKind != JsonValueKind.Object)
            {
                return Invalid("phần tử data[] không phải object");
            }

            var aggSymbol = PrimeXbtJson.ReadString(aggregate, "symbol") ?? string.Empty;
            var hasSubs = aggregate.TryGetProperty("subPositions", out var subs) && subs.ValueKind == JsonValueKind.Array;
            if (!hasSubs)
            {
                if ((PrimeXbtJson.ReadDecimal(aggregate, "qty") ?? 0m) != 0m)
                {
                    return Invalid($"dòng gộp {aggSymbol} có qty nhưng thiếu subPositions");
                }

                continue;
            }

            foreach (var sub in subs.EnumerateArray())
            {
                if (!TryReadSub(sub, out var position))
                {
                    return Invalid($"sub-position của {aggSymbol} thiếu trường bắt buộc");
                }

                if (string.Equals(position.Symbol, symbol, StringComparison.Ordinal))
                {
                    result.Add(position);
                }
            }
        }

        return new PrimeXbtPositionsSnapshot(true, mode, result, null);
    }

    private static bool TryReadSub(JsonElement sub, out PrimeXbtSubPosition position)
    {
        position = null!;
        if (sub.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var id = PrimeXbtJson.ReadLong(sub, "id");
        var side = PrimeXbtJson.ReadSide(sub, "side");
        var qty = PrimeXbtJson.ReadDecimal(sub, "qty");
        var openPrice = PrimeXbtJson.ReadDecimal(sub, "openPrice");
        var openTime = PrimeXbtJson.ReadTime(sub, "openTime");
        var symbol = PrimeXbtJson.ReadString(sub, "symbol");
        if (id is not > 0 || side is null || qty is not > 0m || openPrice is not > 0m || openTime is null || string.IsNullOrEmpty(symbol))
        {
            return false;
        }

        position = new PrimeXbtSubPosition(id.Value, side.Value, qty.Value, openPrice.Value, openTime.Value,
            PrimeXbtJson.ReadDecimal(sub, "upl"), symbol);
        return true;
    }

    private static PrimeXbtPositionsSnapshot Invalid(string error)
        => new(false, string.Empty, [], error);
}

// R4: ticket chân B lưu trong current_slots / _pairIdByTicket là ulong trần, không kèm sàn. Sub-position id PrimeXBT
// (~10^7) có thể trùng ticket MT ⇒ gắn namespace BIT 61 (3 bit cao = 001). cTrader dùng bit 62 (2 bit cao = 01) nên
// hai codec loại trừ nhau; ticket MT (3 bit cao = 000) không bao giờ bị nhận nhầm.
public static class PrimeXbtTicketCodec
{
    public const ulong Namespace = 0x2000_0000_0000_0000UL;
    private const ulong ReservedHighBits = 0xE000_0000_0000_0000UL;

    public const long MaxPositionId = (long)(Namespace - 1);

    public static ulong Encode(long positionId)
    {
        if (positionId <= 0 || positionId > MaxPositionId)
        {
            throw new ArgumentOutOfRangeException(nameof(positionId), positionId,
                $"positionId phải trong [1, {MaxPositionId}] để không đụng bit namespace.");
        }

        return Namespace | (ulong)positionId;
    }

    public static bool TryDecode(ulong ticket, out long positionId)
    {
        if ((ticket & ReservedHighBits) != Namespace || (ticket & ~ReservedHighBits) == 0)
        {
            positionId = 0;
            return false;
        }

        positionId = (long)(ticket & ~ReservedHighBits);
        return true;
    }

    public static bool IsPrimeXbtTicket(ulong ticket) => TryDecode(ticket, out _);
}
