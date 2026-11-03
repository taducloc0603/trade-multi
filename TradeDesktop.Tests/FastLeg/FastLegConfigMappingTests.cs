using System.Net;
using System.Text;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Infrastructure.Supabase;

namespace TradeDesktop.Tests.FastLeg;

// Cột `fast_leg` đi qua repository + ConfigService thật. Cột hỏng KHÔNG được làm hỏng cả config (IsSuccess vẫn true).
public sealed class FastLegConfigMappingTests
{
    [Fact]
    public async Task MissingColumn_IsDisabled()
    {
        var result = await LoadAsync(string.Empty);

        Assert.True(result.IsSuccess, result.Error);
        Assert.False((result.FastLeg ?? FastLegConfig.Disabled).IsActive);
        Assert.Null(result.FastLegError);
    }

    [Fact]
    public async Task NullColumn_IsDisabled()
    {
        var result = await LoadAsync(""","fast_leg":null""");

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(result.FastLeg!.IsActive);
        Assert.Null(result.FastLegError);
    }

    [Fact]
    public async Task ValidObject_MapsEveryField()
    {
        var result = await LoadAsync(
            """
            ,"fast_leg":{"enabled":true,"mapName":"Local\\MT_C_Tick","symbol":"XAUUSD","mode":"filter","rule":"quiet",
              "maxMovePts":5,"windowMs":200,"maxAgeMs":1000,"maxDeferMs":3000,"failOpen":false,"applyToExit":true}
            """);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Null(result.FastLegError);
        var cfg = result.FastLeg!;
        Assert.True(cfg.IsFilter);
        Assert.Equal(@"Local\MT_C_Tick", cfg.MapName);
        Assert.Equal(@"Local\MT_C_Trades", cfg.HeartbeatMapName);
        Assert.Equal("XAUUSD", cfg.Symbol);
        Assert.Equal(5m, cfg.MaxMovePts);
        Assert.Equal(200, cfg.WindowMs);
        Assert.Equal(1000, cfg.MaxAgeMs);
        Assert.Equal(3000, cfg.MaxDeferMs);
        Assert.False(cfg.FailOpen);
        Assert.True(cfg.ApplyToExit);
    }

    [Fact]
    public async Task InvalidObject_KeepsRestOfConfigAndReportsError()
    {
        var result = await LoadAsync(""","fast_leg":{"enabled":true,"rule":"momentum"},"b_bid_offset_pts":7""");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(7, result.BBidOffsetPts);
        Assert.False(result.FastLeg!.IsActive);
        Assert.Contains("rule", result.FastLegError);
    }

    [Fact]
    public async Task MapNameOfExchangeA_IsRejected()
    {
        var result = await LoadAsync(""","fast_leg":{"enabled":true,"mapName":"Local\\MT_A_Tick"}""");

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(result.FastLeg!.IsActive);
        Assert.Contains("A/B", result.FastLegError);
    }

    [Theory]
    [InlineData(""","fast_leg":"{\"enabled\":true}" """)]
    [InlineData(""","fast_leg":[1,2] """)]
    [InlineData(""","fast_leg":true """)]
    public async Task NonObjectColumn_IsDisabledWithError(string column)
    {
        // Chuỗi JSON nhập nhầm trong editor Supabase phải báo lỗi, KHÔNG được tắt sàn C im lặng.
        var result = await LoadAsync(column);

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(result.FastLeg!.IsActive);
        Assert.Contains("JSON object", result.FastLegError);
    }

    [Fact]
    public void NotFound_HasNoFastLeg()
    {
        var result = ConfigLoadResult.NotFound("host");

        Assert.Null(result.FastLeg);
        Assert.Null(result.FastLegError);
    }

    // Đủ cột gap-stability hợp lệ để ConfigService không fail-safe; map A/B = Local\MT_A_Tick / Local\MT_B_Tick.
    private static async Task<ConfigLoadResult> LoadAsync(string extraColumns)
    {
        var json =
            """
            [{"id":"config-id","hostname":"test-host","sans":["Local\\MT_A_Tick","Local\\MT_B_Tick"],"platform_a":"mt5","platform_b":"mt5","point":100,
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
