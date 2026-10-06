using System.Globalization;

namespace TradeDesktop.Application.Services.CTrader;

public enum HedgeLotMatchLevel
{
    Match,
    Mismatch,
    Unknown
}

public sealed record HedgeLotMatchResult(HedgeLotMatchLevel Level, string Message);

// So lot THẬT của hai chân sau khi cặp Auto Open đầu tiên confirm (chế độ cTrader). Khác
// HedgeVolumeConsistencyChecker (chỉ so số config lúc Start): lot chân A do trader cài trong terminal MT,
// app không kiểm soát. Lệch lot = mất hedge mà profit theo point (R5) không phản ánh.
// Unknown (thiếu / không hợp lệ) được caller xử lý như Mismatch (fail-closed).
public static class HedgeLotMatchChecker
{
    // Bước lot nhỏ nhất 0.01 → so sau khi làm tròn 2 chữ số; epsilon khử nhiễu double.
    private const int LotDecimals = 2;
    private const double Epsilon = 1e-7;

    public static HedgeLotMatchResult Check(double? lotA, double? lotB)
    {
        var validA = IsValid(lotA);
        var validB = IsValid(lotB);
        if (!validA || !validB)
        {
            var missing = !validA && !validB ? "A,B" : !validA ? "A" : "B";
            return new HedgeLotMatchResult(
                HedgeLotMatchLevel.Unknown,
                $"lot unreadable leg={missing} lotA={Format(lotA)} lotB={Format(lotB)}");
        }

        var roundedA = Math.Round(lotA!.Value, LotDecimals, MidpointRounding.AwayFromZero);
        var roundedB = Math.Round(lotB!.Value, LotDecimals, MidpointRounding.AwayFromZero);
        var level = Math.Abs(roundedA - roundedB) < Epsilon ? HedgeLotMatchLevel.Match : HedgeLotMatchLevel.Mismatch;
        return new HedgeLotMatchResult(level, $"lotA={Format(lotA)} lotB={Format(lotB)}");
    }

    private static bool IsValid(double? lot)
        => lot is { } value && double.IsFinite(value) && value > 0;

    private static string Format(double? lot)
        => lot?.ToString("0.#####", CultureInfo.InvariantCulture) ?? "null";
}
