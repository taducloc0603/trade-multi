using TradeDesktop.Application.Models;

namespace TradeDesktop.Tests.Config;

public sealed class CTraderFixConfigTests
{
    internal static CTraderFixConfig FxProDemo(string password = "p@ss-word") => new(
        new CTraderEndpoint("demo-uk-eqx-01.p.c-trader.com", 5211, 5201),
        new CTraderEndpoint("demo-uk-eqx-01.p.c-trader.com", 5212, 5202),
        UseSsl: true,
        SenderCompId: "demo.fxpro.10649643",
        TargetCompId: "cServer",
        Password: password,
        Username: string.Empty,
        SymbolId: 41,
        SymbolName: "XAUUSD",
        VolumeBUnits: 1m,
        ContractSizeB: 100m,
        VolumeALots: 0.01m);

    [Fact]
    public void ChannelMapName_IsFixedCTraderB()
    {
        Assert.Equal("CTRADER_B", CTraderFixConfig.ChannelMapName);
    }

    [Theory]
    [InlineData("demo.fxpro.10649643", "", "10649643")]
    [InlineData("live.x.y.z", "", "z")]
    [InlineData("nodots", "", "")]
    [InlineData("", "", "")]
    [InlineData("demo.fxpro.10649643", "override-user", "override-user")]
    public void ResolveUsername_DerivesFromSenderCompIdUnlessOverridden(string sender, string username, string expected)
    {
        var config = CTraderFixConfig.Empty with { SenderCompId = sender, Username = username };

        Assert.Equal(expected, config.ResolveUsername());
    }

    [Theory]
    [InlineData(true, CTraderSessionRole.Quote, 5211)]
    [InlineData(false, CTraderSessionRole.Quote, 5201)]
    [InlineData(true, CTraderSessionRole.Trade, 5212)]
    [InlineData(false, CTraderSessionRole.Trade, 5202)]
    public void ActivePort_FollowsUseSsl(bool useSsl, CTraderSessionRole role, int expected)
    {
        var config = FxProDemo() with { UseSsl = useSsl };

        Assert.Equal(expected, config.ActivePort(role));
    }

    [Theory]
    [InlineData("demo.fxpro.10649643", true)]
    [InlineData("DEMO.fxpro.1", true)]
    [InlineData("live.fxpro.8220816", false)]
    [InlineData("", false)]
    public void IsDemoSender_OnlyForDemoPrefix(string sender, bool expected)
    {
        Assert.Equal(expected, (CTraderFixConfig.Empty with { SenderCompId = sender }).IsDemoSender);
    }

    [Fact]
    public void ToString_NeverContainsPassword()
    {
        var text = FxProDemo("SuperSecret-123").ToString();

        Assert.DoesNotContain("SuperSecret-123", text);
        Assert.Contains("***", text);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0.01", true)]
    [InlineData("1.5", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("0.001", false)]
    public void IsValidVolumeBUnits_PositiveAndMultipleOfCent(string raw, bool expected)
    {
        Assert.Equal(expected, CTraderFixConfig.IsValidVolumeBUnits(decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("100", true)]
    [InlineData("0", false)]
    [InlineData("-5", false)]
    public void IsValidContractSize_MustBePositive(string raw, bool expected)
    {
        Assert.Equal(expected, CTraderFixConfig.IsValidContractSize(decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void GetMissingRequiredFields_CompleteFxProConfig_NothingMissing()
    {
        var config = FxProDemo();

        Assert.Empty(config.GetMissingRequiredFields(config.HasPassword));
    }

    [Fact]
    public void GetMissingRequiredFields_Empty_ReportsEveryRequiredField()
    {
        var missing = CTraderFixConfig.Empty.GetMissingRequiredFields(passwordAvailable: false);

        Assert.Contains("QUOTE host", missing);
        Assert.Contains("TRADE host", missing);
        Assert.Contains("SenderCompID", missing);
        Assert.Contains("Symbol ID", missing);
        Assert.Contains("Volume B (units)", missing);
        Assert.Contains("Contract size B", missing);
        Assert.Contains("Password", missing);
    }

    [Fact]
    public void GetMissingRequiredFields_PlainModeRequiresPlainPorts()
    {
        var config = FxProDemo() with
        {
            UseSsl = false,
            Quote = new CTraderEndpoint("h", 5211, 0),
            Trade = new CTraderEndpoint("h", 5212, 0)
        };

        var missing = config.GetMissingRequiredFields(passwordAvailable: true);

        Assert.Contains("QUOTE port plain", missing);
        Assert.Contains("TRADE port plain", missing);
    }

    [Fact]
    public void Normalize_TrimsAndClampsAndDefaultsTargetCompId()
    {
        var raw = new CTraderFixConfig(
            new CTraderEndpoint("  host  ", -1, 5201),
            new CTraderEndpoint(" t ", 5212, -3),
            UseSsl: true,
            SenderCompId: "  demo.fxpro.1 ",
            TargetCompId: "  ",
            Password: " keep spaces ",
            Username: " u ",
            SymbolId: -4,
            SymbolName: " XAUUSD ",
            VolumeBUnits: -1m,
            ContractSizeB: -100m,
            VolumeALots: -0.01m);

        var normalized = raw.Normalize();

        Assert.Equal(new CTraderEndpoint("host", 0, 5201), normalized.Quote);
        Assert.Equal(new CTraderEndpoint("t", 5212, 0), normalized.Trade);
        Assert.Equal("demo.fxpro.1", normalized.SenderCompId);
        Assert.Equal("cServer", normalized.TargetCompId);
        Assert.Equal(" keep spaces ", normalized.Password);
        Assert.Equal(0, normalized.SymbolId);
        Assert.Equal(0m, normalized.VolumeBUnits);
        Assert.Equal(0m, normalized.ContractSizeB);
        Assert.Equal(0m, normalized.VolumeALots);
    }

    [Fact]
    public void Empty_IsStableUnderNormalize()
    {
        Assert.Equal(CTraderFixConfig.Empty, CTraderFixConfig.Empty.Normalize());
    }
}
