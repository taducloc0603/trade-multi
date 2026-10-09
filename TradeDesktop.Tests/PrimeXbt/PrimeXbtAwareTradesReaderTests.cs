using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Domain.Models;
using TradeDesktop.Infrastructure.PrimeXbt;

namespace TradeDesktop.Tests.PrimeXbt;

public sealed class PrimeXbtAwareTradesReaderTests
{
    private const string PrimeXbtMap = "PRIMEXBT_B_Trades";

    private sealed class InnerReader : ITradesSharedMemoryReader
    {
        public List<string> Calls { get; } = new();

        public SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName)
        {
            Calls.Add(mapName);
            return SharedMapReadResult<TradeSharedRecord>.Success(7, [], 0);
        }
    }

    private sealed class FakeSession : IPrimeXbtQuoteSession, IPrimeXbtTradeSession
    {
        public List<string> EnsureCalls { get; } = new();
        public List<string> TradeReads { get; } = new();

        public event Action<PrimeXbtSessionEvent>? EventRaised { add { } remove { } }
        public event Action<string>? LogLine { add { } remove { } }

        public string StatusText => "fake";
        public bool IsLoggedOn => true;

        public void EnsureState(string platformB, PrimeXbtConfig config) => EnsureCalls.Add(platformB);

        public ExchangeMetrics Read(ExchangeMetrics exchangeA, int point, int confirmLatencyMs) => exchangeA;

        public SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName)
        {
            TradeReads.Add(mapName);
            return SharedMapReadResult<TradeSharedRecord>.Success(99, [], 0, connected: 1);
        }
    }

    private sealed class Provider : IRuntimeConfigProvider
    {
        public string CurrentPlatformB { get; set; } = "primexbt";
        public PrimeXbtConfig CurrentPrimeXbtConfig => PrimeXbtConfigTests.Demo();
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
    public void PrimeXbtMap_GoesToSession_NotInner()
    {
        var inner = new InnerReader();
        var session = new FakeSession();
        var reader = new PrimeXbtAwareTradesReader(inner, new Provider(), session, session);

        var result = reader.ReadTrades(PrimeXbtMap);

        Assert.Equal(99UL, result.Timestamp);
        Assert.Equal(new[] { PrimeXbtMap }, session.TradeReads.ToArray());
        Assert.Equal(new[] { "primexbt" }, session.EnsureCalls.ToArray());
        Assert.Empty(inner.Calls);
    }

    [Theory]
    [InlineData("primexbt", "Local\\MT_A_Trades")]
    [InlineData("mt5", "PRIMEXBT_B_Trades")]
    [InlineData("ctrader", "CTRADER_B_Trades")]
    [InlineData("mt5", "Local\\MT_B_Trades")]
    public void OtherMapsOrPlatforms_PassThroughUnchanged(string platformB, string map)
    {
        var inner = new InnerReader();
        var session = new FakeSession();
        var reader = new PrimeXbtAwareTradesReader(inner, new Provider { CurrentPlatformB = platformB }, session, session);

        var result = reader.ReadTrades(map);

        Assert.Equal(7UL, result.Timestamp);
        Assert.Equal(new[] { map }, inner.Calls.ToArray());
        Assert.Empty(session.TradeReads);
        Assert.Empty(session.EnsureCalls);
    }
}
