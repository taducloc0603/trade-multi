using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Services.TradeLocker;

namespace TradeDesktop.App.Services;

// docs/plans/tradelocker Phase 7A — executor thật sàn B TradeLocker. Rule E: THUẦN BỊ ĐỘNG — chỉ thực thi lệnh router ra;
// KHÔNG BAO GIỜ tự flatten, tự retry, tự reconcile bằng lệnh. Shim mỏng: request router → TradeLockerOrderPlanner →
// ITradeLockerTradeSession.SendOrderAsync (đường duy nhất gửi lệnh) rồi dịch ngược kết quả.
//
// Success = server ack, KHÔNG phải "đã có vị thế": ticket B được ghép vào pair qua polling Trades map như mọi platform.
// TIMEOUT_UNCERTAIN ⇒ Success=false để router đi đường partial-open/pending-close sẵn có; nếu lệnh thật ra đã khớp,
// ticket vẫn xuất hiện trên Trades map và vòng poll xác nhận như bình thường.
// Kill switch: đăng ký lại NullTradeLockerTradeExecutor trong App.xaml.cs.
public sealed class TradeLockerTradeExecutor(
    ITradeLockerTradeSession tradeSession,
    IRuntimeConfigProvider runtimeConfig) : ITradePlatformExecutor, ITradeLegOpenReadiness
{
    private const string PairLevelNotSupported = "TradeLocker không hỗ trợ pair-level dispatch";

    public TradeLegPlatform Platform => TradeLegPlatform.TradeLocker;

    // Chưa đăng nhập / socket rớt / symbol không ACTIVE ⇒ router chặn cả cặp trước dispatch (LEG_B_NOT_READY).
    public string? GetOpenBlockReason() => tradeSession.OpenBlockReason;

    public async Task<ManualTradeLegResult> OpenLegAsync(TradeOpenLegRequest request, CancellationToken cancellationToken = default)
    {
        var action = request.Action.ToString().ToUpperInvariant();
        var side = request.Action switch
        {
            TradeLegAction.Buy => TradeLockerSide.Buy,
            TradeLegAction.Sell => TradeLockerSide.Sell,
            _ => TradeLockerSide.Unspecified
        };
        var plan = TradeLockerOrderPlanner.PlanOpen(tradeSession.CurrentSymbol, side, runtimeConfig.CurrentTradeLockerConfig.VolumeBLots);

        return plan.IsSuccess
            ? await SendAsync(request.Exchange, action, plan.Plan!, cancellationToken).ConfigureAwait(false)
            : Fail(request.Exchange, action, plan.Error!);
    }

    public async Task<ManualTradeLegResult> CloseLegAsync(TradeCloseLegRequest request, CancellationToken cancellationToken = default)
    {
        var action = request.Action.ToString().ToUpperInvariant();
        var plan = TradeLockerOrderPlanner.PlanClose(request.Ticket);
        if (!plan.IsSuccess)
        {
            return Fail(request.Exchange, action, plan.Error!);
        }

        // Chỉ đóng position đang mở trong danh sách vị thế hiện tại; không có ⇒ fail closed (không đoán).
        if (tradeSession.TryGetOpenPosition(plan.Plan!.PositionId!) is null)
        {
            return Fail(request.Exchange, action, $"Không có position {plan.Plan.PositionId} đang mở trên TradeLocker");
        }

        return await SendAsync(request.Exchange, action, plan.Plan, cancellationToken).ConfigureAwait(false);
    }

    // Router luôn dispatch per-leg; hai method dưới chỉ vì interface bắt buộc — trả lỗi tường minh.
    public Task<ManualTradeResult> OpenPairAsync(TradeOpenPairRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new ManualTradeResult("OPEN_TRADELOCKER", false, [Fail("B", "OPEN", PairLevelNotSupported)]));

    public Task<ManualTradeResult> ClosePairAsync(TradeClosePairRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new ManualTradeResult("CLOSE_TRADELOCKER", false, [Fail("B", "CLOSE", PairLevelNotSupported)]));

    private async Task<ManualTradeLegResult> SendAsync(
        string exchange, string action, TradeLockerOrderPlan plan, CancellationToken cancellationToken)
    {
        var outcome = await tradeSession.SendOrderAsync(plan, cancellationToken).ConfigureAwait(false);
        return new ManualTradeLegResult(exchange, action, outcome.Success, outcome.Detail);
    }

    private static ManualTradeLegResult Fail(string exchange, string action, string detail)
        => new(exchange, action, false, detail);
}
