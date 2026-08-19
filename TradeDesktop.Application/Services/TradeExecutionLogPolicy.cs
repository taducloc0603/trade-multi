namespace TradeDesktop.Application.Services;

public static class TradeExecutionLogPolicy
{
    public static bool ShouldWritePairOpen(bool pairSuccess, IReadOnlyList<bool> legResults) =>
        pairSuccess
        && legResults.Count == 2
        && legResults.All(success => success);
}
