using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public static class TradePlatformHwndPolicy
{
    public static bool IsIssueRelevant(HwndIssue issue, string? platformA, string? platformB)
    {
        if (issue.Label.EndsWith(" A", StringComparison.OrdinalIgnoreCase))
        {
            return IsMt4(platformA);
        }

        if (issue.Label.EndsWith(" B", StringComparison.OrdinalIgnoreCase))
        {
            return IsMt4(platformB);
        }

        return IsMt4(platformA) || IsMt4(platformB);
    }

    public static bool HasRequiredConfiguration(
        IReadOnlyList<ManualHwndColumnConfig> columns,
        string? platformA,
        string? platformB)
    {
        if (!IsMt4(platformA) && !IsMt4(platformB))
        {
            return true;
        }

        return columns.Any(column =>
            (!IsMt4(platformA)
             || (!string.IsNullOrWhiteSpace(column.ChartHwndA)
                 && !string.IsNullOrWhiteSpace(column.TradeHwndA)))
            && (!IsMt4(platformB)
                || (!string.IsNullOrWhiteSpace(column.ChartHwndB)
                    && !string.IsNullOrWhiteSpace(column.TradeHwndB))));
    }

    private static bool IsMt4(string? platform) =>
        string.Equals(platform?.Trim(), "mt4", StringComparison.OrdinalIgnoreCase);
}
