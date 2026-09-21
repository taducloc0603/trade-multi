using TradeDesktop.Application.Helpers;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests.Config;

public sealed class ConfigServiceCTraderFixTests
{
    private static ConfigService BuildService(PlatformNormalizationTests.CapturingConfigRepository repository) =>
        new(repository, new PlatformNormalizationTests.StubMachineIdentityService());

    [Fact]
    public async Task Save_WithCTraderFix_WritesBlockIntoSansJson()
    {
        var repository = new PlatformNormalizationTests.CapturingConfigRepository(PlatformNormalizationTests.BaseRecord);
        var fix = CTraderFixConfigTests.FxProDemo();

        var result = await BuildService(repository).SaveByMachineHostNameAsync(
            "Local\\MT_A_Tick", "Local\\MT_B_Tick", "mt5", "ctrader", ctraderFix: fix);

        Assert.True(result.IsSuccess, result.Error);
        Assert.True(SansJsonHelper.TryParseSans(repository.SavedSansJson, out _, out var map2, out _, out var parsed));
        Assert.Equal("Local\\MT_B_Tick", map2);
        Assert.Equal(fix, parsed);
    }

    [Fact]
    public async Task Save_LegacyCallWithoutCTraderFix_WritesNoCTraderKey()
    {
        var repository = new PlatformNormalizationTests.CapturingConfigRepository(PlatformNormalizationTests.BaseRecord);

        var result = await BuildService(repository).SaveByMachineHostNameAsync("A", "B", "mt5", "mt5");

        Assert.True(result.IsSuccess, result.Error);
        Assert.DoesNotContain("ctraderFix", repository.SavedSansJson);
    }

    [Fact]
    public async Task Load_ReturnsCTraderFixFromSansJson()
    {
        var fix = CTraderFixConfigTests.FxProDemo();
        var sans = SansJsonHelper.BuildSans("Local\\MT_A_Tick", "Local\\MT_B_Tick", null, fix);
        var repository = new PlatformNormalizationTests.CapturingConfigRepository(
            PlatformNormalizationTests.BaseRecord with { SansJson = sans, PlatformB = "ctrader" });

        var result = await BuildService(repository).LoadByMachineHostNameAsync();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("Local\\MT_B_Tick", result.MapName2);
        Assert.Equal(fix, result.CTraderFix);
    }

    [Fact]
    public async Task Load_OldSansJson_ReturnsEmptyCTraderFix()
    {
        var repository = new PlatformNormalizationTests.CapturingConfigRepository(
            PlatformNormalizationTests.BaseRecord with
            {
                SansJson = "{\"version\":2,\"mapNames\":[\"A\",\"B\"],\"manualHwndColumns\":[]}"
            });

        var result = await BuildService(repository).LoadByMachineHostNameAsync();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(CTraderFixConfig.Empty, result.CTraderFix);
    }
}
