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

        // Hai buffer dung chung cho ca 4 phep Median, thay vi ~6 mang trung gian nhu truoc.
        // scratch bi quickselect lam xao tron nen magnitudes phai duoc giu nguyen de tinh nua sau.
        var count = gaps.Count;
        var magnitudes = new double[count];
        for (var i = 0; i < count; i++)
        {
            magnitudes[i] = ToMagnitude(gaps[i]);
        }

        var scratch = new double[count];
        magnitudes.CopyTo(scratch, 0);
        var center = MedianDestructive(scratch.AsSpan());

        for (var i = 0; i < count; i++)
        {
            scratch[i] = Math.Abs(magnitudes[i] - center);
        }

        var mad = MedianDestructive(scratch.AsSpan());
        var tolerance = CalculateTolerance(center, mad, config);
        var denominator = Math.Max(center, 1d);
        var dispersion = mad / denominator;
        var (earlyCenter, lateCenter) = CalculateHalfCenters(magnitudes, scratch);
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

    /// <summary>
    /// Trả về <paramref name="metrics"/> với Tolerance tính lại theo <paramref name="config"/> hiện tại.
    /// Center/Mad/Dispersion/EarlyCenter/LateCenter/Drift chỉ phụ thuộc chuỗi Gap nên được giữ nguyên;
    /// chỉ Tolerance phụ thuộc config (AbsoluteFloor / RelativeTolerance / MadMultiplier).
    ///
    /// Dùng để tái sử dụng Metrics đã tính cho cùng một chuỗi Gap mà vẫn đúng khi config được reload
    /// giữa hai tick. Rẻ hơn <see cref="Calculate"/> rất nhiều vì không phải sort lại toàn bộ danh sách.
    /// </summary>
    public static Metrics WithTolerance(Metrics metrics, GapStabilityConfig config)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(config);

        if (!config.TryValidate(out var error))
        {
            throw new ArgumentOutOfRangeException(nameof(config), error);
        }

        var tolerance = CalculateTolerance(metrics.Center, metrics.Mad, config);
        return metrics.Tolerance.Equals(tolerance)
            ? metrics
            : metrics with { Tolerance = tolerance };
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

        // Sao chep ra mang rieng: MedianDestructive xao tron thu tu, khong duoc cham vao collection
        // cua caller.
        var buffer = values.ToArray();
        if (buffer.Length == 0)
        {
            throw new ArgumentException("Không thể tính Median của danh sách rỗng.", nameof(values));
        }

        for (var i = 0; i < buffer.Length; i++)
        {
            if (!double.IsFinite(buffer[i]))
            {
                throw new ArgumentException("Median chỉ chấp nhận số hữu hạn.", nameof(values));
            }
        }

        return MedianDestructive(buffer.AsSpan());
    }

    /// <summary>
    /// Median bang quickselect: O(n) trung binh thay vi O(n log n) cua mot phep sort day du.
    /// Ham LAM XAO TRON <paramref name="values"/>; caller phai truyen buffer dung mot lan.
    /// Chi chap nhan so huu han - caller co trach nhiem kiem tra truoc.
    /// </summary>
    private static double MedianDestructive(Span<double> values)
    {
        var length = values.Length;
        var middle = length / 2;
        var upper = QuickSelect(values, middle);
        if (length % 2 == 1)
        {
            return upper;
        }

        // Sau QuickSelect(middle), moi phan tu trong [0, middle) deu <= values[middle],
        // nen phan tu thu (middle - 1) theo thu tu tang dan chinh la max cua vung do.
        var lower = double.NegativeInfinity;
        for (var i = 0; i < middle; i++)
        {
            if (values[i] > lower)
            {
                lower = values[i];
            }
        }

        return (lower + upper) / 2d;
    }

    /// <summary>
    /// Dua phan tu dung thu hang <paramref name="k"/> (0-based) ve vi tri k va tra ve gia tri do.
    /// Hoare partition + pivot median-of-three de tranh truong hop suy bien O(n^2) tren du lieu
    /// da sap xep san - vo cung pho bien o day vi chuoi Gap thuong gan nhu khong doi.
    /// </summary>
    private static double QuickSelect(Span<double> values, int k)
    {
        var left = 0;
        var right = values.Length - 1;

        while (left < right)
        {
            var pivot = MedianOfThree(values, left, right);
            var i = left;
            var j = right;

            while (i <= j)
            {
                while (values[i] < pivot)
                {
                    i++;
                }

                while (values[j] > pivot)
                {
                    j--;
                }

                if (i <= j)
                {
                    (values[i], values[j]) = (values[j], values[i]);
                    i++;
                    j--;
                }
            }

            if (k <= j)
            {
                right = j;
            }
            else if (k >= i)
            {
                left = i;
            }
            else
            {
                return values[k];
            }
        }

        return values[left];
    }

    private static double MedianOfThree(Span<double> values, int left, int right)
    {
        var mid = left + ((right - left) >> 1);
        var a = values[left];
        var b = values[mid];
        var c = values[right];

        if (a > b)
        {
            (a, b) = (b, a);
        }

        if (b > c)
        {
            (b, c) = (c, b);
        }

        if (a > b)
        {
            (a, b) = (b, a);
        }

        return b;
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

    /// <summary>
    /// Median cua nua dau va nua sau chuoi, dung de do Drift. Giu nguyen quy tac cu:
    /// halfCount = count / 2 va lateStart = count - halfCount, nen voi count le thi mau giua
    /// khong thuoc nua nao.
    /// <paramref name="scratch"/> phai dai it nhat bang <paramref name="magnitudes"/> va se bi ghi de.
    /// </summary>
    private static (double EarlyCenter, double LateCenter) CalculateHalfCenters(
        double[] magnitudes,
        double[] scratch)
    {
        if (magnitudes.Length == 1)
        {
            return (magnitudes[0], magnitudes[0]);
        }

        var halfCount = magnitudes.Length / 2;
        var lateStart = magnitudes.Length - halfCount;

        magnitudes.AsSpan(0, halfCount).CopyTo(scratch);
        var earlyCenter = MedianDestructive(scratch.AsSpan(0, halfCount));

        magnitudes.AsSpan(lateStart, halfCount).CopyTo(scratch);
        var lateCenter = MedianDestructive(scratch.AsSpan(0, halfCount));

        return (earlyCenter, lateCenter);
    }

    private static double ToMagnitude(int gap) => Math.Abs((long)gap);
}
