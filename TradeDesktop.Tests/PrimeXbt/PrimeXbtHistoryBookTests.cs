using TradeDesktop.Application.Services.PrimeXbt;
using TradeDesktop.Infrastructure.PrimeXbt;

namespace TradeDesktop.Tests.PrimeXbt;

public sealed class PrimeXbtHistoryBookTests
{
    private const string Symbol = "XAU/USD";

    private static IReadOnlyList<PrimeXbtOrderReport> Orders()
        => PrimeXbtOrderReportParser.Parse(PrimeXbtFixtures.Body("history.json", "report_orders2_response"));

    private static IReadOnlyList<PrimeXbtSubPosition> HedgePositions()
        => PrimeXbtPositionsParser.Parse(PrimeXbtFixtures.Body("account-positions.json", "positions_event_hedge_two_subs"), Symbol).Positions;

    [Fact]
    public void KnownOpenPrices_ProfitIsComputedInUsd_NotRoundedRpl()
    {
        var book = new PrimeXbtHistoryBook(() => 7_000);
        book.RememberOpen(HedgePositions());

        var (added, estimated) = book.Apply(Orders(), Symbol);
        var records = book.ToHistoryRecords(Symbol, 100m);

        Assert.Equal(2, added); // 2 lệnh ĐÓNG; 2 lệnh mở (openReason=CLIENT) bị bỏ
        Assert.Equal(0, estimated);
        Assert.Equal(2, records.Count);
        var buy = records.Single(r => r.TradeType == 0);
        Assert.True(PrimeXbtTicketCodec.TryDecode(buy.Ticket, out var buyId));
        Assert.Equal(10680072L, buyId);
        Assert.Equal(4179.61, buy.OpenPrice, 6);
        Assert.Equal(4179.51, buy.ClosePrice, 6);
        Assert.Equal(-0.001, buy.Profit, 9); // rpl báo 0 (làm tròn)
        Assert.Equal(0.0001, buy.Volume, 10);
        Assert.Equal(0, buy.Commission);
        Assert.Equal((ulong)DateTimeOffset.Parse("2026-10-09T02:57:39.202Z").ToUnixTimeMilliseconds(), buy.OpenTimeMsc);
        Assert.Equal((ulong)DateTimeOffset.Parse("2026-10-09T02:58:58.924Z").ToUnixTimeMilliseconds(), buy.CloseTimeMsc);
        Assert.Equal(7_000UL, buy.CloseEaTimeLocal);
        var sell = records.Single(r => r.TradeType == 1);
        Assert.Equal(-0.0031, sell.Profit, 9); // Sell 4179.03 → đóng 4179.34
    }

    [Fact]
    public void RecordsOrderedByCloseTime_OldestFirst()
    {
        var book = new PrimeXbtHistoryBook(() => 1);
        book.RememberOpen(HedgePositions());
        book.Apply(Orders(), Symbol);

        var records = book.ToHistoryRecords(Symbol, 100m);

        Assert.True(records[0].CloseTimeMsc < records[1].CloseTimeMsc);
    }

    [Fact]
    public void SameReportAgain_IsDeduped_VersionUnchanged()
    {
        var book = new PrimeXbtHistoryBook(() => 1);
        book.Apply(Orders(), Symbol);
        var version = book.Version;

        Assert.Equal((0, 0), book.Apply(Orders(), Symbol));
        Assert.Equal(version, book.Version);
        Assert.Equal(2, book.Count);
    }

    [Fact]
    public void UnknownOpenPrice_UsesRpl_MarkedEstimated_OpenPriceBackComputed()
    {
        var book = new PrimeXbtHistoryBook(() => 1);

        var (added, estimated) = book.Apply(Orders(), Symbol);
        var buy = book.ToHistoryRecords(Symbol, 100m).Single(r => r.TradeType == 0);

        Assert.Equal(2, added);
        Assert.Equal(2, estimated);
        Assert.Equal(0, buy.Profit); // rpl
        Assert.Equal(buy.ClosePrice, buy.OpenPrice, 6); // rpl 0 ⇒ suy ngược ra đúng giá đóng
        Assert.Equal(0UL, buy.OpenTimeMsc);
    }

    [Fact]
    public void OtherSymbol_IsIgnored()
    {
        var book = new PrimeXbtHistoryBook(() => 1);

        Assert.Equal((0, 0), book.Apply(Orders(), "BTC/USD"));
        Assert.Equal(0UL, book.Version);
    }

    [Fact]
    public void Fifo_KeepsNewestWithinCapacity()
    {
        var book = new PrimeXbtHistoryBook(() => 1, capacity: 1);
        book.Apply(Orders(), Symbol);

        var only = Assert.Single(book.ToHistoryRecords(Symbol, 100m));
        Assert.True(PrimeXbtTicketCodec.TryDecode(only.Ticket, out var id));
        Assert.Equal(10680080L, id); // lệnh đóng muộn nhất (02:59:12)
    }

    [Fact]
    public void Fee_BecomesNegativeCommission()
    {
        var withFee = Orders().Select(o => o with { Fee = 0.05m }).ToList();
        var book = new PrimeXbtHistoryBook(() => 1);

        book.Apply(withFee, Symbol);

        Assert.All(book.ToHistoryRecords(Symbol, 100m), r => Assert.Equal(-0.05, r.Commission, 9));
    }
}
