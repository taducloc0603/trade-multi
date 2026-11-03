using System.Globalization;
using TradeDesktop.Application.Models;
using TradeDesktop.Domain.Models;

namespace TradeDesktop.Application.Services;

/// <summary>
/// Dựng một dòng log gap theo tick cho file <c>*-gap-tick.log</c>.
/// Hàm thuần (không state, không I/O) để test được và để chi phí mỗi tick là hằng số.
/// </summary>
public static class GapTickLineFormatter
{
    public const string MissingValue = "-";

    /// <summary>
    /// Ví dụ:
    /// <c>[14:32:07.412] [GAP_TICK] gap_buy=12 gap_sell=-3 a_sym=XAUUSD a_bid=2412.35 ... point=100</c>
    /// </summary>
    /// <param name="metrics">Snapshot đã map, cùng nguồn với dữ liệu đưa vào signal engine.</param>
    /// <param name="point">Hệ số point runtime đang dùng.</param>
    /// <param name="fastLeg">
    /// Phán quyết sàn C của cùng tick. null (sàn C tắt) ⇒ dòng giữ nguyên từng byte như trước khi có sàn C.
    /// Các trường <c>c_*</c> chèn TRƯỚC <c>point=</c> để <c>point=</c> vẫn là trường cuối dòng.
    /// </param>
    public static string Format(DashboardMetrics metrics, decimal point, FastLegVerdict? fastLeg = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        var a = metrics.ExchangeA;
        var b = metrics.ExchangeB;
        var timestamp = metrics.TimestampUtc.Kind == DateTimeKind.Utc
            ? metrics.TimestampUtc.ToLocalTime()
            : metrics.TimestampUtc;

        return string.Concat(
            "[", timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), "] [GAP_TICK] ",
            "gap_buy=", FormatInt(metrics.GapBuy),
            " gap_sell=", FormatInt(metrics.GapSell),
            " a_sym=", FormatText(a.Symbol),
            " a_bid=", FormatDecimal(a.Bid),
            " a_ask=", FormatDecimal(a.Ask),
            " a_spread=", FormatDecimal(a.Spread),
            " a_lat=", FormatDecimal(a.LatencyMs),
            " b_sym=", FormatText(b.Symbol),
            " b_bid=", FormatDecimal(b.Bid),
            " b_ask=", FormatDecimal(b.Ask),
            " b_spread=", FormatDecimal(b.Spread),
            " b_lat=", FormatDecimal(b.LatencyMs),
            FormatFastLeg(fastLeg),
            " point=", point.ToString(CultureInfo.InvariantCulture));
    }

    private static string FormatFastLeg(FastLegVerdict? c)
    {
        if (c is null)
        {
            return string.Empty;
        }

        return string.Concat(
            " c_bid=", FormatDecimal(c.CBid),
            " c_ask=", FormatDecimal(c.CAsk),
            " c_age=", c.AgeMs.HasValue ? c.AgeMs.Value.ToString(CultureInfo.InvariantCulture) : MissingValue,
            " c_move=", c.MovePts.ToString("0.##", CultureInfo.InvariantCulture),
            " c_verdict=", c.Kind.ToString());
    }

    private static string FormatInt(int? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : MissingValue;

    private static string FormatDecimal(decimal? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : MissingValue;

    private static string FormatText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? MissingValue : value;
}
