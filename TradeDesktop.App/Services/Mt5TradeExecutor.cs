using System.Globalization;
using TradeDesktop.App.Native;

namespace TradeDesktop.App.Services;

public sealed class Mt5TradeExecutor : ITradePlatformExecutor
{
    private readonly IMt5ManualTradeService _mt5ManualTradeService;
    private readonly ITradeSessionFileLogger _logger;
    private readonly SemaphoreSlim _actionGate = new(1, 1);

    public Mt5TradeExecutor(IMt5ManualTradeService mt5ManualTradeService, ITradeSessionFileLogger logger)
    {
        _mt5ManualTradeService = mt5ManualTradeService;
        _logger = logger;
    }

    public TradeLegPlatform Platform => TradeLegPlatform.Mt5;

    public async Task<ManualTradeLegResult> OpenLegAsync(TradeOpenLegRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            if (!TryParseHwnd(request.ChartHwnd, out var chartHwnd))
            {
                SafeLog($"[MT5][WARN] Open leg {request.Exchange} failed: invalid chart hwnd={request.ChartHwnd}");
                return new ManualTradeLegResult(
                    Exchange: request.Exchange,
                    Action: request.Action.ToString().ToUpperInvariant(),
                    Success: false,
                    Detail: $"Open {request.Exchange} failed: HWND CHART không hợp lệ");
            }

            if (!IsValidWindow(chartHwnd))
            {
                SafeLog($"[MT5][WARN] Open leg {request.Exchange} failed: chart hwnd not valid ({request.ChartHwnd})");
                return new ManualTradeLegResult(
                    Exchange: request.Exchange,
                    Action: request.Action.ToString().ToUpperInvariant(),
                    Success: false,
                    Detail: $"Open {request.Exchange} failed: CHART HWND không còn hợp lệ");
            }

            var actionText = request.Action.ToString().ToUpperInvariant();
            var clickResult = request.Action switch
            {
                TradeLegAction.Buy => NativeMethodsMt5.ClickBuy(chartHwnd),
                TradeLegAction.Sell => NativeMethodsMt5.ClickSell(chartHwnd),
                _ => 0
            };

            if (request.Action is not (TradeLegAction.Buy or TradeLegAction.Sell))
            {
                SafeLog($"[MT5][WARN] Open leg {request.Exchange} failed: unsupported action={request.Action}");
                return new ManualTradeLegResult(request.Exchange, actionText, false, "Unsupported MT5 open action");
            }

            var success = clickResult == 1;
            SafeLog($"[MT5][{(success ? "INFO" : "WARN")}] Open leg {request.Exchange} action={actionText} result={(success ? "ok" : "failed")}");
            return new ManualTradeLegResult(
                Exchange: request.Exchange,
                Action: actionText,
                Success: success,
                Detail: success ? "clicked" : "click failed");
        }
        catch (Exception ex)
        {
            SafeLog($"[MT5][ERROR] Open leg {request.Exchange} threw: {ex}");
            return new ManualTradeLegResult(
                Exchange: request.Exchange,
                Action: request.Action.ToString().ToUpperInvariant(),
                Success: false,
                Detail: ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    public async Task<ManualTradeLegResult> CloseLegAsync(TradeCloseLegRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            if (!TryParseHwnd(request.TradeHwnd, out var tradeParentHwnd))
            {
                SafeLog($"[MT5][WARN] Close leg {request.Exchange} ticket={request.Ticket} failed: invalid trade hwnd={request.TradeHwnd}");
                return new ManualTradeLegResult(
                    Exchange: request.Exchange,
                    Action: "CLOSE",
                    Success: false,
                    Detail: $"Close {request.Exchange} failed: ticket={request.Ticket}, error=TRADE HWND không hợp lệ");
            }

            if (!IsValidWindow(tradeParentHwnd))
            {
                SafeLog($"[MT5][WARN] Close leg {request.Exchange} ticket={request.Ticket} failed: trade hwnd not valid ({request.TradeHwnd})");
                return new ManualTradeLegResult(
                    Exchange: request.Exchange,
                    Action: "CLOSE",
                    Success: false,
                    Detail: $"Close {request.Exchange} failed: ticket={request.Ticket}, error=TRADE HWND không còn hợp lệ");
            }

            IntPtr ctx = IntPtr.Zero;
            try
            {
                ctx = NativeMethodsMt5.CreateContextFromParent(tradeParentHwnd);
                if (ctx == IntPtr.Zero)
                {
                    SafeLog($"[MT5][WARN] Close leg {request.Exchange} ticket={request.Ticket} failed: create_context_from_parent returned 0");
                    return new ManualTradeLegResult(
                        Exchange: request.Exchange,
                        Action: "CLOSE",
                        Success: false,
                        Detail: $"Close {request.Exchange} failed: ticket={request.Ticket}, error=create_context_from_parent failed");
                }

                var rowCount = NativeMethodsMt5.UpdateRowCount(ctx);
                if (rowCount <= 0)
                {
                    SafeLog($"[MT5][INFO] Close leg {request.Exchange} ticket={request.Ticket} skipped: no open trade");
                    return new ManualTradeLegResult(
                        Exchange: request.Exchange,
                        Action: "CLOSE",
                        Success: true,
                        Detail: $"Close {request.Exchange} skipped: no open trade (ticket={request.Ticket})");
                }

                if (request.RowIndex is null)
                {
                    SafeLog($"[MT5][WARN] Close leg {request.Exchange} ticket={request.Ticket} failed: unresolved rowIndex rowCount={rowCount}");
                    return new ManualTradeLegResult(
                        Exchange: request.Exchange,
                        Action: "CLOSE",
                        Success: false,
                        Detail: $"Close {request.Exchange} skipped: ticket={request.Ticket} row=unresolved rowCount={rowCount} source=Mt5TradeExecutor");
                }

                var rowIndex = Math.Max(0, Math.Min(request.RowIndex.Value, rowCount - 1));
                var closeResult = NativeMethodsMt5.ClosePositionMt5(ctx, rowIndex);
                var success = closeResult == 1;
                SafeLog($"[MT5][{(success ? "INFO" : "WARN")}] Close leg {request.Exchange} ticket={request.Ticket} row={rowIndex} result={(success ? "ok" : "failed")}");
                return new ManualTradeLegResult(
                    Exchange: request.Exchange,
                    Action: "CLOSE",
                    Success: success,
                    Detail: success
                        ? $"Close {request.Exchange} ok: ticket={request.Ticket} row={rowIndex} rowCount={rowCount}"
                        : $"Close {request.Exchange} failed: ticket={request.Ticket} row={rowIndex} rowCount={rowCount} error=close_position_mt5 source=Mt5TradeExecutor");
            }
            finally
            {
                if (ctx != IntPtr.Zero)
                {
                    NativeMethodsMt5.DestroyContext(ctx);
                }
            }
        }
        catch (Exception ex)
        {
            SafeLog($"[MT5][ERROR] Close leg {request.Exchange} ticket={request.Ticket} threw: {ex}");
            return new ManualTradeLegResult(
                Exchange: request.Exchange,
                Action: "CLOSE",
                Success: false,
                Detail: $"Close {request.Exchange} failed: ticket={request.Ticket}, error={ex.Message}");
        }
        finally
        {
            _actionGate.Release();
        }
    }

    public Task<ManualTradeResult> OpenPairAsync(TradeOpenPairRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return ExecutePairAsync(
            label: "OPEN_MANUAL",
            () => OpenLegAsync(request.LegA, cancellationToken),
            () => OpenLegAsync(request.LegB, cancellationToken));
    }

    public Task<ManualTradeResult> ClosePairAsync(TradeClosePairRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return ExecutePairAsync(
            label: "CLOSE_MANUAL",
            () => request.LegA is null
                ? Task.FromResult(new ManualTradeLegResult("A", "CLOSE", true, "Close A skipped: no open trade"))
                : CloseLegAsync(request.LegA, cancellationToken),
            () => request.LegB is null
                ? Task.FromResult(new ManualTradeLegResult("B", "CLOSE", true, "Close B skipped: no open trade"))
                : CloseLegAsync(request.LegB, cancellationToken));
    }

    private static async Task<ManualTradeResult> ExecutePairAsync(
        string label,
        Func<Task<ManualTradeLegResult>> legATaskFactory,
        Func<Task<ManualTradeLegResult>> legBTaskFactory)
    {
        var legATask = legATaskFactory();
        var legBTask = legBTaskFactory();
        var legs = await Task.WhenAll(legATask, legBTask);

        return new ManualTradeResult(
            Label: label,
            Success: legs.All(x => x.Success),
            Legs: legs);
    }

    private static bool TryParseHwnd(string raw, out ulong hwnd)
    {
        hwnd = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hwnd);
        }

        return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out hwnd);
    }

    private static bool IsValidWindow(ulong hwnd)
    {
        try
        {
            return NativeMethodsMt5.IsValidWindow(hwnd) == 1;
        }
        catch
        {
            return false;
        }
    }

    private void SafeLog(string message)
    {
        try
        {
            _logger.Log(message);
        }
        catch
        {
            // ignored by design
        }
    }
}