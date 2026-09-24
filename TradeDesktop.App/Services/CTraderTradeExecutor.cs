using System.Globalization;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Services.CTrader;

namespace TradeDesktop.App.Services;

// Rule E — lớp này THUẦN BỊ ĐỘNG.
// Báo cáo trạng thái, thực thi lệnh được ra lệnh, hết.
// KHÔNG BAO GIỜ: tự flatten position cho là mồ côi, tự retry close,
// tự reconcile bằng cách gửi lệnh.
// Bất kỳ hành vi nào như vậy là một đường open/close mới bỏ qua signal engine.
//
// Shim mỏng: mọi kiểu FIX nằm trong Infrastructure; ở đây chỉ dịch request của router thành
// CTraderOrderRequest rồi dịch ngược kết quả. Không giữ trạng thái giao dịch nào ngoài bộ đếm ClOrdID.
//
// §4.2: ExecutionReport dùng để bắt REJECT, KHÔNG dùng làm nguồn xác nhận ticket — ticket vẫn được phát hiện
// qua polling trades map như mọi platform khác. Vì vậy Success=true ở đây chỉ nghĩa "sàn đã khớp", phần ghép
// ticket vào pair do vòng poll của DashboardViewModel làm.
public sealed class CTraderTradeExecutor(
    ICTraderTradeSession tradeSession,
    IRuntimeConfigProvider runtimeConfig) : ITradePlatformExecutor
{
    private const string PairLevelNotSupported = "cTrader không hỗ trợ pair-level dispatch";

    private int _counter;

    // Hậu tố sinh MỘT LẦN mỗi lần chạy app. Counter reset về 0 sau restart nên chỉ {unixMs}-{counter} là chưa đủ:
    // hai lệnh rơi đúng cùng mili-giây qua hai lần chạy sẽ trùng ClOrdID, mà đó là khoá tra OrderStatusRequest —
    // trùng là mất khả năng truy vết lệnh nào ứng với report nào.
    private readonly string _runSuffix = Guid.NewGuid().ToString("N")[..4];

    public TradeLegPlatform Platform => TradeLegPlatform.CTrader;

    public async Task<ManualTradeLegResult> OpenLegAsync(TradeOpenLegRequest request, CancellationToken cancellationToken = default)
    {
        var action = request.Action.ToString().ToUpperInvariant();
        var plan = CTraderOrderPlanner.PlanOpen(
            runtimeConfig.CurrentCTraderFixConfig, request.Action == TradeLegAction.Buy, NextClOrdId(request.Exchange));

        return plan.IsValid
            ? await SendAsync(request.Exchange, action, plan.Request!, cancellationToken).ConfigureAwait(false)
            : Fail(request.Exchange, action, plan.Error!);
    }

    public async Task<ManualTradeLegResult> CloseLegAsync(TradeCloseLegRequest request, CancellationToken cancellationToken = default)
    {
        var action = request.Action.ToString().ToUpperInvariant();

        // Chiều + khối lượng lấy từ cache session; planner fail closed nếu thiếu. RowIndex/TradeHwnd chỉ có
        // nghĩa với UI automation của MT nên bỏ qua.
        var position = CTraderTicketCodec.TryDecode(request.Ticket, out var positionId)
            ? tradeSession.TryGetOpenPosition(positionId)
            : null;

        var plan = CTraderOrderPlanner.PlanClose(
            runtimeConfig.CurrentCTraderFixConfig, request.Ticket, position, NextClOrdId(request.Exchange));

        return plan.IsValid
            ? await SendAsync(request.Exchange, action, plan.Request!, cancellationToken).ConfigureAwait(false)
            : Fail(request.Exchange, action, plan.Error!);
    }

    // Router luôn dispatch per-leg (ExecuteOpenPairPerLegAsync / ExecuteClosePairPerLegAsync). Hai method dưới
    // chỉ tồn tại vì interface bắt buộc — trả lỗi tường minh để ai gọi nhầm thấy ngay thay vì âm thầm sai.
    public Task<ManualTradeResult> OpenPairAsync(TradeOpenPairRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new ManualTradeResult("OPEN_CTRADER", false, [Fail("B", "OPEN", PairLevelNotSupported)]));

    public Task<ManualTradeResult> ClosePairAsync(TradeClosePairRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new ManualTradeResult("CLOSE_CTRADER", false, [Fail("B", "CLOSE", PairLevelNotSupported)]));

    private async Task<ManualTradeLegResult> SendAsync(
        string exchange, string action, CTraderOrderRequest request, CancellationToken cancellationToken)
    {
        var outcome = await tradeSession.SendMarketOrderAsync(request, cancellationToken).ConfigureAwait(false);
        return new ManualTradeLegResult(exchange, action, outcome.Success, outcome.Detail);
    }

    // ClOrdID duy nhất XUYÊN RESTART = thời điểm + counter trong phiên + hậu tố của lần chạy.
    private string NextClOrdId(string exchange)
        => string.Create(CultureInfo.InvariantCulture,
            $"{exchange}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Interlocked.Increment(ref _counter)}-{_runSuffix}");

    private static ManualTradeLegResult Fail(string exchange, string action, string detail)
        => new(exchange, action, false, detail);
}
