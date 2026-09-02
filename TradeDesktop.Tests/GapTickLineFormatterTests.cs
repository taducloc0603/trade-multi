using System.Globalization;
using TradeDesktop.Application.Services;
using TradeDesktop.Domain.Models;

namespace TradeDesktop.Tests;

public sealed class GapTickLineFormatterTests
{
    private static DashboardMetrics BuildMetrics(
        int? gapBuy = 12,
        int? gapSell = -3,
        decimal? aBid = 2412.35m,
        decimal? aAsk = 2412.55m,
        DateTime? timestampUtc = null)
    {
        var a = new ExchangeDashboardMetrics(
            Symbol: "XAUUSD",
            Bid: aBid,
            Ask: aAsk,
            Spread: 20m,
            LatencyMs: 8m,
            Tps: 5f,
            Time: "14:32:07.412",
            MaxLatMs: 30m,
            AvgLatMs: 10m,
            IsConnected: true,
            Error: null);
        var b = new ExchangeDashboardMetrics(
            Symbol: "XAUUSD.m",
            Bid: 2412.67m,
            Ask: 2412.88m,
            Spread: 21m,
            LatencyMs: 11m,
            Tps: 6f,
            Time: "14:32:07.418",
            MaxLatMs: 33m,
            AvgLatMs: 12m,
            IsConnected: true,
            Error: null);

        return new DashboardMetrics(
            a,
            b,
            gapBuy,
            gapSell,
            IsConnectedA: true,
            IsConnectedB: true,
            TimestampUtc: timestampUtc ?? new DateTime(2026, 9, 2, 7, 32, 7, 412, DateTimeKind.Utc));
    }

    [Fact]
    public void Format_WritesAllFields_WithTickTimestampPrefix()
    {
        var metrics = BuildMetrics();
        var expectedTime = metrics.TimestampUtc.ToLocalTime()
            .ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

        var line = GapTickLineFormatter.Format(metrics, point: 100);

        Assert.StartsWith($"[{expectedTime}] [GAP_TICK] ", line, StringComparison.Ordinal);
        Assert.Contains("gap_buy=12 gap_sell=-3", line, StringComparison.Ordinal);
        Assert.Contains("a_sym=XAUUSD a_bid=2412.35 a_ask=2412.55 a_spread=20 a_lat=8", line, StringComparison.Ordinal);
        Assert.Contains("b_sym=XAUUSD.m b_bid=2412.67 b_ask=2412.88 b_spread=21 b_lat=11", line, StringComparison.Ordinal);
        Assert.EndsWith(" point=100", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void Format_MissingGapAndPrices_WritesDash()
    {
        var metrics = BuildMetrics(gapBuy: null, gapSell: null, aBid: null, aAsk: null);

        var line = GapTickLineFormatter.Format(metrics, point: 100);

        Assert.Contains("gap_buy=- gap_sell=-", line, StringComparison.Ordinal);
        Assert.Contains("a_bid=- a_ask=-", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_UnderCommaDecimalCulture_StillUsesInvariantDecimalPoint()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

            var line = GapTickLineFormatter.Format(BuildMetrics(), point: 100);

            Assert.Contains("a_bid=2412.35", line, StringComparison.Ordinal);
            Assert.DoesNotContain("2412,35", line, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Format_LocalKindTimestamp_IsNotShiftedAgain()
    {
        var local = new DateTime(2026, 9, 2, 14, 32, 7, 412, DateTimeKind.Local);

        var line = GapTickLineFormatter.Format(BuildMetrics(timestampUtc: local), point: 100);

        Assert.StartsWith("[14:32:07.412] ", line, StringComparison.Ordinal);
    }
}
