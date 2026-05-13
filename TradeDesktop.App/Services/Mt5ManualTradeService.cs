using System.Globalization;
using TradeDesktop.App.Native;

namespace TradeDesktop.App.Services;

public sealed class Mt5ManualTradeService : IMt5ManualTradeService
{
    private readonly SemaphoreSlim _actionGate = new(1, 1);

    public Task<ManualTradeResult> ExecuteBuyAsync(string chartHwndA, string chartHwndB, CancellationToken cancellationToken = default)
        => ExecuteOpenAsync(
            label: "OPEN_MANUAL",
            chartHwndA,
            chartHwndB,
            actionA: "BUY",
            actionB: "SELL",
            clickA: NativeMethodsMt5.ClickBuy,
            clickB: NativeMethodsMt5.ClickSell,
            cancellationToken);

    public Task<ManualTradeResult> ExecuteSellAsync(string chartHwndA, string chartHwndB, CancellationToken cancellationToken = default)
        => ExecuteOpenAsync(
            label: "OPEN_MANUAL",
            chartHwndA,
            chartHwndB,
            actionA: "SELL",
            actionB: "BUY",
            clickA: NativeMethodsMt5.ClickSell,
            clickB: NativeMethodsMt5.ClickBuy,
            cancellationToken);

    public async Task<ManualTradeResult> ExecuteCloseAsync(ManualCloseRequest? closeA, ManualCloseRequest? closeB, CancellationToken cancellationToken = default)
    {
        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            var legATask = CloseFirstTradeAsync("A", closeA, cancellationToken);
            var legBTask = CloseFirstTradeAsync("B", closeB, cancellationToken);
            var legs = await Task.WhenAll(legATask, legBTask);

            return new ManualTradeResult(
                Label: "CLOSE_MANUAL",
                Success: legs.All(l => l.Success),
                Legs: legs);
        }
        catch (Exception ex)
        {
            return new ManualTradeResult(
                Label: "CLOSE_MANUAL",
                Success: false,
                Legs: [],
                ErrorMessage: $"Lỗi close manual: {ex.Message}");
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<ManualTradeResult> ExecuteOpenAsync(
        string label,
        string chartHwndA,
        string chartHwndB,
        string actionA,
        string actionB,
        Func<ulong, int> clickA,
        Func<ulong, int> clickB,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken);
        try
        {
            if (!TryParseHwnd(chartHwndA, out var hwndA) || !TryParseHwnd(chartHwndB, out var hwndB))
            {
                return new ManualTradeResult(
                    Label: label,
                    Success: false,
                    Legs: [],
                    ErrorMessage: "HWND CHART không hợp lệ. Vui lòng kiểm tra lại Config (định dạng 0x... hoặc số thập phân).");
            }

            if (!IsValidWindow(hwndA) || !IsValidWindow(hwndB))
            {
                return new ManualTradeResult(
                    Label: label,
                    Success: false,
                    Legs: [],
                    ErrorMessage: "Một trong các CHART HWND không còn hợp lệ.");
            }

            var legATask = Task.Run(() => ExecuteOpenLeg("A", actionA, hwndA, clickA), cancellationToken);
            var legBTask = Task.Run(() => ExecuteOpenLeg("B", actionB, hwndB, clickB), cancellationToken);
            var legs = await Task.WhenAll(legATask, legBTask);

            return new ManualTradeResult(
                Label: label,
                Success: legs.All(l => l.Success),
                Legs: legs);
        }
        catch (Exception ex)
        {
            return new ManualTradeResult(
                Label: label,
                Success: false,
                Legs: [],
                ErrorMessage: $"Lỗi open manual: {ex.Message}");
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private static ManualTradeLegResult ExecuteOpenLeg(string exchange, string action, ulong chartHwnd, Func<ulong, int> click)
    {
        try
        {
            var ok = click(chartHwnd) == 1;
            return new ManualTradeLegResult(
                Exchange: exchange,
                Action: action,
                Success: ok,
                Detail: ok ? "clicked" : "click failed");
        }
        catch (Exception ex)
        {
            return new ManualTradeLegResult(exchange, action, false, ex.Message);
        }
    }

    private static Task<ManualTradeLegResult> CloseFirstTradeAsync(string exchangeLabel, ManualCloseRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Task.FromResult(new ManualTradeLegResult(
                Exchange: exchangeLabel,
                Action: "CLOSE",
                Success: true,
                Detail: $"Close {exchangeLabel} skipped: no open trade"));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryParseHwnd(request.TradeHwnd, out var tradeParentHwnd))
        {
            return Task.FromResult(new ManualTradeLegResult(
                Exchange: request.Exchange,
                Action: "CLOSE",
                Success: false,
                Detail: $"Close {request.Exchange} failed: ticket={request.Ticket}, error=TRADE HWND không hợp lệ"));
        }

        if (!IsValidWindow(tradeParentHwnd))
        {
            return Task.FromResult(new ManualTradeLegResult(
                Exchange: request.Exchange,
                Action: "CLOSE",
                Success: false,
                Detail: $"Close {request.Exchange} failed: ticket={request.Ticket}, error=TRADE HWND không còn hợp lệ"));
        }

        IntPtr ctx = IntPtr.Zero;
        try
        {
            ctx = NativeMethodsMt5.CreateContextFromParent(tradeParentHwnd);
            if (ctx == IntPtr.Zero)
            {
                return Task.FromResult(new ManualTradeLegResult(
                    Exchange: request.Exchange,
                    Action: "CLOSE",
                    Success: false,
                    Detail: $"Close {request.Exchange} failed: ticket={request.Ticket}, error=create_context_from_parent failed"));
            }

            var rowCount = NativeMethodsMt5.UpdateRowCount(ctx);
            if (rowCount <= 0)
            {
                return Task.FromResult(new ManualTradeLegResult(
                    Exchange: request.Exchange,
                    Action: "CLOSE",
                    Success: true,
                    Detail: $"Close {request.Exchange} skipped: no open trade (ticket={request.Ticket})"));
            }

            if (request.RowIndex is null)
            {
                return Task.FromResult(new ManualTradeLegResult(
                    Exchange: request.Exchange,
                    Action: "CLOSE",
                    Success: false,
                    Detail: $"Close {request.Exchange} skipped: ticket={request.Ticket} row=unresolved rowCount={rowCount} source=Mt5ManualTradeService"));
            }

            var rowIndex = Math.Max(0, Math.Min(request.RowIndex.Value, rowCount - 1));
            var closeResult = NativeMethodsMt5.ClosePositionMt5(ctx, rowIndex);
            var success = closeResult == 1;
            return Task.FromResult(new ManualTradeLegResult(
                Exchange: request.Exchange,
                Action: "CLOSE",
                Success: success,
                Detail: success
                    ? $"Close {request.Exchange} ok: ticket={request.Ticket} row={rowIndex} rowCount={rowCount}"
                    : $"Close {request.Exchange} failed: ticket={request.Ticket} row={rowIndex} rowCount={rowCount} error=close_position_mt5 source=Mt5ManualTradeService"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ManualTradeLegResult(
                Exchange: request.Exchange,
                Action: "CLOSE",
                Success: false,
                Detail: $"Close {request.Exchange} failed: ticket={request.Ticket}, error={ex.Message}"));
        }
        finally
        {
            if (ctx != IntPtr.Zero)
            {
                NativeMethodsMt5.DestroyContext(ctx);
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
            return NativeMethodsMt5.IsValidWindow(hwnd) == 1;
        }
        catch
        {
            return false;
        }
    }
}
