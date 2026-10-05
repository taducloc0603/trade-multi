using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Infrastructure.CTrader;

namespace TradeDesktop.Tests;

// Bảng History "Profit Realtime (A + B)": cột pt và cột "$" cộng hai chân theo STT.
// Mọi tổ hợp nền tảng phải cho hai chân CÙNG đơn vị — trước đây chân cTrader ghi Profit theo POINT
// trong khi MT4/MT5 ghi USD, nên tổng "$" vô nghĩa (ví dụ -31 pt nhưng +513 $).
public sealed class HistoryProfitCalculatorTests
{
    private const int Point = 100;
    private const double ContractSize = 100; // XAUUSD: 100 oz / lot
    private const double Lot = 0.15;

    // Kịch bản chung: A Buy -2.99 (-299 pt), B Sell +3.34 (+334 pt), 0.15 lot mỗi chân.
    private const double AOpen = 2650.00, AClose = 2647.01;
    private const double BOpen = 2650.30, BClose = 2646.96;
    private const double MtCommission = -1.05;

    private static HistorySharedRecord MtLeg(bool isBuy, double open, double close, double commission)
    {
        // MT4 OrderProfit() / MT5 DEAL_PROFIT: broker tính USD = move × lot × contract size.
        var move = isBuy ? close - open : open - close;
        return new HistorySharedRecord(
            Ticket: 1, TradeType: isBuy ? 0 : 1, Volume: Lot, OpenPrice: open, ClosePrice: close,
            Sl: 0, Tp: 0, Commission: commission, Profit: move * Lot * ContractSize,
            OpenTimeMsc: 0, CloseTimeMsc: 0, CloseEaTimeLocal: 0, Symbol: "XAUUSD");
    }

    private static HistorySharedRecord CTraderLeg(bool isBuy, double open, double close)
    {
        var projector = new CTraderHistoryProjector();
        var position = new CTraderPosition(7, 41, isBuy, (decimal)(Lot * ContractSize), (decimal)open, 1, 1);
        projector.OnPositionClosed(new CTraderClosedPosition(position, (decimal)close, 2, 2));
        return Assert.Single(projector.ToHistoryRecords("XAUUSD", (decimal)ContractSize, Point));
    }

    private static HistorySharedRecord Leg(string platform, bool isBuy, double open, double close)
        => platform == "ctrader"
            ? CTraderLeg(isBuy, open, close)
            : MtLeg(isBuy, open, close, MtCommission);

    [Theory]
    [InlineData("mt5", "ctrader")]
    [InlineData("mt4", "ctrader")]
    [InlineData("mt5", "mt5")]
    [InlineData("mt5", "mt4")]
    [InlineData("mt4", "mt5")]
    [InlineData("mt4", "mt4")]
    public void PairTotals_AreInConsistentUnits_ForEveryPlatformCombination(string platformA, string platformB)
    {
        var a = Leg(platformA, isBuy: true, AOpen, AClose);
        var b = Leg(platformB, isBuy: false, BOpen, BClose);

        var pairPoints = HistoryProfitCalculator.CalculatePoints(a, Point) + HistoryProfitCalculator.CalculatePoints(b, Point);
        var pairMoney = HistoryProfitCalculator.CalculateMoney(a) + HistoryProfitCalculator.CalculateMoney(b);

        Assert.Equal(35, pairPoints, 6);

        var expectedCommission = (platformA == "ctrader" ? 0 : MtCommission) + (platformB == "ctrader" ? 0 : MtCommission);
        // Tiền = tổng pt × $0.15/pt (0.15 lot vàng) + commission.
        Assert.Equal(35 * Lot * ContractSize / Point + expectedCommission, pairMoney, 6);
    }

    [Fact]
    public void CTraderLeg_MoneyIsMoveTimesUnits_NotPoints()
    {
        var b = CTraderLeg(isBuy: false, BOpen, BClose);

        Assert.Equal(50.10, b.Profit, 6);
        Assert.Equal(0, b.Commission);
        Assert.Equal(334, HistoryProfitCalculator.CalculatePoints(b, Point), 6);
    }

    [Fact]
    public void MtLeg_MoneyIncludesCommission()
    {
        var a = MtLeg(isBuy: true, AOpen, AClose, MtCommission);

        Assert.Equal(-44.85 - 1.05, HistoryProfitCalculator.CalculateMoney(a), 6);
    }

    [Theory]
    [InlineData(100, -299)]
    [InlineData(1000, -2990)]
    [InlineData(0, -2.99)] // point <= 0 → 1, như CalculateTradeProfit
    public void Points_UseConfiguredPoint_NotHardcoded100(int point, double expected)
    {
        var a = MtLeg(isBuy: true, AOpen, AClose, 0);

        Assert.Equal(expected, HistoryProfitCalculator.CalculatePoints(a, point), 6);
    }

    [Fact]
    public void SellLeg_PointsSignIsOpenMinusClose()
    {
        var sell = MtLeg(isBuy: false, open: 2650.00, close: 2650.40, commission: 0);

        Assert.Equal(-40, HistoryProfitCalculator.CalculatePoints(sell, Point), 6);
        Assert.Equal(-0.40 * Lot * ContractSize, HistoryProfitCalculator.CalculateMoney(sell), 6);
    }
}
