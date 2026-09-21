namespace TradeDesktop.Application.Services.CTrader;

public sealed record PointDigitsCheckResult(bool IsConsistent, int? ExpectedPoint, string Message);

// R6: point cấu hình là global dùng chung hai sàn. Digits của symbol cTrader phải cho đúng point đó,
// lệch là gap sai một luỹ thừa 10 → phải fail closed (Phase 4 wire). Phase 2 chỉ định nghĩa + test.
public static class PointDigitsConsistencyChecker
{
    private const int MaxSupportedDigits = 9;

    public static PointDigitsCheckResult Check(int configuredPoint, int ctraderDigits)
    {
        if (configuredPoint <= 0)
        {
            return new PointDigitsCheckResult(false, null, $"point cấu hình không hợp lệ: {configuredPoint}");
        }

        if (ctraderDigits < 0 || ctraderDigits > MaxSupportedDigits)
        {
            return new PointDigitsCheckResult(false, null, $"digits cTrader không hợp lệ: {ctraderDigits}");
        }

        var expected = 1;
        for (var i = 0; i < ctraderDigits; i++)
        {
            expected *= 10;
        }

        return expected == configuredPoint
            ? new PointDigitsCheckResult(true, expected, $"point={configuredPoint} khớp digits={ctraderDigits}")
            : new PointDigitsCheckResult(false, expected,
                $"point={configuredPoint} KHÔNG khớp digits={ctraderDigits} (kỳ vọng point={expected})");
    }
}
