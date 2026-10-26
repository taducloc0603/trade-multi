using System.Net;
using System.Text;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Services;
using TradeDesktop.Infrastructure.Supabase;

namespace TradeDesktop.Tests.Config;

// b_bid_offset_pts / b_ask_offset_pts: integer NOT NULL DEFAULT 0. Thiếu cột (DB chưa migrate) = 0;
// số âm là giá trị HỢP LỆ và KHÔNG bị clamp ở bất kỳ tầng nào. Các test dưới khoá điều đó.
public sealed class BPriceOffsetConfigMappingTests
{
    private static ConfigLoadResult Build(int bBidOffsetPts, int bAskOffsetPts)
        => ConfigLoadResult.Success(
            machineHostName: "host",
            mapName1: "A",
            mapName2: "B",
            manualHwndColumns: null,
            platformA: "mt5",
            platformB: "mt5",
            point: 100,
            openPts: 1,
            confirmGapPts: 0,
            holdConfirmMs: 0,
            openPriceFreezeMs: 0,
            closePts: 1,
            closeConfirmGapPts: 0,
            closeTpProfit: 1,
            closeConfirmTpProfit: 0,
            closeMaxTpProfit: 35,
            closeHoldConfirmMs: 0,
            closePriceFreezeMs: 0,
            startTimeHold: 5,
            endTimeHold: 15,
            configId: "id",
            sansJson: "{}",
            bBidOffsetPts: bBidOffsetPts,
            bAskOffsetPts: bAskOffsetPts);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(20, -10)]
    [InlineData(-35, 17)]
    public void Success_KeepsSignedValues(int bid, int ask)
    {
        var result = Build(bid, ask);

        Assert.Equal(bid, result.BBidOffsetPts);
        Assert.Equal(ask, result.BAskOffsetPts);
    }

    [Fact]
    public void NotFound_DefaultsToZero()
    {
        var result = ConfigLoadResult.NotFound("host");

        Assert.Equal(0, result.BBidOffsetPts);
        Assert.Equal(0, result.BAskOffsetPts);
    }

    [Fact]
    public async Task LoadFromSupabase_MapsSignedOffsets()
    {
        var result = await LoadAsync("""
            ,"b_bid_offset_pts":-25,"b_ask_offset_pts":40
            """);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(-25, result.BBidOffsetPts);
        Assert.Equal(40, result.BAskOffsetPts);
    }

    [Fact]
    public async Task LoadFromSupabase_MissingColumnsDefaultToZero()
    {
        var result = await LoadAsync(string.Empty);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(0, result.BBidOffsetPts);
        Assert.Equal(0, result.BAskOffsetPts);
    }

    // Đủ cột gap-stability hợp lệ để ConfigService không fail-safe; extraColumns nối vào cuối object.
    private static async Task<ConfigLoadResult> LoadAsync(string extraColumns)
    {
        var json =
            """
            [{"id":"config-id","hostname":"test-host","sans":[],"platform_a":"mt5","platform_b":"mt5","point":100,
              "signal_cycle_size":10,
              "open_gap_absolute_floor":10,"open_gap_relative_tolerance":0.5,"open_gap_mad_multiplier":4.0,
              "open_gap_min_stable_samples":3,"open_gap_max_dispersion":0.35,"open_gap_max_drift":0.4,
              "close_gap_absolute_floor":10,"close_gap_relative_tolerance":0.6,"close_gap_mad_multiplier":4.0,
              "close_gap_min_stable_samples":3,"close_gap_max_dispersion":0.45,"close_gap_max_drift":0.6
            """ + extraColumns.Trim() + "}]";
        using var httpClient = new HttpClient(new StaticJsonHandler(json));
        var repository = new SupabaseConfigRepository(httpClient, "https://example.test", "key");
        var service = new ConfigService(repository, new StubMachineIdentityService());

        return await service.LoadByMachineHostNameAsync();
    }

    private sealed class StubMachineIdentityService : IMachineIdentityService
    {
        public string GetRawHostName() => "TEST-HOST";
        public string GetHostName() => "test-host";
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
