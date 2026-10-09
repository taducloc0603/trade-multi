using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Domain.Models;
using TradeDesktop.Infrastructure.PrimeXbt;

namespace TradeDesktop.Tests.PrimeXbt;

public sealed class PrimeXbtAwareHistoryReaderTests
{
    private sealed class InnerReader : IHistorySharedMemoryReader
    {
        public List<string> Calls { get; } = new();

        public SharedMapReadResult<HistorySharedRecord> ReadHistory(string mapName)
        {
            Calls.Add(mapName);
            return SharedMapReadResult<HistorySharedRecord>.Success(7, [], 0);
        }
    }

    private sealed class FakeTradeSession : IPrimeXbtTradeSession
    {
        public List<string> Reads { get; } = new();

        public SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName) => SharedMapReadResult<TradeSharedRecord>.MapNotFound(mapName);

        public SharedMapReadResult<HistorySharedRecord> ReadHistory(string mapName)
        {
            Reads.Add(mapName);
            return SharedMapReadResult<HistorySharedRecord>.Success(99, [], 0, connected: 1);
        }
    }

    private sealed class Provider : IRuntimeConfigProvider
    {
        public string CurrentPlatformB { get; init; } = "primexbt";
        public CTraderFixConfig CurrentCTraderFixConfig => CTraderFixConfig.Empty;
        public string CurrentMachineHostName => "test";
        public int CurrentPoint => 100;
        public int CurrentOpenPts => 0;
        public int CurrentConfirmGapPts => 0;
        public int CurrentClosePts => 0;
        public int CurrentCloseConfirmGapPts => 0;
        public double CurrentCloseTpProfit => 0;
        public double CurrentCloseConfirmTpProfit => 0;
        public int CurrentStartTimeHold => 0;
        public int CurrentEndTimeHold => 0;
        public int CurrentConfirmLatencyMs => 0;
        public int CurrentMaxGap => 0;
        public int CurrentLimitMaxGap => 0;
        public double CurrentLimitMaxTp => 0;
        public double CurrentSosTriggerAOpenDistancePts => 0;
        public int CurrentSosTriggerAfterSeconds => 0;
        public int CurrentSosCloseConfirmGapPts => 0;
        public int CurrentSosCloseGapPts => 0;
        public int CurrentMaxSpread => 0;
        public int CurrentOpenPendingTimeMs => 0;
        public int CurrentClosePendingTimeMs => 0;
        public int CurrentDelayOpenAMs => 0;
        public int CurrentDelayOpenBMs => 0;
        public int CurrentDelayCloseAMs => 0;
        public int CurrentDelayCloseBMs => 0;
        public int CurrentOpenNumberOfQualifyingTimes => 1;
        public int CurrentCloseNumberOfQualifyingTimes => 1;
        public int CurrentOppositeOpenMinDistancePts => 0;
        public int CurrentOpenPriceFreezeMs => 0;
        public int CurrentClosePriceFreezeMs => 0;
        public string CurrentMapName1 => "Local\\MT_A_Tick";
        public string CurrentMapName2 => "PRIMEXBT_B";
        public DashboardMetrics? CurrentDashboardMetrics => null;
    }

    [Fact]
    public void PrimeXbtHistoryMap_GoesToSession()
    {
        var inner = new InnerReader();
        var session = new FakeTradeSession();

        var result = new PrimeXbtAwareHistoryReader(inner, new Provider(), session).ReadHistory("PRIMEXBT_B_History");

        Assert.Equal(99UL, result.Timestamp);
        Assert.Empty(inner.Calls);
    }

    [Theory]
    [InlineData("primexbt", "Local\\MT_A_History")]
    [InlineData("mt5", "PRIMEXBT_B_History")]
    [InlineData("ctrader", "CTRADER_B_History")]
    public void OtherMapsOrPlatforms_PassThrough(string platformB, string map)
    {
        var inner = new InnerReader();
        var session = new FakeTradeSession();

        var result = new PrimeXbtAwareHistoryReader(inner, new Provider { CurrentPlatformB = platformB }, session).ReadHistory(map);

        Assert.Equal(7UL, result.Timestamp);
        Assert.Equal(new[] { map }, inner.Calls.ToArray());
        Assert.Empty(session.Reads);
    }
}
