using System.Text.Json;
using TradeDesktop.Application.Helpers;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Tests.Config;

public sealed class SansJsonCTraderFixTests
{
    private static readonly IReadOnlyList<ManualHwndColumnConfig> Columns =
    [
        new ManualHwndColumnConfig("0x002C0C64", "0x0002076C", "0x003C0F62", "0x00020AEC")
    ];

    // Hình dạng sans_json thật của máy MT-MT hiện tại (chỉ số HWND công khai, không có dữ liệu nhạy cảm).
    private const string ProductionSansJson =
        "{\"version\":2,\"mapNames\":[\"Local\\\\MT_A_Tick\",\"Local\\\\MT_B_Tick\"]," +
        "\"manualHwndColumns\":[{\"chartA\":\"0x002C0C64\",\"chartB\":\"0x003C0F62\",\"tradeA\":\"0x0002076C\",\"tradeB\":\"0x00020AEC\"}]}";

    [Fact]
    public void RoundTrip_WithCTraderFix_ReadsBackEveryFieldIncludingPassword()
    {
        var fix = CTraderFixConfigTests.FxProDemo("pa\"ss\\word");

        var json = SansJsonHelper.BuildSans("Local\\MT_A_Tick", "Local\\MT_B_Tick", Columns, fix);
        var ok = SansJsonHelper.TryParseSans(json, out var map1, out var map2, out var columns, out var parsed);

        Assert.True(ok);
        Assert.Equal("Local\\MT_A_Tick", map1);
        Assert.Equal("Local\\MT_B_Tick", map2);
        Assert.Equal(Columns[0], Assert.Single(columns));
        Assert.Equal(fix, parsed);
    }

    [Fact]
    public void RoundTrip_KeepsStoredMtMapName2TogetherWithCTraderFix()
    {
        // Quyết định Phase 2: chuyển mode KHÔNG xoá dữ liệu mode đang ẩn.
        var json = SansJsonHelper.BuildSans("Local\\MT_A_Tick", "Local\\MT_B_Tick", Columns, CTraderFixConfigTests.FxProDemo());

        SansJsonHelper.TryParseSans(json, out _, out var map2, out var columns, out var parsed);

        Assert.Equal("Local\\MT_B_Tick", map2);
        Assert.Equal("0x003C0F62", columns[0].ChartHwndB);
        Assert.Equal(41, parsed.SymbolId);
    }

    [Fact]
    public void BuildSans_EmptyCTraderFix_WritesNoKeyAndMatchesLegacyOutput()
    {
        var legacy = SansJsonHelper.BuildSans("Local\\MT_A_Tick", "Local\\MT_B_Tick", Columns);
        var withEmpty = SansJsonHelper.BuildSans("Local\\MT_A_Tick", "Local\\MT_B_Tick", Columns, CTraderFixConfig.Empty);
        var withNull = SansJsonHelper.BuildSans("Local\\MT_A_Tick", "Local\\MT_B_Tick", Columns, null);

        Assert.Equal(legacy, withEmpty);
        Assert.Equal(legacy, withNull);
        Assert.DoesNotContain("ctraderFix", withEmpty);
    }

    [Fact]
    public void TryParseSans_ProductionJsonWithoutCTraderFix_ParsesMapsAndColumnsAndReturnsEmpty()
    {
        var ok = SansJsonHelper.TryParseSans(ProductionSansJson, out var map1, out var map2, out var columns, out var fix);

        Assert.True(ok);
        Assert.Equal("Local\\MT_A_Tick", map1);
        Assert.Equal("Local\\MT_B_Tick", map2);
        var column = Assert.Single(columns);
        Assert.Equal("0x0002076C", column.TradeHwndA);
        Assert.Equal(CTraderFixConfig.Empty, fix);
    }

    [Fact]
    public void TryParseSans_LegacyOverloads_UnchangedByCTraderBlock()
    {
        var json = SansJsonHelper.BuildSans("A", "B", Columns, CTraderFixConfigTests.FxProDemo());

        Assert.True(SansJsonHelper.TryParseSans(json, out var m1, out var m2));
        Assert.Equal(("A", "B"), (m1, m2));
        Assert.True(SansJsonHelper.TryParseSans(json, out _, out _, out var columns));
        Assert.Equal(Columns[0], Assert.Single(columns));
    }

    [Fact]
    public void TryParseSans_MalformedCTraderBlock_DoesNotBreakMapsAndColumns()
    {
        const string json =
            "{\"version\":2,\"mapNames\":[\"A\",\"B\"],\"manualHwndColumns\":[{\"chartA\":\"1\",\"tradeA\":\"2\",\"chartB\":\"3\",\"tradeB\":\"4\"}]," +
            "\"ctraderFix\":{\"quote\":\"not-an-object\",\"symbolId\":\"41\",\"useSsl\":\"yes\",\"volumeBUnits\":null}}";

        var ok = SansJsonHelper.TryParseSans(json, out var map1, out var map2, out var columns, out var fix);

        Assert.True(ok);
        Assert.Equal(("A", "B"), (map1, map2));
        Assert.Single(columns);
        Assert.Equal(CTraderEndpoint.Empty, fix.Quote);
        Assert.Equal(0, fix.SymbolId);
        Assert.True(fix.UseSsl);
    }

    [Fact]
    public void TryParseSans_LegacyArrayFormat_ReturnsEmptyCTraderFix()
    {
        var ok = SansJsonHelper.TryParseSans("[\"A\",\"B\"]", out var map1, out _, out _, out var fix);

        Assert.True(ok);
        Assert.Equal("A", map1);
        Assert.Equal(CTraderFixConfig.Empty, fix);
    }

    [Fact]
    public void Redact_MasksPasswordAndKeepsOtherKeys()
    {
        var json = SansJsonHelper.BuildSans("A", "B", Columns, CTraderFixConfigTests.FxProDemo("TopSecret!"));

        var redacted = SansJsonHelper.Redact(json);

        Assert.DoesNotContain("TopSecret!", redacted);
        using var doc = JsonDocument.Parse(redacted);
        var fix = doc.RootElement.GetProperty("ctraderFix");
        Assert.Equal("***", fix.GetProperty("password").GetString());
        Assert.Equal("demo.fxpro.10649643", fix.GetProperty("senderCompId").GetString());
        Assert.Equal("A", doc.RootElement.GetProperty("mapNames")[0].GetString());
    }

    [Fact]
    public void Redact_JsonWithoutPassword_ReturnsInputUnchanged()
    {
        Assert.Equal(ProductionSansJson, SansJsonHelper.Redact(ProductionSansJson));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Redact_NullOrEmpty_ReturnsEmpty(string? input, string expected)
    {
        Assert.Equal(expected, SansJsonHelper.Redact(input));
    }

    [Fact]
    public void Redact_BrokenJson_DoesNotThrowAndStillMasks()
    {
        const string broken = "{\"ctraderFix\":{\"password\":\"LeakMe\",\"host\":";

        var redacted = SansJsonHelper.Redact(broken);

        Assert.DoesNotContain("LeakMe", redacted);
    }
}
