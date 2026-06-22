using System.Collections.Generic;
using System.Linq;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class HwndHealthCheckerTests
{
    // Probe giả: mọi handle parse được đều coi như cửa sổ tồn tại, trừ tập "missing".
    private sealed class FakeWindowProbe : IWindowProbe
    {
        private readonly HashSet<ulong> _missing;
        public FakeWindowProbe(params ulong[] missing) => _missing = new HashSet<ulong>(missing);
        public bool WindowExists(ulong hwnd) => !_missing.Contains(hwnd);
    }

    private static ManualHwndColumnConfig Col(string a, string ta, string b, string tb) => new(a, ta, b, tb);

    [Fact]
    public void Check_AllValid_ReturnsNoIssues()
    {
        var checker = new HwndHealthChecker(new FakeWindowProbe());
        var columns = new[] { Col("0x10", "0x20", "0x30", "0x40") };

        var issues = checker.Check(columns);

        Assert.Empty(issues);
    }

    [Fact]
    public void Check_DecimalAndHexFormats_BothAccepted()
    {
        var checker = new HwndHealthChecker(new FakeWindowProbe());
        var columns = new[] { Col("4096", "0xABCD", "12345", "0x1") };

        var issues = checker.Check(columns);

        Assert.Empty(issues);
    }

    [Fact]
    public void Check_BadFormat_ReportsBadFormat()
    {
        var checker = new HwndHealthChecker(new FakeWindowProbe());
        var columns = new[] { Col("not-a-number", "0x20", "0x30", "0x40") };

        var issues = checker.Check(columns);

        var issue = Assert.Single(issues);
        Assert.Equal(HwndIssueKind.BadFormat, issue.Kind);
        Assert.Equal("Cột 1 - Chart A", issue.Label);
        Assert.Equal("not-a-number", issue.Value);
    }

    [Fact]
    public void Check_EmptyFieldInUsedColumn_ReportsEmpty()
    {
        var checker = new HwndHealthChecker(new FakeWindowProbe());
        var columns = new[] { Col("0x10", "", "0x30", "0x40") };

        var issues = checker.Check(columns);

        var issue = Assert.Single(issues);
        Assert.Equal(HwndIssueKind.Empty, issue.Kind);
        Assert.Equal("Cột 1 - Trade A", issue.Label);
    }

    [Fact]
    public void Check_FullyEmptyColumn_IsSkipped()
    {
        var checker = new HwndHealthChecker(new FakeWindowProbe());
        var columns = new[] { Col("", "", "", "") };

        var issues = checker.Check(columns);

        Assert.Empty(issues);
    }

    [Fact]
    public void Check_WindowMissing_ReportsWindowMissing()
    {
        // 0x30 (=48) coi như cửa sổ không tồn tại.
        var checker = new HwndHealthChecker(new FakeWindowProbe(0x30));
        var columns = new[] { Col("0x10", "0x20", "0x30", "0x40") };

        var issues = checker.Check(columns);

        var issue = Assert.Single(issues);
        Assert.Equal(HwndIssueKind.WindowMissing, issue.Kind);
        Assert.Equal("Cột 1 - Chart B", issue.Label);
        Assert.Equal("0x30", issue.Value);
    }

    [Fact]
    public void Check_MultipleColumns_LabelsByIndex()
    {
        var checker = new HwndHealthChecker(new FakeWindowProbe());
        var columns = new[]
        {
            Col("0x10", "0x20", "0x30", "0x40"),   // hợp lệ
            Col("bad", "0x21", "0x31", "0x41")     // cột 2 chart A lỗi
        };

        var issues = checker.Check(columns);

        var issue = Assert.Single(issues);
        Assert.Equal("Cột 2 - Chart A", issue.Label);
        Assert.Equal(HwndIssueKind.BadFormat, issue.Kind);
    }

    [Fact]
    public void Check_NullOrEmptyColumns_ReturnsNoIssues()
    {
        var checker = new HwndHealthChecker(new FakeWindowProbe());

        Assert.Empty(checker.Check(null));
        Assert.Empty(checker.Check(new List<ManualHwndColumnConfig>()));
    }
}
