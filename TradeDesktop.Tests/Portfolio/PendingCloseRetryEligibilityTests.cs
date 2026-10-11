using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

public sealed class PendingCloseRetryEligibilityTests
{
    [Theory]
    // MT4/MT5 (dùng HWND): hành vi cũ — thiếu HWND thì không retry.
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, false, true, false)]
    // cTrader / PrimeXBT (không dùng HWND): retry được dù HWND trống.
    [InlineData(true, true, false, false, true)]
    [InlineData(true, true, true, false, true)]
    // Thiếu map hoặc platform: không bao giờ retry.
    [InlineData(false, true, true, true, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, false, false, false, false)]
    public void CanRetry(bool hasTradeMap, bool hasPlatform, bool hasTradeHwnd, bool platformUsesHwnd, bool expected)
    {
        Assert.Equal(expected, PendingCloseRetryEligibility.CanRetry(hasTradeMap, hasPlatform, hasTradeHwnd, platformUsesHwnd));
    }
}
