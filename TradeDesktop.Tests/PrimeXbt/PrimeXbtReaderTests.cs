using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Domain.Models;
using TradeDesktop.Infrastructure.MarketData;

namespace TradeDesktop.Tests.PrimeXbt;

// Phase 4: reader chỉ lấy giá B từ phiên PrimeXBT khi platform_b = primexbt, quyết định lại mỗi tick;
// mt5/ctrader giữ nguyên đường cũ.
public sealed class PrimeXbtReaderTests
{
    [Fact]
    public async Task Reader_UsesPrimeXbtSessionForB_OnlyWhenPlatformIsPrimeXbt()
    {
        var provider = new MutableProvider { PlatformB = "mt5" };
        var session = new SpySession();
        var reader = new SharedMemoryMarketDataReader(provider, ctraderQuoteSession: null, primeXbtQuoteSession: session);
        var snapshots = new List<SharedMemorySnapshot>();
        reader.SnapshotReceived += (_, s) => { lock (snapshots) snapshots.Add(s); };

        await reader.StartAsync();
        try
        {
            await WaitUntilAsync(() => session.EnsureCalls.Count > 2);
            Assert.All(session.EnsureCalls, c => Assert.Equal("mt5", c));
            Assert.Equal(0, session.ReadCalls);

            provider.PlatformB = "primexbt";
            await WaitUntilAsync(() => session.ReadCalls > 2);
            lock (snapshots)
            {
                Assert.Equal("PRIMEXBT", snapshots[^1].SanB.Symbol);
                Assert.True(snapshots[^1].SanB.IsConnected);
            }

            Assert.Equal(provider.CurrentPrimeXbtConfig, session.LastConfig);

            provider.PlatformB = "ctrader"; // không có cTrader session ⇒ quay về MMF, không đọc PrimeXBT
            var readsAtSwitch = session.ReadCalls;
            await WaitUntilAsync(() => session.EnsureCalls.Count(c => c == "ctrader") > 5);
            await Task.Delay(120);
            lock (snapshots)
            {
                Assert.NotEqual("PRIMEXBT", snapshots[^1].SanB.Symbol);
                Assert.False(snapshots[^1].SanB.IsConnected);
            }

            Assert.True(session.ReadCalls <= readsAtSwitch + 1);
        }
        finally
        {
            await reader.StopAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Hết thời gian chờ điều kiện");
            await Task.Delay(20);
        }
    }

    private sealed class SpySession : IPrimeXbtQuoteSession
    {
        private int _readCalls;
        private readonly List<string> _ensureCalls = [];
        private PrimeXbtConfig? _lastConfig;

        public event Action<PrimeXbtSessionEvent>? EventRaised { add { } remove { } }
        public event Action<string>? LogLine { add { } remove { } }

        public string StatusText => "spy";
        public bool IsLoggedOn => true;
        public int ReadCalls => Volatile.Read(ref _readCalls);

        public PrimeXbtConfig? LastConfig
        {
            get { lock (_ensureCalls) return _lastConfig; }
        }

        public List<string> EnsureCalls
        {
            get { lock (_ensureCalls) return [.. _ensureCalls]; }
        }

        public void EnsureState(string platformB, PrimeXbtConfig config)
        {
            lock (_ensureCalls)
            {
                _ensureCalls.Add(platformB);
                _lastConfig = config;
            }
        }

        public ExchangeMetrics Read(ExchangeMetrics exchangeA, int point, int confirmLatencyMs)
        {
            Interlocked.Increment(ref _readCalls);
            return new ExchangeMetrics("PRIMEXBT", 1m, 2m, 1m, 0, 1, "t", 0, 0, true, null);
        }
    }

    private sealed class MutableProvider : IRuntimeConfigProvider
    {
        private volatile string _platformB = "mt5";

        public string PlatformB
        {
            get => _platformB;
            set => _platformB = value;
        }

        public string CurrentPlatformB => _platformB;
        public PrimeXbtConfig CurrentPrimeXbtConfig { get; } = PrimeXbtConfigTests.Demo();
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
        public string CurrentMapName1 => "Local\\TEST_NO_SUCH_MAP_A";
        public string CurrentMapName2 => "Local\\TEST_NO_SUCH_MAP_B";
        public DashboardMetrics? CurrentDashboardMetrics => null;
    }
}
