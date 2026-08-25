using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

/// <summary>
/// Bộ tính toán thuần cho độ ổn định của một chuỗi Gap.
/// Các phép đo sử dụng trị tuyệt đối; danh sách Gap gốc không bị thay đổi dấu.
/// </summary>
public static class GapStabilityCalculator
{
    public sealed record Metrics(
        double Center,
        double Mad,
        double Tolerance,
        double Dispersion,
        double EarlyCenter,
        double LateCenter,
        double Drift,
        int SampleCount);

    public static Metrics Calculate(
        IReadOnlyList<int> gaps,
        GapStabilityConfig config)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        ArgumentNullException.ThrowIfNull(config);

        if (gaps.Count == 0)
        {
            throw new ArgumentException("Cần ít nhất một mẫu Gap.", nameof(gaps));
        }

        if (!config.TryValidate(out var error))
        {
            throw new ArgumentOutOfRangeException(nameof(config), error);
        }

        var magnitudes = gaps.Select(ToMagnitude).ToArray();
        var center = Median(magnitudes);
        var deviations = magnitudes.Select(value => Math.Abs(value - center)).ToArray();
        var mad = Median(deviations);
        var tolerance = CalculateTolerance(center, mad, config);
        var denominator = Math.Max(center, 1d);
        var dispersion = mad / denominator;
        var (earlyCenter, lateCenter) = CalculateHalfCenters(magnitudes);
        var drift = Math.Abs(lateCenter - earlyCenter) / denominator;

        return new Metrics(
            center,
            mad,
            tolerance,
            dispersion,
            earlyCenter,
            lateCenter,
            drift,
            gaps.Count);
    }

    public static double CalculateDelta(int newGap, double center)
    {
        if (!double.IsFinite(center) || center < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(center),
                "Center phải là số hữu hạn và >= 0.");
        }

        return Math.Abs(ToMagnitude(newGap) - center);
    }

    public static double Median(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
        {
            throw new ArgumentException("Không thể tính Median của danh sách rỗng.", nameof(values));
        }

        if (sorted.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Median chỉ chấp nhận số hữu hạn.", nameof(values));
        }

        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2d;
    }

    private static double CalculateTolerance(
        double center,
        double mad,
        GapStabilityConfig config) =>
        Math.Max(
            config.AbsoluteFloor,
            Math.Max(
                config.RelativeTolerance * center,
                config.MadMultiplier * mad));

    private static (double EarlyCenter, double LateCenter) CalculateHalfCenters(
        IReadOnlyList<double> magnitudes)
    {
        if (magnitudes.Count == 1)
        {
            return (magnitudes[0], magnitudes[0]);
        }

        var halfCount = magnitudes.Count / 2;
        var lateStart = magnitudes.Count - halfCount;
        var earlyCenter = Median(magnitudes.Take(halfCount));
        var lateCenter = Median(magnitudes.Skip(lateStart));
        return (earlyCenter, lateCenter);
    }

    private static double ToMagnitude(int gap) => Math.Abs((long)gap);
}
