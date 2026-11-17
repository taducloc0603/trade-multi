using System.Net;
using System.Text;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Infrastructure.Supabase;

namespace TradeDesktop.Tests.Config;

/// <summary>
/// hold_confirm_mode đi DB → SupabaseConfigRepository (cả khối exact lẫn ilike) → ConfigRecord →
/// ConfigService (parse strict, fail-closed) → ConfigLoadResult. Phần RuntimeConfigState ở tầng App
/// (test project không tham chiếu) dùng updater riêng UpdateHoldConfirmMode.
/// </summary>
public sealed class HoldConfirmModeConfigMappingTests
{
    private static readonly GapStabilityConfig ValidStability = new(10, 0.50, 4.0, 3, 0.35, 0.40);

    private const string RowPrefix = """
        {"id":"config-id","hostname":"test-host","sans":[],"platform_a":"mt5","platform_b":"mt5","point":100,
         "open_gap_absolute_floor":10,"open_gap_relative_tolerance":0.5,"open_gap_mad_multiplier":4,
         "open_gap_min_stable_samples":3,"open_gap_max_dispersion":0.35,"open_gap_max_drift":0.4,
         "close_gap_absolute_floor":10,"close_gap_relative_tolerance":0.5,"close_gap_mad_multiplier":4,
         "close_gap_min_stable_samples":3,"close_gap_max_dispersion":0.35,"close_gap_max_drift":0.4
        """;

    // ---------- Repository ----------

    [Fact]
    public async Task Repository_ExactMatch_ReadsThreeColumns()
    {
        var record = await ReadAsync(
            $"[{RowPrefix},\"hold_confirm_mode\":\"ticks\",\"open_hold_confirm_ticks\":3,\"close_hold_confirm_ticks\":2}}]");

        Assert.Equal("ticks", record.HoldConfirmModeRaw);
        Assert.Equal(3, record.OpenHoldConfirmTicks);
        Assert.Equal(2, record.CloseHoldConfirmTicks);
    }

    [Fact]
    public async Task Repository_IlikeFallback_ReadsThreeColumns()
    {
        // Lần đầu (hostname=eq.) không có dòng nào ⇒ repository thử lại bằng ilike (khối parse thứ hai).
        var record = await ReadAsync(
            "[]",
            $"[{RowPrefix},\"hold_confirm_mode\":\"ticks\",\"open_hold_confirm_ticks\":4,\"close_hold_confirm_ticks\":5}}]");

        Assert.Equal("ticks", record.HoldConfirmModeRaw);
        Assert.Equal(4, record.OpenHoldConfirmTicks);
        Assert.Equal(5, record.CloseHoldConfirmTicks);
    }

    [Fact]
    public async Task Repository_MissingColumns_AreNullAndZero()
    {
        var record = await ReadAsync($"[{RowPrefix}}}]");

        Assert.Null(record.HoldConfirmModeRaw);
        Assert.Equal(0, record.OpenHoldConfirmTicks);
        Assert.Equal(0, record.CloseHoldConfirmTicks);
    }

    [Theory]
    [InlineData("\"MS\"", "MS")]
    [InlineData("\"ms \"", "ms ")]
    [InlineData("\"\"", "")]
    [InlineData("1", "1")]
    [InlineData("true", "true")]
    public async Task Repository_KeepsRawValue_WithoutNormalizing(string jsonValue, string expectedRaw)
    {
        var record = await ReadAsync($"[{RowPrefix},\"hold_confirm_mode\":{jsonValue}}}]");

        Assert.Equal(expectedRaw, record.HoldConfirmModeRaw);
    }

    [Fact]
    public async Task Repository_NullColumn_IsNull()
    {
        var record = await ReadAsync($"[{RowPrefix},\"hold_confirm_mode\":null}}]");

        Assert.Null(record.HoldConfirmModeRaw);
    }

    // ---------- ConfigService ----------

    [Fact]
    public async Task Service_Ticks_IsMappedToLoadResult()
    {
        var result = await BuildService(BuildRecord("ticks", 3, 2)).LoadByMachineHostNameAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(HoldConfirmMode.Ticks, result.HoldConfirmMode);
        Assert.Equal(3, result.OpenHoldConfirmTicks);
        Assert.Equal(2, result.CloseHoldConfirmTicks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ms")]
    public async Task Service_MsOrMissing_IsMs(string? raw)
    {
        var result = await BuildService(BuildRecord(raw, 0, 0)).LoadByMachineHostNameAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(HoldConfirmMode.Ms, result.HoldConfirmMode);
    }

    [Fact]
    public async Task Service_MsMode_KeepsTicksValuesForLaterSwitch()
    {
        var result = await BuildService(BuildRecord("ms", 4, 6)).LoadByMachineHostNameAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(HoldConfirmMode.Ms, result.HoldConfirmMode);
        Assert.Equal(4, result.OpenHoldConfirmTicks);
        Assert.Equal(6, result.CloseHoldConfirmTicks);
    }

    [Theory]
    [InlineData("tick")]
    [InlineData("MS")]
    [InlineData("Ticks")]
    [InlineData("ms ")]
    [InlineData(" ticks")]
    [InlineData("")]
    [InlineData("1")]
    public async Task Service_InvalidMode_RejectsConfig(string raw)
    {
        var result = await BuildService(BuildRecord(raw, 1, 1)).LoadByMachineHostNameAsync();

        Assert.False(result.IsSuccess);
        Assert.True(result.Exists);
        Assert.Contains("hold_confirm_mode", result.Error, StringComparison.Ordinal);
        Assert.Contains($"'{raw}'", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ticks", -1, 0)]
    [InlineData("ticks", 0, -1)]
    [InlineData("ms", -2, 0)]
    public async Task Service_NegativeTicks_RejectsConfig(string raw, int openTicks, int closeTicks)
    {
        var result = await BuildService(BuildRecord(raw, openTicks, closeTicks)).LoadByMachineHostNameAsync();

        Assert.False(result.IsSuccess);
        Assert.Contains("open_hold_confirm_ticks", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void NotFound_DefaultsToMs()
    {
        var result = ConfigLoadResult.NotFound("test-host");

        Assert.Equal(HoldConfirmMode.Ms, result.HoldConfirmMode);
        Assert.Equal(0, result.OpenHoldConfirmTicks);
        Assert.Equal(0, result.CloseHoldConfirmTicks);
    }

    // ---------- Parser ----------

    [Theory]
    [InlineData(null, HoldConfirmMode.Ms)]
    [InlineData("ms", HoldConfirmMode.Ms)]
    [InlineData("ticks", HoldConfirmMode.Ticks)]
    public void Parser_AcceptsExactValues(string? raw, HoldConfirmMode expected)
    {
        Assert.True(HoldConfirmModeParser.TryParse(raw, out var mode, out var error));
        Assert.Equal(expected, mode);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("TICKS")]
    [InlineData("tick")]
    [InlineData("\tms")]
    public void Parser_RejectsAnythingElse(string raw)
    {
        Assert.False(HoldConfirmModeParser.TryParse(raw, out var mode, out var error));
        Assert.Equal(HoldConfirmMode.Ms, mode);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Parser_ToConfigValue_RoundTrips()
    {
        Assert.Equal("ms", HoldConfirmModeParser.ToConfigValue(HoldConfirmMode.Ms));
        Assert.Equal("ticks", HoldConfirmModeParser.ToConfigValue(HoldConfirmMode.Ticks));
    }

    // ---------- Helpers ----------

    private static async Task<ConfigRecord> ReadAsync(params string[] responses)
    {
        using var httpClient = new HttpClient(new SequenceJsonHandler(responses));
        var repository = new SupabaseConfigRepository(httpClient, "https://example.test", "key");
        var record = await repository.GetByHostNameAsync("test-host");
        Assert.NotNull(record);
        return record!;
    }

    private static ConfigService BuildService(ConfigRecord record) =>
        new(new StubConfigRepository(record), new StubMachineIdentityService());

    private static ConfigRecord BuildRecord(string? holdConfirmMode, int openTicks, int closeTicks) =>
        new(
            Id: "config-id",
            SansJson: "[]",
            HostName: "test-host",
            PlatformA: "mt5",
            PlatformB: "mt5",
            Point: 100,
            OpenPts: 1,
            ConfirmGapPts: 0,
            HoldConfirmMs: 500,
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
            CloseHoldConfirmMs: 500,
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
            OpenGapStability: ValidStability,
            CloseGapStability: ValidStability,
            HoldConfirmModeRaw: holdConfirmMode,
            OpenHoldConfirmTicks: openTicks,
            CloseHoldConfirmTicks: closeTicks);

    private sealed class SequenceJsonHandler(IReadOnlyList<string> responses) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var json = responses[Math.Min(_index, responses.Count - 1)];
            _index++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

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

        public Task<bool> UpdateFastLegAsync(
            string hostName,
            string? fastLegJson,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
