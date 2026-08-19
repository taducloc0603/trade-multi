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
        Assert.EndsWith("description=\"Không thể mở Hedge Buy vì quota Buy đã đầy\"", text);
        Assert.True(text.IndexOf("reasonCode=QUOTA_BUY_FULL", StringComparison.Ordinal)
                    < text.IndexOf("description=", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_OmitsFieldsWithoutData()
    {
        var item = SignalLifecycleLogFormatter.Create(
            "SIGNAL_OPEN",
            "Phát hiện tín hiệu mở vị thế mới",
            SignalLogLevel.Info,
            ("signalId", "SG-204"),
            ("originalSlot", null));

        var text = item.ToString();
        Assert.Contains("signalId=SG-204", text);
        Assert.DoesNotContain("originalSlot", text);
    }

    [Theory]
    [InlineData("QUOTA_BUY_FULL", "quota Buy đã đầy")]
    [InlineData("ORIGINAL_POSITION_CLOSED", "vị thế gốc đã đóng")]
    [InlineData("PARTIAL_CLOSE", "một chân vẫn còn mở")]
    [InlineData("POST_CLOSE_OPEN_LOCK", "khóa sau khi đóng")]
    [InlineData("SAME_SIDE_OPEN_RANDOM_LOCK", "vị thế cùng chiều")]
    [InlineData("OPEN_TO_CLOSE_RANDOM_LOCK", "sau Auto Open gần nhất")]
    [InlineData("GLOBAL_ACTION_COOLDOWN", "cooldown toàn cục")]
    public void DescriptionForReason_ReturnsReadableVietnamese(string reasonCode, string expected)
    {
        Assert.Contains(expected, SignalLifecycleLogFormatter.DescriptionForReason(reasonCode));
    }
}
