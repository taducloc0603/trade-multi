using TradeDesktop.Application.Models;

namespace TradeDesktop.App.Services;

public interface ITradeExecutionRouter
{
    Task<ManualTradeResult> OpenPairAsync(TradeOpenPairRequest request, CancellationToken cancellationToken = default);
    Task<ManualTradeResult> ClosePairAsync(TradeClosePairRequest request, CancellationToken cancellationToken = default);
}

public enum TradeLegPlatform
{
    Mt4 = 0,
    Mt5 = 1
}

public enum TradeLegAction
{
    Buy = 0,
    Sell = 1,
    Close = 2
}

public enum TradeExecutionReason
{
    StrategicOpen = 0,
    StrategicClose = 1,
    ManualOpen = 2,
    ManualClose = 3,
    OpenPartialRollback = 4,
    ExternalPartialCloseRecovery = 5,
    PendingCloseRetry = 6
}

public sealed record SignalAuthorization(
    Guid SignalId,
    GapSignalAction Action,
    GapSignalTriggerType TriggerType,
    GapSignalSide Side,
    DateTime CreatedAtUtc,
    DateTime ValidUntilUtc,
    string SnapshotFingerprint,
    string? PairId,
    int? SlotId,
    CloseSignalReason CloseReason);

public sealed record TradeRecoveryEvidence(
    string PairId,
    int? SlotId,
    ulong Ticket,
    string Evidence);

public sealed record TradeExecutionContext(
    Guid RequestId,
    TradeExecutionReason Reason,
    string Source,
    string? PairId = null,
    int? SlotId = null,
    SignalAuthorization? Signal = null,
    TradeRecoveryEvidence? Recovery = null,
    Guid? ParentRequestId = null);

public sealed record TradeOpenLegRequest(
    string Exchange,
    TradeLegPlatform Platform,
    string ChartHwnd,
    TradeLegAction Action,
    int DelayMs = 0);

public sealed record TradeCloseLegRequest(
    string Exchange,
    TradeLegPlatform Platform,
    string TradeHwnd,
    ulong Ticket,
    TradeLegAction Action = TradeLegAction.Close,
    int DelayMs = 0,
    int? RowIndex = null);

public sealed record TradeOpenPairRequest(
    TradeOpenLegRequest LegA,
    TradeOpenLegRequest LegB,
    TradeExecutionContext Context);

public sealed record TradeClosePairRequest(
    TradeCloseLegRequest? LegA,
    TradeCloseLegRequest? LegB,
    TradeExecutionContext Context);
