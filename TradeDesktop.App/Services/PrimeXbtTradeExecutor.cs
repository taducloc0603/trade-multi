using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Services.PrimeXbt;

namespace TradeDesktop.App.Services;

// docs/plans/primexbt Phase 7 — executor thật sàn B PrimeXBT. Rule E: THUẦN BỊ ĐỘNG — chỉ thực thi lệnh router ra;
// KHÔNG BAO GIỜ tự flatten, tự retry, tự reconcile bằng lệnh. Shim mỏng: dịch request router → PrimeXbtOrderPlanner →
// IPrimeXbtTradeSession.SendOrderAsync (đường duy nhất gửi lệnh) rồi dịch ngược kết quả.
//
// Success = RESPONSE không lỗi, KHÔNG phải "đã có vị thế": ticket B được ghép vào pair qua polling Trades map như mọi
// platform khác (D2). TIMEOUT_UNCERTAIN ⇒ Success=false để router đi đường partial-open/pending-close sẵn có; nếu lệnh
// thật ra đã khớp, ticket vẫn xuất hiện trên Trades map và vòng poll xác nhận như bình thường.
// Kill switch: đăng ký lại NullPrimeXbtTradeExecutor trong App.xaml.cs.
public sealed class PrimeXbtTradeExecutor(
    IPrimeXbtTradeSession tradeSession,
    IRuntimeConfigProvider runtimeConfig) : ITradePlatformExecutor
{
    private const string PairLevelNotSupported = "PrimeXBT không hỗ trợ pair-level dispatch";

    public TradeLegPlatform Platform => TradeLegPlatform.PrimeXbt;

    public async Task<ManualTradeLegResult> OpenLegAsync(TradeOpenLegRequest request, CancellationToken cancellationToken = default)
    {
        var action = request.Action.ToString().ToUpperInvariant();
        var config = runtimeConfig.CurrentPrimeXbtConfig;
        var side = request.Action == TradeLegAction.Buy ? PrimeXbtSide.Buy : PrimeXbtSide.Sell;
        var plan = PrimeXbtOrderPlanner.PlanOpen(config.Symbol, side, config.VolumeBOz, tradeSession.CurrentTradeSettings);

        return plan.IsSuccess
            ? await SendAsync(request.Exchange, action, plan.Plan!, cancellationToken).ConfigureAwait(false)
            : Fail(request.Exchange, action, plan.Error!);
    }

    public async Task<ManualTradeLegResult> CloseLegAsync(TradeCloseLegRequest request, CancellationToken cancellationToken = default)
    {
        var action = request.Action.ToString().ToUpperInvariant();
        if (!PrimeXbtTicketCodec.TryDecode(request.Ticket, out var positionId))
        {
            return Fail(request.Exchange, action, $"Ticket {request.Ticket} không phải ticket PrimeXBT");
        }

        // Qty lấy đúng sub-position đang mở trong cache; không có ⇒ fail closed (không đoán, không đóng dòng gộp).
        if (tradeSession.TryGetOpenPosition(positionId) is not { } position)
        {
            return Fail(request.Exchange, action, $"Không có sub-position {positionId} đang mở trong cache PrimeXBT");
        }

        var plan = PrimeXbtOrderPlanner.PlanClose(positionId, position.Qty);
        return plan.IsSuccess
            ? await SendAsync(request.Exchange, action, plan.Plan!, cancellationToken).ConfigureAwait(false)
            : Fail(request.Exchange, action, plan.Error!);
    }

    // Router luôn dispatch per-leg; hai method dưới chỉ vì interface bắt buộc — trả lỗi tường minh.
    public Task<ManualTradeResult> OpenPairAsync(TradeOpenPairRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new ManualTradeResult("OPEN_PRIMEXBT", false, [Fail("B", "OPEN", PairLevelNotSupported)]));

    public Task<ManualTradeResult> ClosePairAsync(TradeClosePairRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new ManualTradeResult("CLOSE_PRIMEXBT", false, [Fail("B", "CLOSE", PairLevelNotSupported)]));

    private async Task<ManualTradeLegResult> SendAsync(
        string exchange, string action, PrimeXbtOrderPlan plan, CancellationToken cancellationToken)
    {
        var outcome = await tradeSession.SendOrderAsync(plan, cancellationToken).ConfigureAwait(false);
        return new ManualTradeLegResult(exchange, action, outcome.Success, outcome.Detail);
    }

    private static ManualTradeLegResult Fail(string exchange, string action, string detail)
        => new(exchange, action, false, detail);
}
