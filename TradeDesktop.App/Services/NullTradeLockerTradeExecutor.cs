namespace TradeDesktop.App.Services;

// Giữ chỗ cho TradeLocker tới khi executor thật được bật (Phase 7A của docs/plans/tradelocker). Mọi lệnh trả
// Success=false, KHÔNG throw — để router đi đúng đường partial-open / rollback sẵn có thay vì đường exception.
// Đây cũng là kill switch: đăng ký lại class này trong App.xaml.cs là tắt mọi lệnh sàn B TradeLocker.
public sealed class NullTradeLockerTradeExecutor : ITradePlatformExecutor
{
    private const string NotEnabledDetail = "TradeLocker chưa được kích hoạt";

    public TradeLegPlatform Platform => TradeLegPlatform.TradeLocker;

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
        Task.FromResult(new ManualTradeResult(Label: "OPEN_TRADELOCKER", Success: false, Legs: []));

    public Task<ManualTradeResult> ClosePairAsync(TradeClosePairRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ManualTradeResult(Label: "CLOSE_TRADELOCKER", Success: false, Legs: []));
}
