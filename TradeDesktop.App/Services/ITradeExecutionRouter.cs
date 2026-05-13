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
    TradeOpenLegRequest LegB);

public sealed record TradeClosePairRequest(
    TradeCloseLegRequest? LegA,
    TradeCloseLegRequest? LegB);