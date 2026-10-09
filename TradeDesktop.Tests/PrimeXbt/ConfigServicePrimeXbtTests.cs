using System.Text.Json;
using TradeDesktop.Application.Helpers;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Tests.Config;

namespace TradeDesktop.Tests.PrimeXbt;

// Phase 2: ConfigService đọc/ghi khối primexbt qua sans_json, không đụng cột DB.
public sealed class ConfigServicePrimeXbtTests
{
    private static ConfigService BuildService(PlatformNormalizationTests.CapturingConfigRepository repository) =>
        new(repository, new PlatformNormalizationTests.StubMachineIdentityService());

    [Fact]
    public async Task Save_WithPrimeXbt_WritesBlockAndPlatform()
    {
        var repository = new PlatformNormalizationTests.CapturingConfigRepository(PlatformNormalizationTests.BaseRecord);

        var result = await BuildService(repository).SaveByMachineHostNameAsync(
            "A", "B", "mt5", "primexbt", primeXbt: PrimeXbtConfigTests.Demo());

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("primexbt", repository.SavedPlatformB);
        Assert.True(SansJsonHelper.TryParseSans(repository.SavedSansJson, out _, out _, out _, out _, out var saved));
        Assert.Equal(PrimeXbtConfigTests.Demo(), saved);
    }

    [Fact]
    public async Task Save_WithoutPrimeXbt_WritesNoBlock()
    {
        var repository = new PlatformNormalizationTests.CapturingConfigRepository(PlatformNormalizationTests.BaseRecord);

        var result = await BuildService(repository).SaveByMachineHostNameAsync("A", "B", "mt5", "mt5");

        Assert.True(result.IsSuccess, result.Error);
        Assert.DoesNotContain("primexbt", repository.SavedSansJson);
    }

    [Fact]
    public async Task Save_WithCTraderAndPrimeXbt_KeepsBoth()
    {
        var repository = new PlatformNormalizationTests.CapturingConfigRepository(PlatformNormalizationTests.BaseRecord);
        var fix = CTraderFixConfigTests.FxProDemo();

        await BuildService(repository).SaveByMachineHostNameAsync(
            "A", "B", "mt5", "primexbt", ctraderFix: fix, primeXbt: PrimeXbtConfigTests.Demo());

        using var doc = JsonDocument.Parse(repository.SavedSansJson!);
        Assert.True(doc.RootElement.TryGetProperty("ctraderFix", out _));
        Assert.True(doc.RootElement.TryGetProperty("primexbt", out _));
    }

    [Fact]
    public async Task Load_ReturnsPrimeXbtBlock()
    {
        var sans = SansJsonHelper.BuildSans("A", "B", null, null, PrimeXbtConfigTests.Demo());
        var repository = new PlatformNormalizationTests.CapturingConfigRepository(
            PlatformNormalizationTests.BaseRecord with { SansJson = sans, PlatformB = "primexbt" });

        var result = await BuildService(repository).LoadByMachineHostNameAsync();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("primexbt", result.PlatformB);
        Assert.Equal(PrimeXbtConfigTests.Demo(), result.PrimeXbt);
    }

    [Fact]
    public async Task Load_OldSans_GivesEmptyPrimeXbt()
    {
        var repository = new PlatformNormalizationTests.CapturingConfigRepository(
            PlatformNormalizationTests.BaseRecord with { SansJson = SansJsonHelper.BuildSans("A", "B") });

        var result = await BuildService(repository).LoadByMachineHostNameAsync();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(PrimeXbtConfig.Empty, result.PrimeXbt);
    }
}
