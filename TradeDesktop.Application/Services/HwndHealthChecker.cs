using System.Globalization;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

/// <summary>
/// Loại lỗi của một giá trị HWND khi kiểm tra sức khỏe trước/trong khi chạy Auto.
/// </summary>
public enum HwndIssueKind
{
    /// <summary>Field để trống trong một cột đang được dùng.</summary>
    Empty,

    /// <summary>Không parse được sang ulong (sai định dạng hex 0x.. / decimal).</summary>
    BadFormat,

    /// <summary>Parse được nhưng cửa sổ không còn tồn tại (native mt_is_valid_window = 0).</summary>
    WindowMissing
}

/// <summary>Một handle HWND bị lỗi, kèm label hiển thị + giá trị gốc.</summary>
public sealed record HwndIssue(string Label, string Value, HwndIssueKind Kind);

/// <summary>
/// Probe kiểm tra một HWND có còn ứng với cửa sổ tồn tại hay không.
/// Tách ra interface để unit-test được trên môi trường không có native DLL (macOS).
/// Impl thật (<c>NativeWindowProbe</c>) nằm ở App layer vì cần native P/Invoke.
/// </summary>
public interface IWindowProbe
{
    bool WindowExists(ulong hwnd);
}

/// <summary>Kiểm tra toàn bộ HWND trong config có hợp lệ (đúng định dạng + cửa sổ còn tồn tại).</summary>
public interface IHwndHealthChecker
{
    /// <summary>Duyệt mọi cột, mọi handle (Chart A/Trade A/Chart B/Trade B) → trả về các handle lỗi.</summary>
    IReadOnlyList<HwndIssue> Check(IReadOnlyList<ManualHwndColumnConfig>? columns);
}

public sealed class HwndHealthChecker : IHwndHealthChecker
{
    private readonly IWindowProbe _probe;

    public HwndHealthChecker(IWindowProbe probe)
    {
        _probe = probe;
    }

    public IReadOnlyList<HwndIssue> Check(IReadOnlyList<ManualHwndColumnConfig>? columns)
    {
        var issues = new List<HwndIssue>();
        if (columns is null || columns.Count == 0)
        {
            return issues;
        }

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (column is null)
            {
                continue;
            }

            // Cột trống hoàn toàn → bỏ qua (không coi là lỗi).
            if (!HasText(column.ChartHwndA)
                && !HasText(column.TradeHwndA)
                && !HasText(column.ChartHwndB)
                && !HasText(column.TradeHwndB))
            {
                continue;
            }

            var displayIndex = i + 1;
            Inspect(issues, $"Cột {displayIndex} - Chart A", column.ChartHwndA);
            Inspect(issues, $"Cột {displayIndex} - Trade A", column.TradeHwndA);
            Inspect(issues, $"Cột {displayIndex} - Chart B", column.ChartHwndB);
            Inspect(issues, $"Cột {displayIndex} - Trade B", column.TradeHwndB);
        }

        return issues;
    }

    private void Inspect(List<HwndIssue> issues, string label, string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            issues.Add(new HwndIssue(label, string.Empty, HwndIssueKind.Empty));
            return;
        }

        if (!TryParseHwnd(raw, out var hwnd))
        {
            issues.Add(new HwndIssue(label, raw, HwndIssueKind.BadFormat));
            return;
        }

        if (!_probe.WindowExists(hwnd))
        {
            issues.Add(new HwndIssue(label, raw, HwndIssueKind.WindowMissing));
        }
    }

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    // Cùng logic parse với Mt4/Mt5TradeExecutor (hex 0x.. hoặc decimal).
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
}
