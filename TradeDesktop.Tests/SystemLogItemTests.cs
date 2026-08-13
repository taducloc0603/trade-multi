using TradeDesktop.Application.Models;

namespace TradeDesktop.Tests;

public sealed class SystemLogItemTests
{
    [Theory]
    [InlineData("[SLOT][INFO] allocated", "SLOT", "SLOT", "Trading")]
    [InlineData("[SLOT][TP_CHECK] waiting", "SLOT", "TP_CHECK", "Trading")]
    [InlineData("[MT5][ERROR] open failed", "MT5", "MT5", "Execution")]
    [InlineData("[MIN_PROFIT][WAITING] not ready", "MIN_PROFIT", "WAITING", "Trading")]
    [InlineData("[MMF_TRADES][WARN] unavailable", "MMF_TRADES", "MMF_TRADES", "Market")]
    [InlineData("plain message", "GENERAL", "GENERAL", "Application")]
    public void Parse_ClassifiesCategoryEventAndDomain(
        string message,
        string category,
        string eventType,
        string domain)
    {
        var item = SystemLogItem.Parse(DateTime.Now, message, SystemLogSeverity.Info);

        Assert.Equal(category, item.Category);
        Assert.Equal(eventType, item.EventType);
        Assert.Equal(domain, item.Domain);
    }

    [Fact]
    public void Parse_ClassifiesStructuredSignalForSystemFilter()
    {
        var item = SystemLogItem.Parse(
            DateTime.Now,
            "[2026-08-13 10:30:01.123] [SIGNAL_HEDGE_BLOCKED][WARN] signalId=abc description=blocked",
            SystemLogSeverity.Warn);

        Assert.True(item.IsSignal);
        Assert.Equal("Signal", item.Domain);
        Assert.Equal("SIGNAL_HEDGE_BLOCKED", item.Category);
        Assert.Equal("SIGNAL_HEDGE_BLOCKED", item.EventType);
        Assert.Equal(
            "[2026-08-13 10:30:01.123] [SIGNAL_HEDGE_BLOCKED][WARN] signalId=abc description=blocked",
            item.DisplayText);
    }
}
