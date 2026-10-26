using System.Text.Json;
using TradeDesktop.Application.Helpers;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.TradeLocker;
using TradeDesktop.Tests.Config;
using TradeDesktop.Tests.PrimeXbt;

namespace TradeDesktop.Tests.TradeLocker;

// Phase 2 (docs/plans/tradelocker): khối `tradelocker` trong sans_json + ConfigService + ngưỡng latency B riêng.
public sealed class TradeLockerConfigTests
{
    internal static TradeLockerConfig Demo() =>
        new("1791342294192995800", "PRDTL", "XAUUSD", 0.01m, 100m, 0.01m, 2500);

    private static readonly IReadOnlyList<ManualHwndColumnConfig> Columns =
        [new ManualHwndColumnConfig("0x1", "0x2", "0x3", "0x4")];

    [Fact]
    public void Normalize_TrimsAndUppercasesBrandSymbol_ClampsNegativeVolumes()
    {
        var tl = new TradeLockerConfig(" 123 ", " prdtl ", " xauusd ", -1m, -2m, -3m, 0).Normalize();

        Assert.Equal("123", tl.AccountId);
        Assert.Equal("PRDTL", tl.Brand);
        Assert.Equal("XAUUSD", tl.Symbol);
        Assert.Equal(0m, tl.VolumeBLots);
        Assert.Equal(0m, tl.ContractSizeB);
        Assert.Equal(0m, tl.VolumeALots);
        Assert.Equal(0, tl.ConfirmLatencyB); // 0 = tắt guard, không bị đổi
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("1791342294192995800", true)]
    [InlineData("PRDTL#O#1791342294192995800", false)]
    [InlineData("12a", false)]
    [InlineData("12345678901234567890", false)] // quá 19 chữ số
    public void IsValidAccountId_AcceptsEmptyOrDigitsOnly(string value, bool expected)
    {
        Assert.Equal(expected, TradeLockerConfig.IsValidAccountId(value));
    }

    [Fact]
    public void GetMissingRequiredFields_DemoIsComplete_EmptyListsEverything()
    {
        Assert.Empty(Demo().GetMissingRequiredFields());

        var missing = TradeLockerConfig.Empty.GetMissingRequiredFields();
        Assert.Contains("Symbol", missing);
        Assert.Contains("Volume B (lot)", missing);
        Assert.Contains("Contract size B", missing);
        Assert.DoesNotContain("Account ID (chuỗi số)", missing); // trống = account đầu tiên, hợp lệ
    }

    [Theory]
    [InlineData(0.015)]
    [InlineData(0.001)]
    public void GetMissingRequiredFields_VolumeOffLotStep_IsRejected(double volume)
    {
        var tl = Demo() with { VolumeBLots = (decimal)volume };

        Assert.Contains("Volume B (lot)", tl.GetMissingRequiredFields());
    }

    [Fact]
    public void EffectiveBrand_DefaultsToPrdtl()
    {
        Assert.Equal("PRDTL", (Demo() with { Brand = "" }).EffectiveBrand);
        Assert.Equal("ABC", (Demo() with { Brand = "ABC" }).EffectiveBrand);
    }

    [Fact]
    public void Sans_BuildThenParse_RoundTripsTradeLockerBlock()
    {
        var json = SansJsonHelper.BuildSans("A", "B", Columns, null, null, Demo());

        Assert.True(SansJsonHelper.TryParseSans(json, out var m1, out var m2, out _, out _, out var px, out var tl));
        Assert.Equal("A", m1);
        Assert.Equal("B", m2);
        Assert.Equal(PrimeXbtConfig.Empty, px);
        Assert.Equal(Demo(), tl);
    }

    [Fact]
    public void Sans_EmptyOrNullTradeLocker_IsByteIdenticalToFiveArgOverload()
    {
        var px = PrimeXbtConfigTests.Demo();
        var fix = CTraderFixConfigTests.FxProDemo();

        Assert.Equal(SansJsonHelper.BuildSans("A", "B", Columns, null, null), SansJsonHelper.BuildSans("A", "B", Columns, null, null, null));
        Assert.Equal(SansJsonHelper.BuildSans("A", "B", Columns, fix, px), SansJsonHelper.BuildSans("A", "B", Columns, fix, px, TradeLockerConfig.Empty));
        // Chỉ điền mặc định trong form ⇒ ConfigViewModel trả Empty ⇒ không có key tradelocker.
        Assert.DoesNotContain("tradelocker", SansJsonHelper.BuildSans("A", "B", Columns, null, null, TradeLockerConfig.Empty));
    }

    [Fact]
    public void Sans_KeepsCTraderAndPrimeXbtBlocksAlongsideTradeLocker()
    {
        var json = SansJsonHelper.BuildSans("A", "B", Columns, CTraderFixConfigTests.FxProDemo(), PrimeXbtConfigTests.Demo(), Demo());

        Assert.True(SansJsonHelper.TryParseSans(json, out _, out _, out _, out var fix, out var px, out var tl));
        Assert.Equal(CTraderFixConfigTests.FxProDemo().Normalize(), fix);
        Assert.Equal(PrimeXbtConfigTests.Demo(), px);
        Assert.Equal(Demo(), tl);
    }

    [Fact]
    public void Sans_MalformedTradeLockerBlock_GivesEmptyWithoutBreakingRest()
    {
        const string json = """{"version":2,"mapNames":["A","B"],"manualHwndColumns":[],"tradelocker":{"volumeBLots":"abc","symbol":7}}""";

        Assert.True(SansJsonHelper.TryParseSans(json, out var m1, out _, out _, out _, out _, out var tl));
        Assert.Equal("A", m1);
        Assert.Equal(TradeLockerConfig.Empty, tl);
    }

    [Fact]
    public void Sans_OldPayloadWithoutBlock_GivesEmpty()
    {
        Assert.True(SansJsonHelper.TryParseSans(SansJsonHelper.BuildSans("A", "B"), out _, out _, out _, out _, out _, out var tl));
        Assert.Equal(TradeLockerConfig.Empty, tl);
    }

    [Fact]
    public async Task ConfigService_SaveAndLoad_CarryTradeLockerBlock()
    {
        var repository = new PlatformNormalizationTests.CapturingConfigRepository(PlatformNormalizationTests.BaseRecord);
        var service = new ConfigService(repository, new PlatformNormalizationTests.StubMachineIdentityService());

        var save = await service.SaveByMachineHostNameAsync("A", "B", "mt5", "tradelocker", tradeLocker: Demo());
        Assert.True(save.IsSuccess, save.Error);
        Assert.Equal("tradelocker", repository.SavedPlatformB);
        using (var doc = JsonDocument.Parse(repository.SavedSansJson!))
        {
            Assert.True(doc.RootElement.TryGetProperty("tradelocker", out _));
        }

        var loaded = await new ConfigService(
                new PlatformNormalizationTests.CapturingConfigRepository(
                    PlatformNormalizationTests.BaseRecord with { SansJson = repository.SavedSansJson!, PlatformB = "tradelocker" }),
                new PlatformNormalizationTests.StubMachineIdentityService())
            .LoadByMachineHostNameAsync();
        Assert.True(loaded.IsSuccess, loaded.Error);
        Assert.Equal(Demo(), loaded.TradeLocker);
    }

    [Theory]
    [InlineData("tradelocker", 2500)]
    [InlineData("TRADELOCKER", 2500)]
    [InlineData("primexbt", null)] // giá trị sót trong sans_json không áp dụng cho nền tảng khác
    [InlineData("mt5", null)]
    public void ResolveConfirmLatencyB_OnlyWhenPlatformIsTradeLocker(string platformB, int? expected)
    {
        Assert.Equal(expected, TradeLockerRoutingRules.ResolveConfirmLatencyB(platformB, Demo()));
    }

    [Fact]
    public void ResolveConfirmLatencyB_NullConfigOrNullValue_FallsBack()
    {
        Assert.Null(TradeLockerRoutingRules.ResolveConfirmLatencyB("tradelocker", null));
        Assert.Null(TradeLockerRoutingRules.ResolveConfirmLatencyB("tradelocker", Demo() with { ConfirmLatencyB = null }));
        Assert.Equal(0, TradeLockerRoutingRules.ResolveConfirmLatencyB("tradelocker", Demo() with { ConfirmLatencyB = 0 }));
    }
}
