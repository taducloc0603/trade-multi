namespace TradeDesktop.App.Services;

public interface IMt5ManualTradeService
{
    Task<ManualTradeResult> ExecuteBuyAsync(string chartHwndA, string chartHwndB, CancellationToken cancellationToken = default);
    Task<ManualTradeResult> ExecuteSellAsync(string chartHwndA, string chartHwndB, CancellationToken cancellationToken = default);
    Task<ManualTradeResult> ExecuteCloseAsync(ManualCloseRequest? closeA, ManualCloseRequest? closeB, CancellationToken cancellationToken = default);
}

public sealed record ManualCloseRequest(
    string Exchange,
    string TradeHwnd,
    ulong Ticket,
    int? RowIndex = null);

public sealed record ManualTradeLegResult(
    string Exchange,
    string Action,
    bool Success,
    string Detail,
    ulong? Ticket = null,
    ulong? Deal = null,
    double? ExecutedPrice = null,
    double? ExecutedVolume = null,
    uint? Retcode = null,
    string? ExecutionStatus = null);

public sealed record ManualTradeResult(
    string Label,
    bool Success,
    IReadOnlyList<ManualTradeLegResult> Legs,
    string? ErrorMessage = null,
    bool BlockedByGlobalActionGate = false,
    int GateRemainingMilliseconds = 0,
    bool BlockedByExecutionPolicy = false,
    string? PolicyBlockCode = null)
{
    public bool IsDispatchBlocked => BlockedByGlobalActionGate || BlockedByExecutionPolicy;
}
