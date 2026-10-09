namespace TradeDesktop.App.Services;

// Giữ chỗ cho PrimeXBT tới khi executor thật được bật (Phase 7 của docs/plans/primexbt). Mọi lệnh trả
// Success=false, KHÔNG throw — để router đi đúng đường partial-open / rollback sẵn có thay vì đường exception.
// Đây cũng là kill switch: đăng ký lại class này trong App.xaml.cs là tắt mọi lệnh sàn B PrimeXBT.
public sealed class NullPrimeXbtTradeExecutor : ITradePlatformExecutor
{
    private const string NotEnabledDetail = "PrimeXBT chưa được kích hoạt";

    public TradeLegPlatform Platform => TradeLegPlatform.PrimeXbt;

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
        Task.FromResult(new ManualTradeResult(Label: "OPEN_PRIMEXBT", Success: false, Legs: []));

    public Task<ManualTradeResult> ClosePairAsync(TradeClosePairRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ManualTradeResult(Label: "CLOSE_PRIMEXBT", Success: false, Legs: []));
}
