using TradeDesktop.Application.Services.CTrader;
using TradeDesktop.Infrastructure.CTrader;

namespace TradeDesktop.Tests.CTrader;

public sealed class CTraderPositionCacheTests
{
    private const string Header = "34=10|49=cServer|50=TRADE|52=20260916-15:00:00.000|56=live.fxpro.8220816|57=TRADE";

    private static QuickFix.Message Er(char execType, string execId, long positionId, char side, decimal qty, decimal price, string clOrdId = "c1")
        => FixTestSupport.Parse(
            $"35=8|{Header}|37=900|11={clOrdId}|17={execId}|150={execType}|39={(execType == 'F' ? '2' : '0')}|55=41|54={side}|38={qty}|32={qty}|31={price}|151=0|14={qty}|6={price}|60=20260916-15:00:00.123|721={positionId}");

    private static CTraderPositionReport Ap(string body)
    {
        Assert.True(CTraderPositionReportParser.TryParse(FixTestSupport.Parse($"35=AP|{Header}|{body}"), out var report));
        return report;
    }

    [Fact]
    public void Parser_ReadsLongPositionFromGroup()
    {
        var report = Ap("710=r1|721=555|727=1|728=0|55=41|702=1|704=100|705=0|730=4350.5");

        Assert.Equal("r1", report.PosReqId);
        Assert.Equal(1, report.TotalNumPosReports);
        Assert.NotNull(report.Position);
        Assert.Equal(555, report.Position!.PositionId);
        Assert.True(report.Position.IsBuy);
        Assert.Equal(100m, report.Position.VolumeUnits);
        Assert.Equal(4350.5m, report.Position.EntryPrice);
    }

    [Fact]
    public void Parser_ShortPosition_AndNoPositionsResult()
    {
        var shortReport = Ap("710=r1|721=7|727=1|728=0|55=41|702=1|704=0|705=200|730=4000");
        Assert.False(shortReport.Position!.IsBuy);
        Assert.Equal(200m, shortReport.Position.VolumeUnits);

        var empty = Ap("710=r2|727=0|728=2");
        Assert.Equal(CTraderPositionReport.ResultNoPositions, empty.PosReqResult);
        Assert.Null(empty.Position);
    }

    [Fact]
    public void Parser_NonApMessage_ReturnsFalse()
    {
        Assert.False(CTraderPositionReportParser.TryParse(Er('F', "e1", 1, '1', 100, 1), out _));
    }

    [Fact]
    public void NotSynced_UntilBatchComplete_AndMapUnavailable()
    {
        var cache = new CTraderPositionCache();

        Assert.False(cache.PositionsSynced);
        Assert.False(cache.ApplyPositionReport(Ap("710=r1|721=1|727=2|728=0|55=41|702=1|704=100|730=1")));
        Assert.False(cache.PositionsSynced);
        Assert.Equal(0, cache.Count);

        Assert.True(cache.ApplyPositionReport(Ap("710=r1|721=2|727=2|728=0|55=41|702=1|704=0|705=100|730=2")));
        Assert.True(cache.PositionsSynced);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void ReportFromNewBatchDiscardsUnfinishedBatch()
    {
        var cache = new CTraderPositionCache();
        cache.ApplyPositionReport(Ap("710=old|721=1|727=2|728=0|55=41|702=1|704=100|730=1"));

        Assert.True(cache.ApplyPositionReport(Ap("710=new|721=9|727=1|728=0|55=41|702=1|704=100|730=1")));

        Assert.Equal([9L], cache.Positions.Select(p => p.PositionId));
    }

    [Fact]
    public void NoPositionsResult_CountsAsSyncedEmpty()
    {
        var cache = new CTraderPositionCache();

        cache.ApplyPositionReport(Ap("710=r1|727=0|728=2"));

        Assert.True(cache.PositionsSynced);
        Assert.Equal(0, cache.Count);
        var result = cache.ReadAsMapResult(FixTestSupport.Health(), "CTRADER_B_Trades", "XAUUSD", 100m);
        Assert.True(result.IsMapAvailable);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void MapUnavailable_WhenHealthNotSynced_EvenWithPositions()
    {
        var cache = new CTraderPositionCache();
        cache.ApplyExecutionReport(Er('F', "e1", 5, '1', 100, 4350));

        var result = cache.ReadAsMapResult(FixTestSupport.Health(synced: false), "CTRADER_B_Trades", "XAUUSD", 100m);

        Assert.False(result.IsMapAvailable);
        Assert.Empty(result.Records);
    }

    [Fact]
    public void LoggedOut_ClearsSynced()
    {
        var cache = new CTraderPositionCache();
        cache.ApplyPositionReport(Ap("710=r1|727=0|728=2"));

        cache.OnLoggedOut();

        Assert.False(cache.PositionsSynced);
    }

    [Fact]
    public void NewThenFill_OnlyFillOpensPosition()
    {
        var cache = new CTraderPositionCache();

        Assert.False(cache.ApplyExecutionReport(Er('0', "e0", 5, '1', 100, 4350)));
        Assert.Equal(0UL, cache.Version);
        Assert.Equal(0, cache.Count);

        Assert.True(cache.ApplyExecutionReport(Er('F', "e1", 5, '1', 100, 4350)));
        Assert.Equal(1UL, cache.Version);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void DuplicateFill_IsIgnoredAndVersionStable()
    {
        var cache = new CTraderPositionCache();
        cache.ApplyExecutionReport(Er('F', "e1", 5, '1', 100, 4350));

        Assert.False(cache.ApplyExecutionReport(Er('F', "e1", 5, '1', 100, 4350)));

        Assert.Equal(1UL, cache.Version);
        Assert.Equal(100m, cache.Positions.Single().VolumeUnits);
    }

    [Fact]
    public void FillBeforePendingSync_ThenSyncReportSameContent_DoesNotBumpVersion()
    {
        var cache = new CTraderPositionCache();
        cache.ApplyExecutionReport(Er('F', "e1", 5, '1', 100, 4350));

        Assert.False(cache.ApplyPositionReport(Ap("710=r1|721=5|727=1|728=0|55=41|702=1|704=100|730=4350")));

        Assert.True(cache.PositionsSynced);
        Assert.Equal(1UL, cache.Version);
    }

    [Fact]
    public void AddReduceClose_RaisesClosedOnceWithClosePrice()
    {
        var cache = new CTraderPositionCache();
        var closed = new List<CTraderClosedPosition>();
        cache.PositionClosed += closed.Add;

        cache.ApplyExecutionReport(Er('F', "e1", 5, '1', 100, 4350));
        cache.ApplyExecutionReport(Er('F', "e2", 5, '1', 100, 4352));
        Assert.Equal(4351m, cache.Positions.Single().EntryPrice);

        cache.ApplyExecutionReport(Er('F', "e3", 5, '2', 50, 4360));
        Assert.Equal(150m, cache.Positions.Single().VolumeUnits);
        Assert.Empty(closed);

        cache.ApplyExecutionReport(Er('F', "e4", 5, '2', 150, 4361));

        Assert.Equal(0, cache.Count);
        var single = Assert.Single(closed);
        Assert.Equal(4361m, single.ClosePrice);
        Assert.Equal(5, single.Position.PositionId);
    }

    [Fact]
    public void RejectedReport_Ignored()
    {
        var cache = new CTraderPositionCache();

        Assert.False(cache.ApplyExecutionReport(Er('8', "e1", 5, '1', 100, 4350)));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void TradeRecords_UseEncodedTicketAndLots()
    {
        var cache = new CTraderPositionCache(() => 777);
        cache.ApplyExecutionReport(Er('F', "e1", 5, '2', 150, 4350));

        var record = Assert.Single(cache.ToTradeRecords("XAUUSD", 100m));

        Assert.Equal(CTraderTicketCodec.Encode(5), record.Ticket);
        Assert.Equal(1, record.TradeType);
        Assert.Equal(1.5, record.Lot, 6);
        Assert.Equal("XAUUSD", record.Symbol);
        Assert.Equal(777UL, record.OpenEaTimeLocal);
    }

    [Fact]
    public void BatchOfThree_SyncedOnlyAfterThird()
    {
        var cache = new CTraderPositionCache();

        cache.ApplyPositionReport(Ap("710=r3|721=1|727=3|728=0|55=41|702=1|704=100|730=1"));
        Assert.False(cache.PositionsSynced);
        cache.ApplyPositionReport(Ap("710=r3|721=2|727=3|728=0|55=41|702=1|704=100|730=1"));
        Assert.False(cache.PositionsSynced);
        cache.ApplyPositionReport(Ap("710=r3|721=3|727=3|728=0|55=41|702=1|704=0|705=100|730=1"));

        Assert.True(cache.PositionsSynced);
        Assert.Equal(3, cache.Count);
    }

    // R2: health dựng từ cache — logout giữa lúc có position → unavailable NGAY; logon lại chưa sync → vẫn unavailable.
    [Fact]
    public void LogoutWithOpenPositions_MapUnavailableImmediately_AndStaysUntilResync()
    {
        var cache = new CTraderPositionCache();
        cache.ApplyPositionReport(Ap("710=r1|721=5|727=1|728=0|55=41|702=1|704=100|730=4350"));
        Assert.True(FixTestSupport.Health(synced: cache.PositionsSynced).IsMapAvailable);

        cache.OnLoggedOut();
        Assert.False(FixTestSupport.Health(trade: false, synced: cache.PositionsSynced).IsMapAvailable);
        Assert.Equal(1, cache.Count);

        var relogged = FixTestSupport.Health(trade: true, synced: cache.PositionsSynced);
        Assert.False(relogged.IsMapAvailable);
        Assert.False(cache.ReadAsMapResult(relogged, "CTRADER_B_Trades", "XAUUSD", 100m).IsMapAvailable);

        cache.ApplyPositionReport(Ap("710=r2|721=5|727=1|728=0|55=41|702=1|704=100|730=4350"));
        Assert.True(FixTestSupport.Health(synced: cache.PositionsSynced).IsMapAvailable);
    }

    [Fact]
    public void Version_StableAcrossRepeatedReadsWithoutNewMessages()
    {
        var cache = new CTraderPositionCache();
        cache.ApplyPositionReport(Ap("710=r1|721=5|727=1|728=0|55=41|702=1|704=100|730=4350"));
        var version = cache.Version;

        for (var i = 0; i < 10; i++)
        {
            var result = cache.ReadAsMapResult(FixTestSupport.Health(), "CTRADER_B_Trades", "XAUUSD", 100m);
            Assert.Equal(version, result.Timestamp);
        }

        // Reconciliation định kỳ với cùng nội dung cũng không làm version nhảy.
        cache.ApplyPositionReport(Ap("710=r2|721=5|727=1|728=0|55=41|702=1|704=100|730=4350"));
        Assert.Equal(version, cache.Version);
    }

    [Fact]
    public void OpenEaTimeLocal_DefaultClockIsEnvironmentTickCount_NotZero()
    {
        var before = (ulong)Environment.TickCount64;
        var cache = new CTraderPositionCache();
        cache.ApplyExecutionReport(Er('F', "e1", 5, '1', 100, 4350));

        var stamped = cache.Positions.Single().OpenEaTimeLocal;
        Assert.True(stamped >= before && stamped > 0);
        Assert.True(cache.Positions.Single().OpenTimeMsc > 0);
    }

    [Fact]
    public void HistoryProjector_ComputesSyntheticProfitAndCapsFifo()
    {
        var projector = new CTraderHistoryProjector(capacity: 2);
        var buy = new CTraderPosition(1, 41, true, 100, 4350.00m, 1, 1);
        var sell = new CTraderPosition(2, 41, false, 100, 4350.00m, 1, 1);

        projector.OnPositionClosed(new CTraderClosedPosition(buy, 4351.25m, 2, 2));
        projector.OnPositionClosed(new CTraderClosedPosition(sell, 4351.25m, 2, 2));

        var records = projector.ToHistoryRecords("XAUUSD", 100m, 100);
        Assert.Equal(125, records[0].Profit, 6);
        Assert.Equal(-125, records[1].Profit, 6);
        Assert.Equal(0, records[0].Commission);

        projector.OnPositionClosed(new CTraderClosedPosition(buy with { PositionId = 3 }, 4350m, 3, 3));
        Assert.Equal(2, projector.Count);
        Assert.Equal(3UL, projector.Version);
        Assert.Equal(CTraderTicketCodec.Encode(2), projector.ToHistoryRecords("XAUUSD", 100m, 100)[0].Ticket);

        Assert.False(projector.ReadAsMapResult(FixTestSupport.Health(trade: false), "h", "XAUUSD", 100m, 100).IsMapAvailable);
    }
}
