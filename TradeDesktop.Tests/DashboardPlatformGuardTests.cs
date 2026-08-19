using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class DashboardPlatformGuardTests
{
    [Fact]
    public void BothMt5_DoesNotRequireHwndConfiguration()
    {
        var result = TradePlatformHwndPolicy.HasRequiredConfiguration(
            [ManualHwndColumnConfig.Empty],
            "mt5",
            "mt5");

        Assert.True(result);
    }

    [Fact]
    public void MixedPlatform_RequiresOnlyMt4ExchangeHwnd()
    {
        var result = TradePlatformHwndPolicy.HasRequiredConfiguration(
            [new ManualHwndColumnConfig("0x10", "0x20", string.Empty, string.Empty)],
            "mt4",
            "mt5");

        Assert.True(result);
    }

    [Fact]
    public void Mt5HwndIssue_IsIgnored()
    {
        var issue = new HwndIssue("Cột 1 - Chart B", "0x30", HwndIssueKind.WindowMissing);

        var relevant = TradePlatformHwndPolicy.IsIssueRelevant(
            issue,
            "mt4",
            "mt5");

        Assert.False(relevant);
    }

    [Theory]
    [InlineData("Giá Bid/Ask sàn A đóng băng: configuredMs=1000", "PRICE_FREEZE_GUARD")]
    [InlineData("Gap và giá nguồn 10 mẫu cuối cùng đóng băng", "PRICE_FREEZE_GUARD")]
    public void VietnameseFreezeReason_UsesPriceFreezeCode(string reason, string expected)
    {
        Assert.Equal(expected, GuardReasonCodeResolver.Resolve(reason));
    }
}
