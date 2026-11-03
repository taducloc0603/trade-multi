using System.Net;
using System.Text;
using System.Text.Json;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Infrastructure.Supabase;

namespace TradeDesktop.Tests.FastLeg;

// Mục "Sàn C" trong cửa sổ Config: JSON ghi ra phải đọc lại được bằng đúng TryParse của runtime,
// và lệnh ghi chỉ được đụng cột fast_leg.
public sealed class FastLegConfigSaveTests
{
    private const string MapA = @"Local\MT_A_Tick";
    private const string MapB = @"Local\MT_B_Tick";

    // ---------- FastLegConfig.ToJson / ReadDraft / FormDefaults ----------

    [Fact]
    public void ToJsonEnabled_RoundTripsThroughTryParse()
    {
        var source = FastLegConfig.FormDefaults with
        {
            Symbol = "XAUUSD.lmx",
            Mode = FastLegMode.Filter,
            MaxMovePts = 7.5m,
            WindowMs = 250,
            MaxAgeMs = 1500,
            MaxDeferMs = 4000,
            FailOpen = true,
            ApplyToExit = false
        };

        Assert.True(FastLegConfig.TryParse(source.ToJson(enabled: true), MapA, MapB, out var parsed, out var error), error);
        Assert.Equal(source, parsed);
    }

    [Fact]
    public void ToJsonDisabled_ParsesAsDisabledWithoutError_ButDraftKeepsValues()
    {
        var source = FastLegConfig.FormDefaults with { Symbol = "XAUUSD", MaxMovePts = 9m, Mode = FastLegMode.Filter };
        var json = source.ToJson(enabled: false);

        Assert.True(FastLegConfig.TryParse(json, MapA, MapB, out var parsed, out var error));
        Assert.Null(error);
        Assert.False(parsed.IsActive);

        var draft = FastLegConfig.ReadDraft(json, MapA, MapB);
        Assert.NotNull(draft);
        Assert.Equal("XAUUSD", draft!.Symbol);
        Assert.Equal(9m, draft.MaxMovePts);
        Assert.Equal(FastLegMode.Filter, draft.Mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"enabled\":false,\"mapName\":\"Local\\\\MT_A_Tick\"}")]
    public void ReadDraft_EmptyBrokenOrInvalid_ReturnsNull(string json)
    {
        Assert.Null(FastLegConfig.ReadDraft(json, MapA, MapB));
    }

    [Fact]
    public void FormDefaults_AreValidLongParametersInObserveMode()
    {
        Assert.True(FastLegConfig.TryParse(FastLegConfig.FormDefaults.ToJson(enabled: true), MapA, MapB, out var parsed, out var error), error);
        Assert.Equal(FastLegMode.Observe, parsed.Mode);
        Assert.Equal(5m, parsed.MaxMovePts);
        Assert.Equal(200, parsed.WindowMs);
        Assert.Equal(1000, parsed.MaxAgeMs);
        Assert.Equal(@"Local\MT_C_Trades", parsed.HeartbeatMapName);
    }

    // ---------- Repository: PATCH chỉ có cột fast_leg ----------

    [Fact]
    public async Task UpdateFastLeg_PatchesOnlyFastLegColumnOfThisHost()
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        var repository = new SupabaseConfigRepository(httpClient, "https://example.test", "key");

        var ok = await repository.UpdateFastLegAsync("TEST-HOST ", FastLegConfig.FormDefaults.ToJson(enabled: true));

        Assert.True(ok);
        Assert.Equal(HttpMethod.Patch, handler.Method);
        Assert.Contains("hostname=eq.test-host", handler.Uri, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(handler.Body!);
        var properties = body.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new List<string> { "fast_leg" }, properties);
        Assert.True(body.RootElement.GetProperty("fast_leg").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task UpdateFastLeg_NullJson_WritesNullColumn()
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        var repository = new SupabaseConfigRepository(httpClient, "https://example.test", "key");

        await repository.UpdateFastLegAsync("test-host", null);

        Assert.Equal("{\"fast_leg\":null}", handler.Body);
    }

    [Fact]
    public async Task UpdateFastLeg_EmptyHost_DoesNotSend()
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        var repository = new SupabaseConfigRepository(httpClient, "https://example.test", "key");

        Assert.False(await repository.UpdateFastLegAsync("  ", "{}"));
        Assert.Null(handler.Method);
    }

    // ---------- ConfigService ----------

    [Fact]
    public async Task SaveFastLeg_ForwardsJsonToRepository_AndSaveOfAbDoesNotTouchFastLeg()
    {
        var repository = new RecordingRepository();
        var service = new ConfigService(repository, new StubMachineIdentityService());

        var saveAb = await service.SaveByMachineHostNameAsync(MapA, MapB, "mt5", "mt5", [ManualHwndColumnConfig.Empty]);
        Assert.Equal(0, repository.FastLegCalls);

        var json = FastLegConfig.FormDefaults.ToJson(enabled: true);
        var result = await service.SaveFastLegByMachineHostNameAsync(json);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(1, repository.FastLegCalls);
        Assert.Equal("test-host", repository.FastLegHost);
        Assert.Equal(json, repository.FastLegJson);
        _ = saveAb;
    }

    [Fact]
    public async Task SaveFastLeg_RepositoryUpdatesNothing_Fails()
    {
        var repository = new RecordingRepository { FastLegResult = false };
        var service = new ConfigService(repository, new StubMachineIdentityService());

        var result = await service.SaveFastLegByMachineHostNameAsync(null);

        Assert.False(result.IsSuccess);
        Assert.Contains("Sàn C", result.Error);
    }

    [Fact]
    public async Task Load_ExposesRawJson_EvenWhenDisabled()
    {
        var disabled = (FastLegConfig.FormDefaults with { Symbol = "XAUUSD" }).ToJson(enabled: false);
        var json =
            """
            [{"id":"config-id","hostname":"test-host","sans":["Local\\MT_A_Tick","Local\\MT_B_Tick"],"platform_a":"mt5","platform_b":"mt5","point":100,
              "signal_cycle_size":10,
              "open_gap_absolute_floor":10,"open_gap_relative_tolerance":0.5,"open_gap_mad_multiplier":4.0,
              "open_gap_min_stable_samples":3,"open_gap_max_dispersion":0.35,"open_gap_max_drift":0.4,
              "close_gap_absolute_floor":10,"close_gap_relative_tolerance":0.6,"close_gap_mad_multiplier":4.0,
              "close_gap_min_stable_samples":3,"close_gap_max_dispersion":0.45,"close_gap_max_drift":0.6,
              "fast_leg":
            """ + disabled + "}]";
        using var httpClient = new HttpClient(new StaticJsonHandler(json));
        var service = new ConfigService(
            new SupabaseConfigRepository(httpClient, "https://example.test", "key"),
            new StubMachineIdentityService());

        var result = await service.LoadByMachineHostNameAsync();

        Assert.True(result.IsSuccess, result.Error);
        Assert.False(result.FastLeg!.IsActive);
        Assert.Equal("XAUUSD", FastLegConfig.ReadDraft(result.FastLegJson, result.MapName1, result.MapName2)!.Symbol);
    }

    private sealed class StubMachineIdentityService : IMachineIdentityService
    {
        public string GetRawHostName() => "TEST-HOST";
        public string GetHostName() => "test-host";
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string Uri { get; private set; } = string.Empty;
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri?.ToString() ?? string.Empty;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"id\":\"x\"}]", Encoding.UTF8, "application/json")
            };
        }
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

    private sealed class RecordingRepository : IConfigRepository
    {
        public int FastLegCalls { get; private set; }
        public string? FastLegHost { get; private set; }
        public string? FastLegJson { get; private set; }
        public bool FastLegResult { get; init; } = true;

        public Task<ConfigRecord?> GetByHostNameAsync(string hostName, CancellationToken cancellationToken = default) =>
            Task.FromResult<ConfigRecord?>(TradeDesktop.Tests.Config.PlatformNormalizationTests.BaseRecord);

        public Task<bool> UpdateSansAndHostNameByHostNameAsync(
            string hostName, string sansJson, string platformA, string platformB, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> UpdateCurrentTicksAsync(
            string hostName, string currentTickA, string currentTickB, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> UpdateCurrentSlotsAsync(
            string hostName, string currentSlotsJson, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> UpdateFastLegAsync(string hostName, string? fastLegJson, CancellationToken cancellationToken = default)
        {
            FastLegCalls++;
            FastLegHost = hostName;
            FastLegJson = fastLegJson;
            return Task.FromResult(FastLegResult);
        }
    }
}
