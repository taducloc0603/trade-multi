using System.Globalization;
using System.Linq;

namespace TradeDesktop.Application.Services;

/// <summary>
/// Centralized, stateless formatter for all signal log lines.
/// Manual and Auto use separate methods but share the same timestamp format.
/// </summary>
public static class SignalLogFormatter
{
    private const string TimestampFormat = "yyyy.MM.dd HH:mm:ss.ffffff";

    /// <summary>
    /// Format: "Time> [slot:X]. TYPE SYMBOL at PRICE by Manual | Gap=N pt."
    /// </summary>
    public static string FormatManualOpen(
        DateTime localTime, int slot, string exchangeLabel,
        string tradeType, string symbol, decimal? price, int? gap,
        string? spreadText = null)
    {
        var ts = localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var gapText = gap.HasValue ? gap.Value.ToString(CultureInfo.InvariantCulture) : "0";
        var suffix = string.IsNullOrWhiteSpace(spreadText) ? string.Empty : $" {spreadText}";
        return $"{ts}> [{slot}:{exchangeLabel}]. {tradeType.ToUpperInvariant()} {symbol} at {Fp(price)} by Manual | Gap={gapText} pt.{suffix}";
    }

    /// <summary>
    /// Format: "Time> [slot:X]. TYPE SYMBOL at PRICE by Gap BUY/SELL=N pt (allGap)."
    /// </summary>
    public static string FormatAutoOpen(
        DateTime localTime, int slot, string exchangeLabel,
        string tradeType, string symbol, decimal? price,
        string gapLabel, int? lastGap, IReadOnlyList<int>? allGaps,
        string? spreadText = null)
    {
        var ts = localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var lastGapText = lastGap.HasValue ? lastGap.Value.ToString(CultureInfo.InvariantCulture) : "0";
        var suffix = string.IsNullOrWhiteSpace(spreadText) ? string.Empty : $" {spreadText}";
        return $"{ts}> [{slot}:{exchangeLabel}]. {tradeType.ToUpperInvariant()} {symbol} at {Fp(price)} by {gapLabel}={lastGapText} pt ({Fg(allGaps)}).{suffix}";
    }

    /// <summary>
    /// Format: "Time> [slot:X]. CLOSE TYPE SYMBOL at PRICE. Reason: Close by Manual"
    /// </summary>
    public static string FormatManualClose(
        DateTime localTime, int slot, string exchangeLabel,
        string tradeType, string symbol, decimal? price)
    {
        var ts = localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return $"{ts}> [{slot}:{exchangeLabel}]. CLOSE {tradeType.ToUpperInvariant()} {symbol} at {Fp(price)}. Reason: Close by Manual";
    }

    /// <summary>
    /// Format: "Time> [slot:X]. CLOSE TYPE SYMBOL at PRICE. Reason: Close by Gap BUY/SELL = lastGap (allGap)"
    /// </summary>
    public static string FormatAutoClose(
        DateTime localTime, int slot, string exchangeLabel,
        string tradeType, string symbol, decimal? price,
        string gapLabel, int? lastGap, IReadOnlyList<int>? allGaps)
    {
        var ts = localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var lastGapText = lastGap.HasValue ? lastGap.Value.ToString(CultureInfo.InvariantCulture) : "0";
        return $"{ts}> [{slot}:{exchangeLabel}]. CLOSE {tradeType.ToUpperInvariant()} {symbol} at {Fp(price)}. Reason: Close by {gapLabel} = {lastGapText} ({Fg(allGaps)})";
    }

    /// <summary>
    /// Format: "Time> [slot:X]. OPEN Type SYMBOL. Price P. Slippage=S pt. Execution=E ms."
    /// </summary>
    public static string FormatOpenConfirm(
        DateTime localTime, int slot, string exchangeLabel,
        string tradeType, string symbol,
        double actualPrice, double? slippage, long? executionMs)
    {
        var ts = localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return $"{ts}> [{slot}:{exchangeLabel}]. OPEN {Cap(tradeType)} {symbol}. Price {actualPrice.ToString("0.#####", CultureInfo.InvariantCulture)}. Slippage={Fs(slippage)} pt. Execution={Fe(executionMs)} ms.";
    }

    /// <summary>
    /// Format: "Time> [slot:X]. CLOSE Type SYMBOL. Price P. Slippage=S pt. Execution=E ms."
    /// </summary>
    public static string FormatCloseConfirm(
        DateTime localTime, int slot, string exchangeLabel,
        string tradeType, string symbol,
        double actualPrice, double? slippage, long? executionMs)
    {
        var ts = localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return $"{ts}> [{slot}:{exchangeLabel}]. CLOSE {Cap(tradeType)} {symbol}. Price {actualPrice.ToString("0.#####", CultureInfo.InvariantCulture)}. Slippage={Fs(slippage)} pt. Execution={Fe(executionMs)} ms.";
    }

    public static string FormatRandomHoldingTime(DateTime localTime, int holdingSeconds)
    {
        var ts = localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return $"{ts}> Random holding time {holdingSeconds}s";
    }

    public static string FormatRandomWaitingTime(DateTime localTime, int waitingSeconds)
    {
        var ts = localTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return $"{ts}> Random waiting time {waitingSeconds}s";
    }

    /// <summary>Open Buy → Ask, Open Sell → Bid</summary>
    public static decimal? ResolveOpenPrice(decimal? bid, decimal? ask, bool isBuy)
        => isBuy ? ask : bid;

    /// <summary>Close Buy position → Bid, Close Sell position → Ask</summary>
    public static decimal? ResolveClosePrice(decimal? bid, decimal? ask, bool isBuyPosition)
        => isBuyPosition ? bid : ask;

    public static string TradeTypeString(int tradeType) => tradeType == 0 ? "BUY" : "SELL";

    private static string Fp(decimal? price)
        => price.HasValue ? price.Value.ToString("0.#####", CultureInfo.InvariantCulture) : "-";

    private static string Fs(double? slippage)
        => slippage.HasValue ? slippage.Value.ToString("0", CultureInfo.InvariantCulture) : "-";

    private static string Fe(long? ms)
        => ms.HasValue ? ms.Value.ToString(CultureInfo.InvariantCulture) : "-";

    private static string Fg(IReadOnlyList<int>? gaps)
        => gaps is null || gaps.Count == 0
            ? "0"
            : string.Join("|", gaps.Select(g => g.ToString(CultureInfo.InvariantCulture)));

    private static string Cap(string tradeType)
    {
        if (string.IsNullOrWhiteSpace(tradeType))
        {
            return tradeType;
        }

        var s = tradeType.ToLowerInvariant();
        return char.ToUpperInvariant(s[0]) + s[1..];
    }
}