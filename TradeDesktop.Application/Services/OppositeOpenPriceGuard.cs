using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public static class OppositeOpenPriceGuard
{
    public static OppositeOpenPriceGuardResult Evaluate(
        IReadOnlyList<TradeSharedRecord> exchangeATrades,
        IReadOnlySet<ulong> eligibleTickets,
        int requestedTradeType,
        decimal? currentBidA,
        decimal? currentAskA,
        int pointMultiplier,
        int requiredDistancePts)
    {
        if (requiredDistancePts <= 0)
        {
            return OppositeOpenPriceGuardResult.Skip("GUARD_DISABLED", "Điều kiện khoảng cách đảo chiều đang tắt");
        }

        var active = exchangeATrades
            .Where(x => eligibleTickets.Contains(x.Ticket) && x.Price > 0d && x.TradeType is 0 or 1)
            .ToList();
        if (active.Count == 0)
        {
            return OppositeOpenPriceGuardResult.Skip("FIRST_OPEN", "Lệnh đầu tiên, không kiểm tra khoảng cách đảo chiều");
        }

        var latest = active.OrderByDescending(x => x.TimeMsc).ThenByDescending(x => x.OpenEaTimeLocal).First();
        if (latest.TradeType == requestedTradeType)
        {
            return OppositeOpenPriceGuardResult.Skip(
                "SAME_SIDE_OPEN",
                "Mở cùng chiều với lệnh gần nhất, không áp dụng kiểm tra đảo chiều",
                latest.TradeType);
        }

        var sameSide = active.Where(x => x.TradeType == latest.TradeType).ToList();
        if (sameSide.Count == 0)
        {
            return OppositeOpenPriceGuardResult.Block(
                "OPEN_PRICE_A_MISSING",
                "Không có giá mở hợp lệ trên sàn A để tính giá trung bình",
                latest.TradeType);
        }

        var average = sameSide.Average(x => (decimal)x.Price);
        decimal? currentPrice = requestedTradeType == 1 ? currentBidA : currentAskA;
        if (!currentPrice.HasValue || currentPrice.Value <= 0m)
        {
            return OppositeOpenPriceGuardResult.Block(
                "CURRENT_PRICE_A_MISSING",
                "Không có giá hiện tại của sàn A để kiểm tra",
                latest.TradeType,
                (double)average,
                sameSide.Count);
        }

        var point = Math.Max(1, pointMultiplier);
        var distance = requestedTradeType == 1
            ? (currentPrice.Value - average) * point
            : (average - currentPrice.Value) * point;
        var distancePts = (int)Math.Floor(distance);
        var absoluteDistancePts = (int)Math.Floor(Math.Abs(distance));
        var allowed = absoluteDistancePts >= requiredDistancePts;
        return new OppositeOpenPriceGuardResult(
            allowed,
            false,
            allowed ? "DISTANCE_REACHED" : "DISTANCE_NOT_REACHED",
            allowed
                ? "Khoảng cách giá đã đạt điều kiện mở đảo chiều"
                : "Khoảng cách giá chưa đạt điều kiện mở đảo chiều",
            latest.TradeType,
            (double)average,
            (double)currentPrice.Value,
            requestedTradeType == 1 ? "BID" : "ASK",
            sameSide.Count,
            distancePts,
            requiredDistancePts);
    }
}

public sealed record OppositeOpenPriceGuardResult(
    bool Allowed,
    bool Skipped,
    string ReasonCode,
    string ReasonVietnamese,
    int? LastTradeType = null,
    double? AverageOpenPriceA = null,
    double? CurrentPriceA = null,
    string? CurrentPriceType = null,
    int PositionCount = 0,
    int? DistancePts = null,
    int RequiredPts = 0)
{
    public static OppositeOpenPriceGuardResult Skip(string code, string reason, int? lastTradeType = null)
        => new(true, true, code, reason, lastTradeType);

    public static OppositeOpenPriceGuardResult Block(
        string code,
        string reason,
        int? lastTradeType = null,
        double? average = null,
        int count = 0)
        => new(false, false, code, reason, lastTradeType, average, PositionCount: count);
}
