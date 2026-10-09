using System.Reflection;
using TradeDesktop.Application.Services.PrimeXbt;

namespace TradeDesktop.Tests.PrimeXbt;

public sealed class PrimeXbtOrderPlannerTests
{
    private static PrimeXbtTradeSettings Settings() =>
        PrimeXbtTradeSettings.TryParse(PrimeXbtFixtures.Body("quotes.json", "trade_settings_response"))!;

    [Fact]
    public void TradeSettings_RealFixture()
    {
        var s = Settings();

        Assert.Equal(0.01m, s.MinOrderSize);
        Assert.Equal(0.01m, s.OrderStep);
        Assert.Equal(5000m, s.MaxOrderSize);
        Assert.Equal("ounces", s.LotUnit);
    }

    [Theory]
    [InlineData("""{"minOrderSize":0.01,"orderStep":0.01}""")]
    [InlineData("""{"minOrderSize":1,"orderStep":0.01,"maxOrderSize":0.5}""")]
    [InlineData("""[]""")]
    public void TradeSettings_IncompleteOrInconsistent_IsNull(string json)
    {
        Assert.Null(PrimeXbtTradeSettings.TryParse(PrimeXbtFixtures.Json(json)));
    }

    [Fact]
    public void PlanOpen_MatchesWebAppFrame()
    {
        var result = PrimeXbtOrderPlanner.PlanOpen("XAU/USD", PrimeXbtSide.Buy, 0.01m, Settings());

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("orders/market/place", result.Plan!.Action);
        var expected = PrimeXbtFixtures.Get("trading.json", "open_buy", "request", "frame", "body").GetRawText();
        Assert.Equal(expected, result.Plan.Body.ToJsonString());
    }

    [Fact]
    public void PlanOpen_Sell()
    {
        var result = PrimeXbtOrderPlanner.PlanOpen("XAU/USD", PrimeXbtSide.Sell, 0.01m, Settings());

        Assert.Equal("SELL", result.Plan!.Body["side"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("0.001")]   // < min — server trả TOO_LOW_AMOUNT
    [InlineData("0.015")]   // sai step — server trả WRONG_ORDER_AMOUNT
    [InlineData("5000.01")] // > max
    [InlineData("0")]
    public void PlanOpen_InvalidQty_IsRejectedBeforeSending(string qty)
    {
        var result = PrimeXbtOrderPlanner.PlanOpen("XAU/USD", PrimeXbtSide.Buy, decimal.Parse(qty, System.Globalization.CultureInfo.InvariantCulture), Settings());

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void PlanOpen_WithoutSettings_FailsClosed()
    {
        Assert.False(PrimeXbtOrderPlanner.PlanOpen("XAU/USD", PrimeXbtSide.Buy, 0.01m, null).IsSuccess);
        Assert.False(PrimeXbtOrderPlanner.PlanOpen(" ", PrimeXbtSide.Buy, 0.01m, Settings()).IsSuccess);
    }

    [Fact]
    public void PlanClose_MatchesWebAppFrame()
    {
        var result = PrimeXbtOrderPlanner.PlanClose(10680072, 0.01m);

        Assert.Equal("positions/id/close", result.Plan!.Action);
        var expected = PrimeXbtFixtures.Get("trading.json", "close_buy_by_id", "request", "frame", "body").GetRawText();
        Assert.Equal(expected, result.Plan.Body.ToJsonString());
    }

    [Theory]
    [InlineData(0L, "0.01")]
    [InlineData(10680072L, "0")]
    public void PlanClose_Invalid_IsRejected(long id, string qty)
    {
        Assert.False(PrimeXbtOrderPlanner.PlanClose(id, decimal.Parse(qty, System.Globalization.CultureInfo.InvariantCulture)).IsSuccess);
    }

    [Fact]
    public void Planner_ExposesOnlyOpenAndCloseById()
    {
        var methods = typeof(PrimeXbtOrderPlanner).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.Name).OrderBy(n => n).ToArray();
        var actions = typeof(PrimeXbtOrderPlanner).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetValue(null)!).ToArray();

        Assert.Equal(new[] { "PlanClose", "PlanOpen" }, methods);
        Assert.Equal(2, actions.Length);
        Assert.DoesNotContain(actions, a => a.EndsWith("/all", StringComparison.Ordinal));
        Assert.DoesNotContain("positions/close", actions);
    }
}

public sealed class PrimeXbtMatchingAndHistoryTests
{
    private static IReadOnlyList<PrimeXbtOrderReport> Orders() =>
        PrimeXbtOrderReportParser.Parse(PrimeXbtFixtures.Body("history.json", "report_orders2_response"));

    private static IReadOnlyList<PrimeXbtSubPosition> HedgePositions() =>
        PrimeXbtPositionsParser.Parse(PrimeXbtFixtures.Body("account-positions.json", "positions_event_hedge_two_subs"), "XAU/USD").Positions;

    [Fact]
    public void ReportParser_RealFixture()
    {
        var orders = Orders();

        Assert.Equal(4, orders.Count);
        Assert.Equal(2, orders.Count(o => o.IsClosePosition));
        Assert.All(orders.Where(o => o.IsClosePosition), o => Assert.NotNull(o.PositionId));
        Assert.All(orders.Where(o => !o.IsClosePosition), o => Assert.Null(o.PositionId));
    }

    [Theory]
    [InlineData(2095468976L, 10680072L)] // Buy
    [InlineData(2095469981L, 10680080L)] // Sell (hedge)
    public void Matcher_RealOpenOrders_MatchTheirSubPosition(long orderId, long expectedPositionId)
    {
        var order = Orders().Single(o => o.Id == orderId);

        var result = PrimeXbtOrderMatcher.Match(order, HedgePositions());

        Assert.Equal(PrimeXbtMatchStatus.Matched, result.Status);
        Assert.Equal(expectedPositionId, result.PositionId);
    }

    [Fact]
    public void Matcher_CloseOrder_IsNotAnOpenToMatch()
    {
        var close = Orders().First(o => o.IsClosePosition);

        Assert.Equal(PrimeXbtMatchStatus.InvalidOrder, PrimeXbtOrderMatcher.Match(close, HedgePositions()).Status);
    }

    [Fact]
    public void Matcher_TwoIdenticalCandidates_IsAmbiguous_NeverGuesses()
    {
        var order = Orders().Single(o => o.Id == 2095468976);
        var buy = HedgePositions().Single(p => p.Id == 10680072);
        var twin = buy with { Id = 99999999 };

        var result = PrimeXbtOrderMatcher.Match(order, [buy, twin]);

        Assert.Equal(PrimeXbtMatchStatus.Ambiguous, result.Status);
        Assert.Null(result.PositionId);
        Assert.Equal(2, result.CandidateCount);
    }

    [Fact]
    public void Matcher_PriceDiffers_IsNotFound()
    {
        var order = Orders().Single(o => o.Id == 2095468976) with { ExecutedPrice = 4179.62m };

        Assert.Equal(PrimeXbtMatchStatus.NotFound, PrimeXbtOrderMatcher.Match(order, HedgePositions()).Status);
    }

    [Fact]
    public void History_WithKnownOpenPrices_ComputesExactProfitInsteadOfRoundedRpl()
    {
        var opens = new Dictionary<long, decimal> { [10680072] = 4179.61m, [10680080] = 4179.03m };

        var trades = PrimeXbtHistoryProjector.Project(Orders(), opens);

        Assert.Equal(2, trades.Count);
        var buy = trades.Single(t => t.PositionId == 10680072);
        Assert.Equal(PrimeXbtSide.Buy, buy.PositionSide);   // đóng Buy bằng lệnh Sell
        Assert.Equal(-0.0010m, buy.Profit);                  // (4179.51 − 4179.61) × 0.01
        Assert.Equal(0m, buy.BrokerRpl);                     // rpl broker làm tròn về 0
        Assert.False(buy.ProfitIsEstimated);
        var sell = trades.Single(t => t.PositionId == 10680080);
        Assert.Equal(PrimeXbtSide.Sell, sell.PositionSide);
        Assert.Equal(-0.0031m, sell.Profit);                 // (4179.34 − 4179.03) × 0.01 × −1
    }

    [Fact]
    public void History_WithoutOpenPrice_FallsBackToRplAndFlagsEstimate()
    {
        var trades = PrimeXbtHistoryProjector.Project(Orders(), new Dictionary<long, decimal>());

        Assert.All(trades, t => Assert.True(t.ProfitIsEstimated));
        Assert.All(trades, t => Assert.Equal(0m, t.Profit));
    }

    [Fact]
    public void History_IgnoresOpenOrders()
    {
        var trades = PrimeXbtHistoryProjector.Project(Orders().Where(o => !o.IsClosePosition), new Dictionary<long, decimal>());

        Assert.Empty(trades);
    }
}
