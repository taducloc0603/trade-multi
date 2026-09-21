using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Infrastructure.Supabase;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TradeDesktop.Tests.Config;

// R7: NormalizePlatform có 5 bản sao private (ConfigService ×2, SupabaseConfigRepository, RuntimeConfigState,
// ConfigViewModel). Test project không reference App nên hai bản ở App được kiểm bằng grep lúc nghiệm thu; ba bản
// còn lại được khoá qua API public dưới đây. Mục tiêu: không bản nào âm thầm biến "ctrader" thành "mt5".
public sealed class PlatformNormalizationTests
{
    public static TheoryData<string?, string> NormalizationCases => new()
    {
        { "mt4", "mt4" },
        { "mt5", "mt5" },
        { "ctrader", "ctrader" },
        { "CTRADER", "ctrader" },
        { " ctrader ", "ctrader" },
        { " MT4 ", "mt4" },
        { "xyz", "mt5" },
        { "", "mt5" },
        { null, "mt5" },
    };

    [Theory]
    [MemberData(nameof(NormalizationCases))]
    public async Task NormalizePlatform_AllTestableCopiesAcceptSameSet(string? raw, string expected)
    {
        // Bản ConfigService dùng khi Save (đầu file) — đi qua chân B vì chân A=ctrader bị reject.
        var repository = new CapturingConfigRepository(BaseRecord);
        var saveResult = await BuildService(repository)
            .SaveByMachineHostNameAsync("MAP_A", "MAP_B", "mt5", raw!);
        Assert.True(saveResult.IsSuccess, saveResult.Error);
        var fromSave = repository.SavedPlatformB;

        // Bản ConfigService dùng khi Load (cuối file).
        var loadResult = await BuildService(new CapturingConfigRepository(BaseRecord with { PlatformB = raw! }))
            .LoadByMachineHostNameAsync();
        Assert.True(loadResult.IsSuccess, loadResult.Error);
        var fromLoad = loadResult.PlatformB;

        // Bản SupabaseConfigRepository khi đọc row.
        var fromSupabaseRead = await ReadPlatformBFromSupabaseAsync(raw);

        Assert.Equal(expected, fromSave);
        Assert.Equal(expected, fromLoad);
        Assert.Equal(expected, fromSupabaseRead);
    }

    [Fact]
    public async Task Save_PlatformACTrader_IsRejectedAndNothingWritten()
    {
        var repository = new CapturingConfigRepository(BaseRecord);

        var result = await BuildService(repository)
            .SaveByMachineHostNameAsync("MAP_A", "MAP_B", "ctrader", "mt5");

        Assert.False(result.IsSuccess);
        Assert.Equal("cTrader chỉ được dùng cho sàn B.", result.Error);
        Assert.Equal(0, repository.UpdateCallCount);
    }

    [Theory]
    [InlineData("CTRADER")]
    [InlineData(" ctrader ")]
    public async Task Save_PlatformACTraderInAnyCase_IsRejected(string platformA)
    {
        var repository = new CapturingConfigRepository(BaseRecord);

        var result = await BuildService(repository)
            .SaveByMachineHostNameAsync("MAP_A", "MAP_B", platformA, "mt5");

        Assert.False(result.IsSuccess);
        Assert.Equal(0, repository.UpdateCallCount);
    }

    // Ma trận platform README §1: 6 tổ hợp hợp lệ lưu được và giữ nguyên giá trị.
    [Theory]
    [InlineData("mt4", "mt4")]
    [InlineData("mt4", "mt5")]
    [InlineData("mt5", "mt4")]
    [InlineData("mt5", "mt5")]
    [InlineData("mt4", "ctrader")]
    [InlineData("mt5", "ctrader")]
    public async Task Save_SupportedPlatformMatrix_RoundTripsUnchanged(string platformA, string platformB)
    {
        var repository = new CapturingConfigRepository(BaseRecord);

        var result = await BuildService(repository)
            .SaveByMachineHostNameAsync("MAP_A", "MAP_B", platformA, platformB);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(platformA, repository.SavedPlatformA);
        Assert.Equal(platformB, repository.SavedPlatformB);
    }

    [Theory]
    [InlineData("mt4", "ctrader")]
    [InlineData("mt5", "ctrader")]
    [InlineData("mt5", "mt4")]
    public async Task Load_SupportedPlatformMatrix_RoundTripsUnchanged(string platformA, string platformB)
    {
        var result = await BuildService(new CapturingConfigRepository(
                BaseRecord with { PlatformA = platformA, PlatformB = platformB }))
            .LoadByMachineHostNameAsync();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(platformA, result.PlatformA);
        Assert.Equal(platformB, result.PlatformB);
    }

    [Fact]
    public async Task SupabaseRepository_WritePath_KeepsCTraderInPatchPayload()
    {
        var handler = new CapturingHandler("""[{"id":"config-id"}]""");
        using var httpClient = new HttpClient(handler);
        var repository = new SupabaseConfigRepository(httpClient, "https://example.test", "key");

        var updated = await repository.UpdateSansAndHostNameByHostNameAsync(
            "test-host", "[]", "mt5", " CTrader ");

        Assert.True(updated);
        using var payload = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("mt5", payload.RootElement.GetProperty("platform_a").GetString());
        Assert.Equal("ctrader", payload.RootElement.GetProperty("platform_b").GetString());
    }

    private static async Task<string> ReadPlatformBFromSupabaseAsync(string? raw)
    {
        var platformJson = raw is null ? "null" : JsonSerializer.Serialize(raw);
        var json = $$"""[{"id":"config-id","hostname":"test-host","sans":[],"platform_a":"mt5","platform_b":{{platformJson}}}]""";
        using var httpClient = new HttpClient(new CapturingHandler(json));
        var repository = new SupabaseConfigRepository(httpClient, "https://example.test", "key");

        var record = await repository.GetByHostNameAsync("test-host");

        Assert.NotNull(record);
        return record.PlatformB;
    }

    private static ConfigService BuildService(IConfigRepository repository) =>
        new(repository, new StubMachineIdentityService());

    internal static readonly ConfigRecord BaseRecord = new(
        Id: "config-id",
        SansJson: "[]",
        HostName: "test-host",
        PlatformA: "mt5",
        PlatformB: "mt5",
        Point: 100,
        OpenPts: 1,
        ConfirmGapPts: 0,
        HoldConfirmMs: 0,
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
        CloseHoldConfirmMs: 0,
        ClosePriceFreezeMs: 2000,
        StartTimeHold: 0,
        EndTimeHold: 0,
        ConfirmLatencyMs: 100,
        MaxGap: 0,
        LimitMaxGap: 0,
        MaxSpread: 40,
        OpenMaxTimesTick: 0,
        CloseMaxTimesTick: 0,
        OpenPendingTimeMs: 30000,
        ClosePendingTimeMs: 1000,
        DelayOpenAMs: 0,
        DelayOpenBMs: 0,
        DelayCloseAMs: 0,
        DelayCloseBMs: 0,
        OpenNumberOfQualifyingTimes: 1,
        CloseNumberOfQualifyingTimes: 1,
        OpenGapStability: new GapStabilityConfig(10, 0.50, 4.0, 3, 0.35, 0.40),
        CloseGapStability: new GapStabilityConfig(10, 0.60, 4.0, 3, 0.45, 0.60),
        SignalCycleSize: 10);

    internal sealed class StubMachineIdentityService : IMachineIdentityService
    {
        public string GetRawHostName() => "TEST-HOST";
        public string GetHostName() => "test-host";
    }

    internal sealed class CapturingConfigRepository(ConfigRecord record) : IConfigRepository
    {
        public int UpdateCallCount { get; private set; }
        public string? SavedPlatformA { get; private set; }
        public string? SavedPlatformB { get; private set; }
        public string? SavedSansJson { get; private set; }

        public Task<ConfigRecord?> GetByHostNameAsync(
            string hostName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ConfigRecord?>(record);

        public Task<bool> UpdateSansAndHostNameByHostNameAsync(
            string hostName,
            string sansJson,
            string platformA,
            string platformB,
            CancellationToken cancellationToken = default)
        {
            UpdateCallCount++;
            SavedPlatformA = platformA;
            SavedPlatformB = platformB;
            SavedSansJson = sansJson;
            return Task.FromResult(true);
        }

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

    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
