namespace TradeDesktop.App.Services;

// Giữ chỗ cho cTrader tới khi executor FIX thật được bật (Phase 7). Mọi lệnh trả Success=false,
// KHÔNG throw — để router đi đúng đường partial-open / rollback sẵn có thay vì đường exception.
public sealed class NullCTraderTradeExecutor : ITradePlatformExecutor
{
    private const string NotEnabledDetail = "cTrader chưa được kích hoạt";

    public TradeLegPlatform Platform => TradeLegPlatform.CTrader;

    public Task<ManualTradeLegResult> OpenLegAsync(TradeOpenLegRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ManualTradeLegResult(
            Exchange: request.Exchange,
            Action: request.Action.ToString().ToUpperInvariant(),
            Success: false,
            Detail: NotEnabledDetail));

    public Task<ManualTradeLegResult> CloseLegAsync(TradeCloseLegRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ManualTradeLegResult(
            Exchange: request.Exchange,
            Action: request.Action.ToString().ToUpperInvariant(),
            Success: false,
            Detail: NotEnabledDetail));

    public Task<ManualTradeResult> OpenPairAsync(TradeOpenPairRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ManualTradeResult(Label: "OPEN_CTRADER", Success: false, Legs: []));

    public Task<ManualTradeResult> ClosePairAsync(TradeClosePairRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ManualTradeResult(Label: "CLOSE_CTRADER", Success: false, Legs: []));
}
