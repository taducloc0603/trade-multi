using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.PrimeXbt;

namespace TradeDesktop.Tests.PrimeXbt;

// Phase 2 (docs/plans/primexbt): khối sans_json.primexbt và ngưỡng latency B riêng.
public sealed class PrimeXbtConfigTests
{
    internal static PrimeXbtConfig Demo(int? confirmLatencyB = 2000) =>
        new("D1282507", "XAU/USD", 1m, 100m, 0.01m, confirmLatencyB);

    [Fact]
    public void Empty_NormalizeIsStillEmpty()
    {
        Assert.Equal(PrimeXbtConfig.Empty, PrimeXbtConfig.Empty.Normalize());
    }

    [Fact]
    public void Normalize_TrimsUppercasesAccountAndStripsHash_ClampsNumbers_KeepsLatency()
    {
        var raw = new PrimeXbtConfig("  #d1282507 ", " XAU/USD ", -1m, -5m, -0.01m, -3);

        var normalized = raw.Normalize();

        Assert.Equal("D1282507", normalized.AccountId);
        Assert.Equal("XAU/USD", normalized.Symbol);
        Assert.Equal(0m, normalized.VolumeBOz);
        Assert.Equal(0m, normalized.ContractSizeB);
        Assert.Equal(0m, normalized.VolumeALots);
        Assert.Equal(-3, normalized.ConfirmLatencyB); // không clamp, giống ctrader_confirm_latency_b
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0.01", true)]
    [InlineData("2.5", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("0.015", false)]
    [InlineData("0.001", false)]
    public void IsValidVolumeBOz_RequiresPositiveMultipleOfOrderStep(string value, bool expected)
    {
        Assert.Equal(expected, PrimeXbtConfig.IsValidVolumeBOz(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void GetMissingRequiredFields_CompleteConfig_IsEmpty()
    {
        Assert.Empty(Demo().GetMissingRequiredFields());
    }

    [Fact]
    public void GetMissingRequiredFields_Empty_ListsEveryRequiredField()
    {
        Assert.Equal(
            ["Account ID", "Symbol", "Volume B (oz)", "Contract size B"],
            PrimeXbtConfig.Empty.GetMissingRequiredFields());
    }

    [Fact]
    public void GetMissingRequiredFields_LatencyAndVolumeAAreOptional()
    {
        var config = Demo(confirmLatencyB: null) with { VolumeALots = 0m };

        Assert.Empty(config.GetMissingRequiredFields());
    }

    [Theory]
    [InlineData("D1282507", true)]
    [InlineData("d1282507", true)]
    [InlineData("L1852279", false)]
    [InlineData("", false)]
    public void IsDemoAccount_FollowsPxTraderPrefix(string accountId, bool expected)
    {
        Assert.Equal(expected, (PrimeXbtConfig.Empty with { AccountId = accountId }).IsDemoAccount);
    }

    [Theory]
    [InlineData("primexbt", 2000, 2000)]
    [InlineData("PRIMEXBT", 0, 0)]       // 0 = tắt guard riêng, KHÔNG bị bỏ qua
    [InlineData("primexbt", null, null)]
    [InlineData("mt5", 2000, null)]       // giá trị sót trong sans_json không áp dụng khi B là MT
    [InlineData("ctrader", 2000, null)]
    [InlineData(null, 2000, null)]
    public void ResolveConfirmLatencyB_OnlyAppliesWhenPlatformBIsPrimeXbt(string? platformB, int? configured, int? expected)
    {
        Assert.Equal(expected, PrimeXbtRoutingRules.ResolveConfirmLatencyB(platformB, Demo(configured)));
    }

    [Fact]
    public void ResolveConfirmLatencyB_NullConfig_IsNull()
    {
        Assert.Null(PrimeXbtRoutingRules.ResolveConfirmLatencyB("primexbt", null));
    }
}
