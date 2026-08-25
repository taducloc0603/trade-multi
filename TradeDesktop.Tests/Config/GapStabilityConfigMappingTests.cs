using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Infrastructure.Supabase;
using System.Net;
using System.Text;

namespace TradeDesktop.Tests.Config;

public sealed class GapStabilityConfigMappingTests
{
    private static readonly GapStabilityConfig ValidOpen =
        new(10, 0.50, 4.0, 3, 0.35, 0.40);

    private static readonly GapStabilityConfig ValidClose =
        new(10, 0.60, 4.0, 3, 0.45, 0.60);

    [Fact]
    public void TryValidate_AcceptsAgreedPolicies()
    {
        Assert.True(ValidOpen.TryValidate(out var openError), openError);
        Assert.True(ValidClose.TryValidate(out var closeError), closeError);
    }

    [Theory]
    [InlineData(-1, 0.5, 4.0, 3, 0.35, 0.4)]
    [InlineData(10, -0.1, 4.0, 3, 0.35, 0.4)]
    [InlineData(10, 0.5, -1.0, 3, 0.35, 0.4)]
    [InlineData(10, 0.5, 4.0, 2, 0.35, 0.4)]
    [InlineData(10, 0.5, 4.0, 3, -0.1, 0.4)]
    [InlineData(10, 0.5, 4.0, 3, 0.35, -0.1)]
    public void TryValidate_RejectsInvalidRanges(
        int floor,
        double relative,
        double madMultiplier,
        int samples,
        double dispersion,
        double drift)
    {
        var config = new GapStabilityConfig(
            floor,
            relative,
            madMultiplier,
            samples,
            dispersion,
            drift);

        Assert.False(config.TryValidate(out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryValidate_RejectsNonFiniteValues()
    {
        var config = ValidOpen with { RelativeTolerance = double.NaN };

        Assert.False(config.TryValidate(out var error));
        Assert.Contains("hữu hạn", error);
    }

    [Fact]
    public async Task LoadByMachineHostNameAsync_MapsBothPolicies()
    {
        var service = BuildService(BuildRecord(ValidOpen, ValidClose));

        var result = await service.LoadByMachineHostNameAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(ValidOpen, result.OpenGapStability);
        Assert.Equal(ValidClose, result.CloseGapStability);
    }

    [Fact]
    public async Task LoadByMachineHostNameAsync_MissingPolicyFailsSafe()
    {
        var service = BuildService(BuildRecord(null, ValidClose));

        var result = await service.LoadByMachineHostNameAsync();

        Assert.False(result.IsSuccess);
        Assert.True(result.Exists);
        Assert.Contains("OPEN GAP STABILITY", result.Error);
    }

    [Fact]
    public async Task LoadByMachineHostNameAsync_InvalidPolicyFailsSafe()
    {
        var invalidClose = ValidClose with { MinStableSamples = 2 };
        var service = BuildService(BuildRecord(ValidOpen, invalidClose));

        var result = await service.LoadByMachineHostNameAsync();

        Assert.False(result.IsSuccess);
        Assert.True(result.Exists);
        Assert.Contains("NORMAL CLOSE GAP STABILITY", result.Error);
    }

    [Fact]
    public async Task SupabaseRepository_MapsAllTwelveColumns()
    {
        const string json = """
            [{
              "id":"config-id",
              "hostname":"test-host",
              "sans":[],
              "platform_a":"mt5",
              "platform_b":"mt5",
              "point":100,
              "open_gap_absolute_floor":11,
              "open_gap_relative_tolerance":0.51,
              "open_gap_mad_multiplier":4.1,
              "open_gap_min_stable_samples":4,
              "open_gap_max_dispersion":0.36,
              "open_gap_max_drift":0.41,
              "close_gap_absolute_floor":12,
              "close_gap_relative_tolerance":0.61,
              "close_gap_mad_multiplier":4.2,
              "close_gap_min_stable_samples":5,
              "close_gap_max_dispersion":0.46,
              "close_gap_max_drift":0.62
            }]
            """;
        using var httpClient = new HttpClient(new StaticJsonHandler(json));
        var repository = new SupabaseConfigRepository(httpClient, "https://example.test", "key");

        var record = await repository.GetByHostNameAsync("test-host");

        Assert.NotNull(record);
        Assert.Equal(new GapStabilityConfig(11, 0.51, 4.1, 4, 0.36, 0.41), record.OpenGapStability);
        Assert.Equal(new GapStabilityConfig(12, 0.61, 4.2, 5, 0.46, 0.62), record.CloseGapStability);
    }

    private static ConfigService BuildService(ConfigRecord record) =>
        new(new StubConfigRepository(record), new StubMachineIdentityService());

    private static ConfigRecord BuildRecord(
        GapStabilityConfig? open,
        GapStabilityConfig? close) =>
        new(
            Id: "config-id",
            SansJson: "[]",
            HostName: "test-host",
            PlatformA: "mt5",
            PlatformB: "mt5",
            Point: 100,
            OpenPts: 1,
            ConfirmGapPts: 0,
            HoldConfirmMs: 1000,
            OpenPriceFreezeMs: 2000,
            ClosePts: 1,
            CloseConfirmGapPts: 0,
            CloseTpProfit: 1,
            CloseConfirmTpProfit: 0,
            CloseMaxTpProfit: 1000,
            LimitMaxTp: 1000,
            SosTriggerAOpenDistancePts: 0,
            SosTriggerAfterSeconds: 0,
            SosCloseConfirmGapPts: 0,
            SosCloseGapPts: 0,
            CloseHoldConfirmMs: 1000,
            ClosePriceFreezeMs: 2000,
            StartTimeHold: 0,
            EndTimeHold: 0,
            ConfirmLatencyMs: 100,
            MaxGap: 0,
            LimitMaxGap: 0,
            MaxSpread: 40,
            OpenMaxTimesTick: 32,
            CloseMaxTimesTick: 32,
            OpenPendingTimeMs: 30000,
            ClosePendingTimeMs: 1000,
            DelayOpenAMs: 0,
            DelayOpenBMs: 0,
            DelayCloseAMs: 0,
            DelayCloseBMs: 0,
            OpenNumberOfQualifyingTimes: 1,
            CloseNumberOfQualifyingTimes: 1,
            OpenGapStability: open,
            CloseGapStability: close);

    private sealed class StubMachineIdentityService : IMachineIdentityService
    {
        public string GetRawHostName() => "TEST-HOST";
        public string GetHostName() => "test-host";
    }

    private sealed class StubConfigRepository(ConfigRecord record) : IConfigRepository
    {
        public Task<ConfigRecord?> GetByHostNameAsync(
            string hostName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConfigRecord?>(record);

        public Task<bool> UpdateSansAndHostNameByHostNameAsync(
            string hostName,
            string sansJson,
            string platformA,
            string platformB,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> UpdateCurrentTicksAsync(
            string hostName,
            string currentTickA,
            string currentTickB,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> UpdateCurrentSlotsAsync(
            string hostName,
            string currentSlotsJson,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
