using System.Globalization;
using TradeDesktop.App.Native;

namespace TradeDesktop.App.Services;

public sealed class Mt4TradeExecutor : ITradePlatformExecutor
{
    private readonly ITradeSessionFileLogger _logger;
    private readonly SemaphoreSlim _actionGate = new(1, 1);

    public Mt4TradeExecutor(ITradeSessionFileLogger logger)
    {
        _logger = logger;
    }

    public TradeLegPlatform Platform => TradeLegPlatform.Mt4;

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
                SafeLog($"[MT4][WARN] Open leg {request.Exchange} failed: invalid chart hwnd={request.ChartHwnd}");
                return new ManualTradeLegResult(
                    Exchange: request.Exchange,
                    Action: request.Action.ToString().ToUpperInvariant(),
                    Success: false,
                    Detail: $"Open {request.Exchange} failed: HWND CHART không hợp lệ");
            }

            if (!IsValidWindow(chartHwnd))
            {
                SafeLog($"[MT4][WARN] Open leg {request.Exchange} failed: chart hwnd not valid ({request.ChartHwnd})");
                return new ManualTradeLegResult(
                    Exchange: request.Exchange,
                    Action: request.Action.ToString().ToUpperInvariant(),
                    Success: false,
                    Detail: $"Open {request.Exchange} failed: CHART HWND không còn hợp lệ");
            }

            return ExecuteOpenLeg(request.Exchange, request.Action, chartHwnd);
        }
        catch (Exception ex)
        {
            SafeLog($"[MT4][ERROR] Open leg {request.Exchange} threw: {ex}");
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
            return CloseFirstTrade(request);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    public async Task<ManualTradeResult> OpenPairAsync(TradeOpenPairRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            if (!TryParseHwnd(request.LegA.ChartHwnd, out var hwndA) || !TryParseHwnd(request.LegB.ChartHwnd, out var hwndB))
            {
                return new ManualTradeResult(
                    Label: "OPEN_MANUAL",
                    Success: false,
                    Legs: [],
                    ErrorMessage: "HWND CHART không hợp lệ. Vui lòng kiểm tra lại Config (định dạng 0x... hoặc số thập phân).");
            }

            if (!IsValidWindow(hwndA) || !IsValidWindow(hwndB))
            {
                return new ManualTradeResult(
                    Label: "OPEN_MANUAL",
                    Success: false,
                    Legs: [],
                    ErrorMessage: "Một trong các CHART HWND không còn hợp lệ.");
            }

            var legATask = Task.Run(() => ExecuteOpenLeg(request.LegA.Exchange, request.LegA.Action, hwndA), cancellationToken);
            var legBTask = Task.Run(() => ExecuteOpenLeg(request.LegB.Exchange, request.LegB.Action, hwndB), cancellationToken);
            var legs = await Task.WhenAll(legATask, legBTask);

            return new ManualTradeResult(
                Label: "OPEN_MANUAL",
                Success: legs.All(l => l.Success),
                Legs: legs);
        }
        catch (Exception ex)
        {
            SafeLog($"[MT4][ERROR] Open pair threw: {ex}");
            return new ManualTradeResult(
                Label: "OPEN_MANUAL",
                Success: false,
                Legs: [],
                ErrorMessage: $"Lỗi open manual MT4: {ex.Message}");
        }
        finally
        {
            _actionGate.Release();
        }
    }

    public async Task<ManualTradeResult> ClosePairAsync(TradeClosePairRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            var legATask = Task.Run(() => CloseFirstTrade(request.LegA), cancellationToken);
            var legBTask = Task.Run(() => CloseFirstTrade(request.LegB), cancellationToken);
            var legs = await Task.WhenAll(legATask, legBTask);

            return new ManualTradeResult(
                Label: "CLOSE_MANUAL",
                Success: legs.All(l => l.Success),
                Legs: legs);
        }
        catch (Exception ex)
        {
            SafeLog($"[MT4][ERROR] Close pair threw: {ex}");
            return new ManualTradeResult(
                Label: "CLOSE_MANUAL",
                Success: false,
                Legs: [],
                ErrorMessage: $"Lỗi close manual MT4: {ex.Message}");
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private ManualTradeLegResult ExecuteOpenLeg(string exchange, TradeLegAction action, ulong chartHwnd)
    {
        var actionText = action.ToString().ToUpperInvariant();
        try
        {
            var clickResult = action switch
            {
                TradeLegAction.Buy => NativeMethodsMt4.ClickBuy(chartHwnd),
                TradeLegAction.Sell => NativeMethodsMt4.ClickSell(chartHwnd),
                _ => 0
            };

            if (action is not (TradeLegAction.Buy or TradeLegAction.Sell))
            {
                SafeLog($"[MT4][WARN] Open leg {exchange} failed: unsupported action={action}");
                return new ManualTradeLegResult(exchange, actionText, false, "Unsupported MT4 open action");
            }

            var success = clickResult == 1;
            SafeLog($"[MT4][{(success ? "INFO" : "WARN")}] Open leg {exchange} action={actionText} result={(success ? "ok" : "failed")}");
            return new ManualTradeLegResult(
                Exchange: exchange,
                Action: actionText,
                Success: success,
                Detail: success ? "clicked" : "click failed");
        }
        catch (Exception ex)
        {
            SafeLog($"[MT4][ERROR] Open leg {exchange} action={actionText} threw: {ex}");
            return new ManualTradeLegResult(exchange, actionText, false, ex.Message);
        }
    }

    private ManualTradeLegResult CloseFirstTrade(TradeCloseLegRequest? request)
    {
        if (request is null)
        {
            SafeLog("[MT4][INFO] Close leg skipped: no close request");
            return new ManualTradeLegResult(
                Exchange: "UNKNOWN",
                Action: "CLOSE",
                Success: true,
                Detail: "Close skipped: no open trade");
        }

        if (!TryParseHwnd(request.TradeHwnd, out var tradeParentHwnd))
        {
            SafeLog($"[MT4][WARN] Close leg {request.Exchange} ticket={request.Ticket} failed: invalid trade hwnd={request.TradeHwnd}");
            return new ManualTradeLegResult(
                Exchange: request.Exchange,
                Action: "CLOSE",
                Success: false,
                Detail: $"Close {request.Exchange} failed: ticket={request.Ticket}, error=TRADE HWND không hợp lệ");
        }

        if (!IsValidWindow(tradeParentHwnd))
        {
            SafeLog($"[MT4][WARN] Close leg {request.Exchange} ticket={request.Ticket} failed: trade hwnd not valid ({request.TradeHwnd})");
            return new ManualTradeLegResult(
                Exchange: request.Exchange,
                Action: "CLOSE",
                Success: false,
                Detail: $"Close {request.Exchange} failed: ticket={request.Ticket}, error=TRADE HWND không còn hợp lệ");
        }

        IntPtr context = IntPtr.Zero;
        try
        {
            context = NativeMethodsMt4.CreateContextFromParent(tradeParentHwnd);
            if (context == IntPtr.Zero)
            {
                SafeLog($"[MT4][WARN] Close leg {request.Exchange} ticket={request.Ticket} failed: create_context_from_parent returned 0");
                return new ManualTradeLegResult(
                    Exchange: request.Exchange,
                    Action: "CLOSE",
                    Success: false,
                    Detail: $"Close {request.Exchange} failed: ticket={request.Ticket}, error=create_context_from_parent failed");
            }

            var rowCount = NativeMethodsMt4.UpdateRowCount(context);
            if (rowCount <= 0)
            {
                SafeLog($"[MT4][INFO] Close leg {request.Exchange} ticket={request.Ticket} skipped: no open trade");
                return new ManualTradeLegResult(
                    Exchange: request.Exchange,
                    Action: "CLOSE",
                    Success: true,
                    Detail: $"Close {request.Exchange} skipped: no open trade (ticket={request.Ticket})");
            }

            if (request.RowIndex is null)
            {
                SafeLog($"[MT4][WARN] Close leg {request.Exchange} ticket={request.Ticket} failed: unresolved rowIndex rowCount={rowCount}");
                return new ManualTradeLegResult(
                    Exchange: request.Exchange,
                    Action: "CLOSE",
                    Success: false,
                    Detail: $"Close {request.Exchange} skipped: ticket={request.Ticket} row=unresolved rowCount={rowCount} source=Mt4TradeExecutor");
            }

            var rowIndex = Math.Max(0, Math.Min(request.RowIndex.Value, rowCount - 1));
            var closeResult = NativeMethodsMt4.ClosePositionMt4(context, rowIndex);
            var success = closeResult == 1;
            SafeLog($"[MT4][{(success ? "INFO" : "WARN")}] Close leg {request.Exchange} ticket={request.Ticket} row={rowIndex} result={(success ? "ok" : "failed")}");
            return new ManualTradeLegResult(
                Exchange: request.Exchange,
                Action: "CLOSE",
                Success: success,
                Detail: success
                    ? $"Close {request.Exchange} ok: ticket={request.Ticket} row={rowIndex} rowCount={rowCount}"
                    : $"Close {request.Exchange} failed: ticket={request.Ticket} row={rowIndex} rowCount={rowCount} error=close_position_mt4 source=Mt4TradeExecutor");
        }
        catch (Exception ex)
        {
            SafeLog($"[MT4][ERROR] Close leg {request.Exchange} ticket={request.Ticket} threw: {ex}");
            return new ManualTradeLegResult(
                Exchange: request.Exchange,
                Action: "CLOSE",
                Success: false,
                Detail: $"Close {request.Exchange} failed: ticket={request.Ticket}, error={ex.Message}");
        }
        finally
        {
            if (context != IntPtr.Zero)
            {
                NativeMethodsMt4.DestroyContext(context);
            }
        }
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
            return NativeMethodsMt4.IsValidWindow(hwnd) == 1;
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