using System.Text.Json;
using TradeDesktop.Application.Helpers;
using TradeDesktop.Application.Models;
using TradeDesktop.Tests.Config;

namespace TradeDesktop.Tests.PrimeXbt;

// Phase 2: khối `primexbt` trong sans_json không được làm mất/đổi dữ liệu cũ (mapNames, manualHwndColumns, ctraderFix).
public sealed class SansJsonPrimeXbtTests
{
    private static readonly IReadOnlyList<ManualHwndColumnConfig> Columns =
        [new ManualHwndColumnConfig("0x00180EBA", "0x00EC0DE0", "0x000611A6", "0x000210F6")];

    [Fact]
    public void RoundTrip_ReadsBackEveryField()
    {
        var px = PrimeXbtConfigTests.Demo();

        var json = SansJsonHelper.BuildSans("Local\\MT_A_Tick", "Local\\MT_B_Tick", Columns, null, px);
        Assert.True(SansJsonHelper.TryParseSans(json, out var map1, out var map2, out var columns, out var fix, out var parsed));

        Assert.Equal("Local\\MT_A_Tick", map1);
        Assert.Equal("Local\\MT_B_Tick", map2);
        Assert.Equal(Columns[0], columns[0]);
        Assert.Equal(CTraderFixConfig.Empty, fix);
        Assert.Equal(px, parsed);
    }

    [Fact]
    public void RoundTrip_NullLatency_StaysNull()
    {
        var px = PrimeXbtConfigTests.Demo(confirmLatencyB: null);

        var json = SansJsonHelper.BuildSans("A", "B", Columns, null, px);
        SansJsonHelper.TryParseSans(json, out _, out _, out _, out _, out var parsed);

        Assert.Null(parsed.ConfirmLatencyB);
    }

    [Fact]
    public void Build_WithBothCTraderAndPrimeXbt_KeepsBothBlocks()
    {
        var fix = CTraderFixConfigTests.FxProDemo();
        var px = PrimeXbtConfigTests.Demo();

        var json = SansJsonHelper.BuildSans("A", "B", Columns, fix, px);
        SansJsonHelper.TryParseSans(json, out _, out _, out _, out var parsedFix, out var parsedPx);

        Assert.Equal(fix.Normalize(), parsedFix);
        Assert.Equal(px, parsedPx);
    }

    [Fact]
    public void Build_WithEmptyOrNullPrimeXbt_IsByteIdenticalToPreviousOverload()
    {
        var fix = CTraderFixConfigTests.FxProDemo();
        var legacyMt = SansJsonHelper.BuildSans("A", "B", Columns);
        var legacyCTrader = SansJsonHelper.BuildSans("A", "B", Columns, fix);

        Assert.Equal(legacyMt, SansJsonHelper.BuildSans("A", "B", Columns, null, null));
        Assert.Equal(legacyMt, SansJsonHelper.BuildSans("A", "B", Columns, null, PrimeXbtConfig.Empty));
        Assert.Equal(legacyCTrader, SansJsonHelper.BuildSans("A", "B", Columns, fix, PrimeXbtConfig.Empty));
        Assert.DoesNotContain("primexbt", legacyMt);
    }

    [Fact]
    public void Parse_JsonWithoutBlock_GivesEmpty()
    {
        var json = SansJsonHelper.BuildSans("A", "B", Columns);

        Assert.True(SansJsonHelper.TryParseSans(json, out _, out _, out _, out _, out var parsed));
        Assert.Equal(PrimeXbtConfig.Empty, parsed);
    }

    [Fact]
    public void Parse_MalformedBlock_DoesNotBreakMapsOrColumns()
    {
        const string json = """
            {"version":2,"mapNames":["A","B"],
             "manualHwndColumns":[{"chartA":"0x1","tradeA":"0x2","chartB":"0x3","tradeB":"0x4"}],
             "primexbt":{"accountId":123,"symbol":"XAU/USD","volumeBOz":"1","contractSizeB":null,"confirmLatencyB":"2000"}}
            """;

        Assert.True(SansJsonHelper.TryParseSans(json, out var map1, out var map2, out var columns, out _, out var parsed));

        Assert.Equal("A", map1);
        Assert.Equal("B", map2);
        Assert.Equal("0x4", columns[0].TradeHwndB);
        Assert.Equal(string.Empty, parsed.AccountId);   // số không phải chuỗi ⇒ rỗng
        Assert.Equal("XAU/USD", parsed.Symbol);
        Assert.Equal(0m, parsed.VolumeBOz);              // chuỗi không phải số ⇒ 0
        Assert.Null(parsed.ConfirmLatencyB);
    }

    [Fact]
    public void Parse_LegacyArrayFormat_GivesEmpty()
    {
        Assert.True(SansJsonHelper.TryParseSans("""["A","B"]""", out _, out _, out _, out _, out var parsed));
        Assert.Equal(PrimeXbtConfig.Empty, parsed);
    }

    [Fact]
    public void OldFourOutOverload_StillParsesCTraderWhenPrimeXbtPresent()
    {
        var fix = CTraderFixConfigTests.FxProDemo();
        var json = SansJsonHelper.BuildSans("A", "B", Columns, fix, PrimeXbtConfigTests.Demo());

        Assert.True(SansJsonHelper.TryParseSans(json, out _, out _, out _, out var parsedFix));
        Assert.Equal(fix.Normalize(), parsedFix);
    }

    [Fact]
    public void Build_PrimeXbtBlock_HasExpectedKeysAndNoSecrets()
    {
        var json = SansJsonHelper.BuildSans("A", "B", Columns, null, PrimeXbtConfigTests.Demo());
        using var doc = JsonDocument.Parse(json);
        var block = doc.RootElement.GetProperty("primexbt");

        Assert.Equal("D1282507", block.GetProperty("accountId").GetString());
        Assert.Equal(2000, block.GetProperty("confirmLatencyB").GetInt32());
        Assert.False(block.TryGetProperty("jwt", out _));
        Assert.DoesNotContain("eyJ", json);
    }
}
