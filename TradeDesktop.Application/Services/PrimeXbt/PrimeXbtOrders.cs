using System.Text.Json;
using System.Text.Json.Nodes;

namespace TradeDesktop.Application.Services.PrimeXbt;

public sealed record PrimeXbtTradeSettings(decimal MinOrderSize, decimal OrderStep, decimal MaxOrderSize, string LotUnit)
{
    // body của `trade-settings`. Thiếu min/step/max ⇒ null (fail-closed: không lập lệnh khi chưa biết giới hạn).
    public static PrimeXbtTradeSettings? TryParse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var min = PrimeXbtJson.ReadDecimal(body, "minOrderSize");
        var step = PrimeXbtJson.ReadDecimal(body, "orderStep");
        var max = PrimeXbtJson.ReadDecimal(body, "maxOrderSize");
        return min is > 0m && step is > 0m && max is > 0m && max >= min
            ? new PrimeXbtTradeSettings(min.Value, step.Value, max.Value, PrimeXbtJson.ReadString(body, "lotUnit") ?? string.Empty)
            : null;
    }
}

public sealed record PrimeXbtOrderPlan(string Action, JsonObject Body);

public sealed record PrimeXbtPlanResult(PrimeXbtOrderPlan? Plan, string? Error)
{
    public bool IsSuccess => Plan is not null;
}

// Chỉ có HAI loại lệnh app được phép tạo: mở market và đóng ĐÚNG MỘT sub-position theo id.
// CỐ Ý không có API cho route đóng theo DÒNG GỘP (đóng cả hai chiều hedge) hay route ĐÓNG TẤT CẢ
// (Rule F: chỉ đóng đúng pair). Recheck A6 grep chặn nguyên văn hai route đó trong code production.
public static class PrimeXbtOrderPlanner
{
    public const string ActionMarketPlace = "orders/market/place";
    public const string ActionClosePosition = "positions/id/close";

    public static PrimeXbtPlanResult PlanOpen(string symbol, PrimeXbtSide side, decimal qty, PrimeXbtTradeSettings? settings)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return Fail("Thiếu symbol");
        }

        if (QtyError(qty, settings) is { } error)
        {
            return Fail(error);
        }

        return new PrimeXbtPlanResult(
            new PrimeXbtOrderPlan(ActionMarketPlace, new JsonObject
            {
                ["qty"] = qty,
                ["side"] = side == PrimeXbtSide.Buy ? "BUY" : "SELL",
                ["symbol"] = symbol.Trim()
            }),
            null);
    }

    public static PrimeXbtPlanResult PlanClose(long positionId, decimal qty)
    {
        if (positionId <= 0)
        {
            return Fail("positionId không hợp lệ");
        }

        if (qty <= 0m)
        {
            return Fail("qty đóng phải > 0");
        }

        return new PrimeXbtPlanResult(
            new PrimeXbtOrderPlan(ActionClosePosition, new JsonObject { ["positionId"] = positionId, ["qty"] = qty }),
            null);
    }

    private static string? QtyError(decimal qty, PrimeXbtTradeSettings? settings)
    {
        if (settings is null)
        {
            return "Chưa có trade-settings của symbol (min/step/max)";
        }

        if (qty < settings.MinOrderSize)
        {
            return $"qty {qty} < min {settings.MinOrderSize}";
        }

        if (qty > settings.MaxOrderSize)
        {
            return $"qty {qty} > max {settings.MaxOrderSize}";
        }

        return qty % settings.OrderStep == 0m ? null : $"qty {qty} không là bội của step {settings.OrderStep}";
    }

    private static PrimeXbtPlanResult Fail(string error) => new(null, error);
}

public sealed record PrimeXbtOrderReport(
    long Id,
    PrimeXbtSide Side,
    string Status,
    DateTimeOffset? PlacedAt,
    DateTimeOffset? ExecutedAt,
    decimal Qty,
    decimal? ExecutedPrice,
    decimal? Rpl,
    decimal Fee,
    long? PositionId,
    string OpenReason,
    string Symbol)
{
    public bool IsClosePosition => string.Equals(OpenReason, "CLOSE_POSITION", StringComparison.Ordinal);
    public bool IsExecuted => string.Equals(Status, "EXECUTED", StringComparison.Ordinal);
}

public static class PrimeXbtOrderReportParser
{
    // body của `report/orders2`: {data:[…], total}. Bản ghi thiếu id/side/qty bị bỏ (không đoán).
    public static IReadOnlyList<PrimeXbtOrderReport> Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<PrimeXbtOrderReport>();
        foreach (var o in data.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = PrimeXbtJson.ReadLong(o, "id");
            var side = PrimeXbtJson.ReadSide(o, "side");
            var qty = PrimeXbtJson.ReadDecimal(o, "executedQty") ?? PrimeXbtJson.ReadDecimal(o, "qty");
            if (id is not > 0 || side is null || qty is not > 0m)
            {
                continue;
            }

            result.Add(new PrimeXbtOrderReport(
                id.Value,
                side.Value,
                PrimeXbtJson.ReadString(o, "status") ?? string.Empty,
                PrimeXbtJson.ReadTime(o, "placedAt"),
                PrimeXbtJson.ReadTime(o, "executedAt"),
                qty.Value,
                PrimeXbtJson.ReadDecimal(o, "executedPrice"),
                PrimeXbtJson.ReadDecimal(o, "rpl"),
                PrimeXbtJson.ReadDecimal(o, "fee") ?? 0m,
                PrimeXbtJson.ReadLong(o, "positionId"),
                PrimeXbtJson.ReadString(o, "openReason") ?? string.Empty,
                PrimeXbtJson.ReadString(o, "symbol") ?? string.Empty));
        }

        return result;
    }
}

public enum PrimeXbtMatchStatus
{
    Matched,
    NotFound,
    Ambiguous,
    InvalidOrder
}

public sealed record PrimeXbtMatchResult(PrimeXbtMatchStatus Status, long? PositionId, int CandidateCount);

// D3: ack của `orders/market/place` KHÔNG có positionId. Gắn lệnh MỞ đã khớp với sub-position bằng
// openTime == executedAt ∧ openPrice == executedPrice ∧ side ∧ qty ∧ symbol (Phase 0: đúng 20/20).
// Không khớp duy nhất ⇒ KHÔNG đoán (NotFound/Ambiguous), caller fail-closed.
public static class PrimeXbtOrderMatcher
{
    public static PrimeXbtMatchResult Match(PrimeXbtOrderReport order, IEnumerable<PrimeXbtSubPosition> positions)
    {
        if (!order.IsExecuted || order.IsClosePosition || order.ExecutedAt is null || order.ExecutedPrice is null)
        {
            return new PrimeXbtMatchResult(PrimeXbtMatchStatus.InvalidOrder, null, 0);
        }

        var candidates = positions
            .Where(p => p.OpenTime == order.ExecutedAt.Value &&
                        p.OpenPrice == order.ExecutedPrice.Value &&
                        p.Side == order.Side &&
                        p.Qty == order.Qty &&
                        string.Equals(p.Symbol, order.Symbol, StringComparison.Ordinal))
            .ToList();

        return candidates.Count switch
        {
            1 => new PrimeXbtMatchResult(PrimeXbtMatchStatus.Matched, candidates[0].Id, 1),
            0 => new PrimeXbtMatchResult(PrimeXbtMatchStatus.NotFound, null, 0),
            _ => new PrimeXbtMatchResult(PrimeXbtMatchStatus.Ambiguous, null, candidates.Count)
        };
    }
}

public sealed record PrimeXbtClosedTrade(
    long PositionId,
    long CloseOrderId,
    PrimeXbtSide PositionSide,
    decimal Qty,
    decimal? OpenPrice,
    decimal ClosePrice,
    DateTimeOffset CloseTime,
    decimal Profit,
    bool ProfitIsEstimated,
    decimal? BrokerRpl,
    decimal Fee);

public static class PrimeXbtHistoryProjector
{
    // Lệnh ĐÓNG (openReason=CLOSE_POSITION, có positionId) → giao dịch đã đóng. Profit tự tính
    // (close − open) × qty × dấu (USD với XAU/USD) vì `rpl` làm tròn 2 số (P11: −0,001 USD hiện 0).
    // Thiếu giá mở (app khởi động sau khi lệnh đã đóng) ⇒ dùng rpl và đánh dấu ước lượng.
    // Chiều VỊ THẾ ngược chiều lệnh đóng (đóng Buy bằng lệnh Sell).
    public static IReadOnlyList<PrimeXbtClosedTrade> Project(
        IEnumerable<PrimeXbtOrderReport> orders,
        IReadOnlyDictionary<long, decimal> knownOpenPrices)
    {
        var result = new List<PrimeXbtClosedTrade>();
        foreach (var o in orders)
        {
            if (!o.IsExecuted || !o.IsClosePosition || o.PositionId is not > 0 || o.ExecutedPrice is null || o.ExecutedAt is null)
            {
                continue;
            }

            var positionSide = o.Side == PrimeXbtSide.Buy ? PrimeXbtSide.Sell : PrimeXbtSide.Buy;
            var hasOpen = knownOpenPrices.TryGetValue(o.PositionId.Value, out var openPrice);
            decimal profit;
            if (hasOpen)
            {
                var sign = positionSide == PrimeXbtSide.Buy ? 1m : -1m;
                profit = (o.ExecutedPrice.Value - openPrice) * o.Qty * sign;
            }
            else
            {
                profit = o.Rpl ?? 0m;
            }

            result.Add(new PrimeXbtClosedTrade(
                o.PositionId.Value,
                o.Id,
                positionSide,
                o.Qty,
                hasOpen ? openPrice : null,
                o.ExecutedPrice.Value,
                o.ExecutedAt.Value,
                profit,
                ProfitIsEstimated: !hasOpen,
                o.Rpl,
                o.Fee));
        }

        return result;
    }
}
