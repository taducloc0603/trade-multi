using System.Globalization;

namespace TradeDesktop.Application.Services.TradeLocker;

public enum TradeLockerOrderKind
{
    OpenMarket,
    ClosePosition
}

// TradableUnitId/Side/Lots chỉ có ở lệnh mở (để session khớp "position mới" và giữ một Open in-flight mỗi unit + chiều).
public sealed record TradeLockerOrderPlan(
    TradeLockerOrderKind Kind,
    string Route,
    byte[] Data,
    string? PositionId,
    string Quantity,
    string? TradableUnitId = null,
    TradeLockerSide Side = TradeLockerSide.Unspecified,
    decimal Lots = 0m);

public sealed record TradeLockerPlanResult(TradeLockerOrderPlan? Plan, string? Error)
{
    public bool IsSuccess => Plan is not null;
}

// Chỉ có HAI loại lệnh app được phép tạo: mở market và đóng ĐÚNG MỘT position theo id (Rule F).
// CỐ Ý không có API cho route đóng-tất-cả của hermes — recheck A6 grep chặn nguyên văn chuỗi đó trong code production.
public static class TradeLockerOrderPlanner
{
    public const string RouteOpen = "v3.positions:open";

    public static string CloseRoute(string positionId) => $"v3.positions.{positionId}:close";

    public static TradeLockerPlanResult PlanOpen(TradeLockerSymbol? symbol, TradeLockerSide side, decimal lots)
    {
        if (symbol is null)
        {
            return Fail("Chưa có thông tin symbol (tradable unit) từ sàn");
        }

        if (side is not (TradeLockerSide.Buy or TradeLockerSide.Sell))
        {
            return Fail("Chiều lệnh không hợp lệ");
        }

        if (!symbol.IsTradingActive)
        {
            return Fail($"Symbol {symbol.Symbol} không ở trạng thái giao dịch (trading_status={symbol.TradingStatus})");
        }

        if (LotError(lots, symbol) is { } error)
        {
            return Fail(error);
        }

        var quantity = FormatLots(lots);
        return new TradeLockerPlanResult(
            new TradeLockerOrderPlan(
                TradeLockerOrderKind.OpenMarket,
                RouteOpen,
                TradeLockerMessages.EncodeMarketOpenRequest(side, symbol.TradableUnitId, quantity),
                null,
                quantity,
                symbol.TradableUnitId,
                side,
                lots),
            null);
    }

    // Ticket phải là ticket TradeLocker (bit 63); sai ⇒ không lập lệnh (fail-closed, không fallback).
    public static TradeLockerPlanResult PlanClose(ulong ticket)
    {
        if (!TradeLockerTicketCodec.TryDecodeToWireId(ticket, out var positionId))
        {
            return Fail($"Ticket {ticket} không phải ticket TradeLocker");
        }

        return new TradeLockerPlanResult(
            new TradeLockerOrderPlan(TradeLockerOrderKind.ClosePosition, CloseRoute(positionId), [], positionId, string.Empty),
            null);
    }

    // Kiểm trước khi gửi để không tốn round-trip vào QUANTITY_NOT_ON_LOT_STEP_GRID (Phase 0 Q8).
    public static string? LotError(decimal lots, TradeLockerSymbol symbol)
    {
        if (symbol.LotStep <= 0m || symbol.MinimumLot <= 0m || symbol.MaximumLot <= 0m)
        {
            return "Chưa biết lot_step/minimum_lot/maximum_lot của symbol";
        }

        if (lots < symbol.MinimumLot)
        {
            return $"lot {lots} < min {symbol.MinimumLot}";
        }

        if (lots > symbol.MaximumLot)
        {
            return $"lot {lots} > max {symbol.MaximumLot}";
        }

        return lots % symbol.LotStep == 0m ? null : $"lot {lots} không là bội của step {symbol.LotStep}";
    }

    // Web gửi "0.01" (không thừa số 0). decimal ⇒ chuỗi bất biến văn hoá, bỏ số 0 cuối.
    public static string FormatLots(decimal lots) => lots.ToString("0.##########", CultureInfo.InvariantCulture);

    private static TradeLockerPlanResult Fail(string error) => new(null, error);
}

public enum TradeLockerMatchStatus
{
    Matched,
    NotFound,
    Ambiguous
}

public sealed record TradeLockerMatchResult(TradeLockerMatchStatus Status, string? PositionId, int CandidateCount);

// Ack `:open` có correlation_id nhưng order luôn mang last_correlation_id="-1" (Phase 0) ⇒ khớp lệnh mở với position
// MỚI (không có trước khi gửi) cùng tradable unit + chiều + đủ khối lượng. Phase 0 R9: hai lệnh gửi đồng thời làm
// cách khớp "position mới đầu tiên" gán cùng một id cho cả hai ⇒ executor bắt buộc một Open in-flight mỗi symbol, và ở
// đây ≥ 2 ứng viên ⇒ Ambiguous, KHÔNG đoán.
public static class TradeLockerOrderMatcher
{
    public static TradeLockerMatchResult Match(
        IReadOnlySet<string> idsBeforeSend,
        IEnumerable<TradeLockerPositionView> current,
        string tradableUnitId,
        TradeLockerSide side,
        decimal lots)
    {
        var candidates = current
            .Where(p => !idsBeforeSend.Contains(p.Id)
                        && string.Equals(p.TradableUnitId, tradableUnitId, StringComparison.Ordinal)
                        && p.Side == side
                        && p.Quantity > 0m)
            .ToList();

        if (candidates.Count > 1)
        {
            return new TradeLockerMatchResult(TradeLockerMatchStatus.Ambiguous, null, candidates.Count);
        }

        // Một ứng viên nhưng chưa khớp đủ khối lượng (partial fill) ⇒ chưa coi là khớp, chờ snapshot sau.
        return candidates.Count == 1 && candidates[0].Quantity == lots && candidates[0].Status == TradeLockerPositionStatus.Open
            ? new TradeLockerMatchResult(TradeLockerMatchStatus.Matched, candidates[0].Id, 1)
            : new TradeLockerMatchResult(TradeLockerMatchStatus.NotFound, null, candidates.Count);
    }
}

public enum TradeLockerErrorCategory
{
    // Server từ chối hẳn — lệnh CHẮC CHẮN không được thực hiện.
    Rejected,
    // Đóng position không tồn tại: chỉ coi là "đã đóng" khi PositionStore cũng không còn id đó.
    NotFound,
    Unknown
}

// Mã ProtoError đã thấy ở Phase 0 (Q6/Q8). Mã lạ ⇒ Unknown (caller coi như thất bại, không đoán là đã khớp).
public static class TradeLockerErrorMapper
{
    public static TradeLockerErrorCategory Classify(TradeLockerError error) => error.Code switch
    {
        "QUANTITY_NOT_ON_LOT_STEP_GRID" or "INPUT_VALIDATION" => TradeLockerErrorCategory.Rejected,
        "NOT_FOUND" => TradeLockerErrorCategory.NotFound,
        _ => TradeLockerErrorCategory.Unknown
    };

    public static string Describe(TradeLockerError error)
        => string.IsNullOrEmpty(error.Code) ? error.Description : $"[{error.Code}] {error.Description}";
}
