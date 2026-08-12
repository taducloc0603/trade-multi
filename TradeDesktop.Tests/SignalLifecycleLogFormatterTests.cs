using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class SignalLifecycleLogFormatterTests
{
    [Theory]
    [InlineData("SIGNAL_OPEN", "Open", "Detected")]
    [InlineData("SIGNAL_HEDGE_CONFIRMED", "Hedge", "Confirmed")]
    [InlineData("SIGNAL_CLOSE_BLOCKED", "Close", "Blocked")]
    [InlineData("SIGNAL_OPEN_CANCELLED", "Open", "Blocked")]
    [InlineData("SIGNAL_CLOSE_FAILED", "Close", "Failed")]
    public void Create_ClassifiesCategoryAndOutcome(string eventType, string category, string outcome)
    {
        var item = SignalLifecycleLogFormatter.Create(eventType, "Mô tả", SignalLogLevel.Info);

        Assert.Equal(category, item.Category);
        Assert.Equal(outcome, item.Outcome);
    }

    [Fact]
    public void Create_IncludesVietnameseDescriptionAndStructuredFields()
    {
        var item = SignalLifecycleLogFormatter.Create(
            "SIGNAL_HEDGE_BLOCKED",
            "Không thể mở Hedge Buy vì quota Buy đã đầy",
            SignalLogLevel.Warn,
            ("signalId", "SG-203"),
            ("reasonCode", "QUOTA_BUY_FULL"));

        var text = item.ToString();
        Assert.Contains("[SIGNAL_HEDGE_BLOCKED][WARN]", text);
        Assert.Contains("description=\"Không thể mở Hedge Buy vì quota Buy đã đầy\"", text);
        Assert.Contains("signalId=SG-203", text);
        Assert.Contains("reasonCode=QUOTA_BUY_FULL", text);
    }

    [Theory]
    [InlineData("QUOTA_BUY_FULL", "quota Buy đã đầy")]
    [InlineData("ORIGINAL_POSITION_CLOSED", "vị thế gốc đã đóng")]
    [InlineData("PARTIAL_CLOSE", "một chân vẫn còn mở")]
    public void DescriptionForReason_ReturnsReadableVietnamese(string reasonCode, string expected)
    {
        Assert.Contains(expected, SignalLifecycleLogFormatter.DescriptionForReason(reasonCode));
    }
}
