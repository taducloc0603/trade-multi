namespace TradeDesktop.Application.Models;

public sealed record ManualHwndColumnConfig(
    string ChartHwndA,
    string TradeHwndA,
    string ChartHwndB,
    string TradeHwndB)
{
    public static ManualHwndColumnConfig Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ChartHwndA) &&
        !string.IsNullOrWhiteSpace(TradeHwndA) &&
        !string.IsNullOrWhiteSpace(ChartHwndB) &&
        !string.IsNullOrWhiteSpace(TradeHwndB);

    // Sàn B là cTrader thì không có cửa sổ để click → HWND B không bắt buộc. IsComplete giữ nguyên cho đường MT.
    public bool IsCompleteFor(bool requiresExchangeBHwnd) =>
        requiresExchangeBHwnd
            ? IsComplete
            : !string.IsNullOrWhiteSpace(ChartHwndA) && !string.IsNullOrWhiteSpace(TradeHwndA);

    public ManualHwndColumnConfig Normalize()
        => new(
            (ChartHwndA ?? string.Empty).Trim(),
            (TradeHwndA ?? string.Empty).Trim(),
            (ChartHwndB ?? string.Empty).Trim(),
            (TradeHwndB ?? string.Empty).Trim());
}