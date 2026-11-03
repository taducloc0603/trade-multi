using TradeDesktop.Application.Models;

namespace TradeDesktop.Tests.FastLeg;

// Cột JSONB `fast_leg` (docs/plans/fast-leg Phase 1). Cấu hình hỏng KHÔNG được ném lỗi và luôn ra Disabled.
public sealed class FastLegConfigTests
{
    private const string MapA = @"Local\MT_A_Tick";
    private const string MapB = @"Local\MT_B_Tick";

    // Thông số Long đang chạy (slide fast-leg + ảnh chụp modal).
    private const string LongJson =
        """
        {"enabled":true,"mapName":"Local\\MT_C_Tick","heartbeatMapName":"Local\\MT_C_Trades","symbol":"XAUUSD",
         "mode":"filter","rule":"quiet","maxMovePts":5,"windowMs":200,"maxAgeMs":1000,"maxDeferMs":3000,
         "failOpen":false,"applyToExit":true}
        """;

    private static (bool Ok, FastLegConfig Config, string? Error) Parse(string? json)
    {
        var ok = FastLegConfig.TryParse(json, MapA, MapB, out var config, out var error);
        return (ok, config, error);
    }

    [Fact]
    public void LongParameters_MapExactly()
    {
        var (ok, cfg, error) = Parse(LongJson);

        Assert.True(ok, error);
        Assert.Null(error);
        Assert.True(cfg.IsActive);
        Assert.True(cfg.IsFilter);
        Assert.Equal(@"Local\MT_C_Tick", cfg.MapName);
        Assert.Equal(@"Local\MT_C_Trades", cfg.HeartbeatMapName);
        Assert.Equal("XAUUSD", cfg.Symbol);
        Assert.Equal(FastLegMode.Filter, cfg.Mode);
        Assert.Equal(5m, cfg.MaxMovePts);
        Assert.Equal(200, cfg.WindowMs);
        Assert.Equal(1000, cfg.MaxAgeMs);
        Assert.Equal(3000, cfg.MaxDeferMs);
        Assert.False(cfg.FailOpen);
        Assert.True(cfg.ApplyToExit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyColumn_IsDisabledWithoutError(string? json)
    {
        var (ok, cfg, error) = Parse(json);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Same(FastLegConfig.Disabled, cfg);
        Assert.False(cfg.IsActive);
        Assert.Equal("disabled", cfg.ToSummary());
    }

    [Fact]
    public void EnabledFalse_IsDisabledAndSkipsValidation()
    {
        // Cấu hình dở dang nhưng đã tắt: không được sinh lỗi.
        var (ok, cfg, error) = Parse("""{"enabled":false,"maxMovePts":-1,"rule":"momentum"}""");

        Assert.True(ok);
        Assert.Null(error);
        Assert.False(cfg.IsActive);
    }

    [Fact]
    public void MissingKeys_UseDefaults()
    {
        var (ok, cfg, error) = Parse("""{"enabled":true}""");

        Assert.True(ok, error);
        Assert.Equal(FastLegConfig.DefaultMapName, cfg.MapName);
        Assert.Equal(@"Local\MT_C_Trades", cfg.HeartbeatMapName);
        Assert.Equal(string.Empty, cfg.Symbol);
        // Thiếu mode ⇒ observe: bật F mà quên mode thì không được âm thầm chặn lệnh.
        Assert.Equal(FastLegMode.Observe, cfg.Mode);
        Assert.False(cfg.IsFilter);
        Assert.Equal(FastLegConfig.DefaultMaxMovePts, cfg.MaxMovePts);
        Assert.Equal(FastLegConfig.DefaultWindowMs, cfg.WindowMs);
        Assert.Equal(FastLegConfig.DefaultMaxAgeMs, cfg.MaxAgeMs);
        Assert.Equal(FastLegConfig.DefaultMaxDeferMs, cfg.MaxDeferMs);
        Assert.False(cfg.FailOpen);
        Assert.False(cfg.ApplyToExit);
    }

    [Fact]
    public void SnakeCaseKeys_AreAccepted()
    {
        var (ok, cfg, error) = Parse(
            """{"enabled":true,"map_name":"Local\\MT_X_Tick","max_move_pts":4.5,"window_ms":150,"max_age_ms":800,"max_defer_ms":2000,"fail_open":true,"apply_to_exit":true,"mode":"FILTER"}""");

        Assert.True(ok, error);
        Assert.Equal(@"Local\MT_X_Tick", cfg.MapName);
        Assert.Equal(@"Local\MT_X_Trades", cfg.HeartbeatMapName);
        Assert.Equal(4.5m, cfg.MaxMovePts);
        Assert.Equal(150, cfg.WindowMs);
        Assert.Equal(800, cfg.MaxAgeMs);
        Assert.Equal(2000, cfg.MaxDeferMs);
        Assert.True(cfg.FailOpen);
        Assert.True(cfg.ApplyToExit);
        Assert.Equal(FastLegMode.Filter, cfg.Mode);
    }

    [Theory]
    [InlineData("""{"enabled":true,"rule":"momentum"}""", "rule")]
    [InlineData("""{"enabled":true,"mode":"block"}""", "mode")]
    [InlineData("""{"enabled":true,"maxMovePts":0}""", "maxMovePts")]
    [InlineData("""{"enabled":true,"maxMovePts":-3}""", "maxMovePts")]
    [InlineData("""{"enabled":true,"maxMovePts":"5"}""", "maxMovePts")]
    [InlineData("""{"enabled":true,"windowMs":0}""", "windowMs")]
    [InlineData("""{"enabled":true,"windowMs":5001}""", "windowMs")]
    [InlineData("""{"enabled":true,"windowMs":200.5}""", "windowMs")]
    [InlineData("""{"enabled":true,"maxAgeMs":0}""", "maxAgeMs")]
    [InlineData("""{"enabled":true,"maxDeferMs":499}""", "maxDeferMs")]
    [InlineData("""{"enabled":true,"maxDeferMs":10001}""", "maxDeferMs")]
    [InlineData("""{"enabled":true,"mapName":""}""", "mapName")]
    [InlineData("""{"enabled":true,"failOpen":"no"}""", "failOpen")]
    [InlineData("""{"enabled":"yes"}""", "enabled")]
    public void InvalidValue_DisablesWithError(string json, string expectedField)
    {
        var (ok, cfg, error) = Parse(json);

        Assert.False(ok);
        Assert.False(cfg.IsActive);
        Assert.NotNull(error);
        Assert.Contains(expectedField, error);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public void MalformedJson_DisablesWithError(string json)
    {
        var (ok, cfg, error) = Parse(json);

        Assert.False(ok);
        Assert.False(cfg.IsActive);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData(@"Local\MT_A_Tick")]
    [InlineData(@"local\mt_b_tick")]
    [InlineData(@" Local\MT_B_Tick ")]
    public void MapNameEqualToExchangeAOrB_IsRejected(string mapName)
    {
        var json = $$"""{"enabled":true,"mapName":{{System.Text.Json.JsonSerializer.Serialize(mapName)}}}""";

        var (ok, cfg, error) = Parse(json);

        Assert.False(ok);
        Assert.False(cfg.IsActive);
        Assert.Contains("A/B", error);
    }

    [Theory]
    [InlineData(@"Local\MT_C_Tick")]
    [InlineData(@"Local\MT_A_Trades")]
    [InlineData(@"Local\MT_B_Trades")]
    public void HeartbeatMapCollision_IsRejected(string heartbeat)
    {
        var json = $$"""{"enabled":true,"heartbeatMapName":{{System.Text.Json.JsonSerializer.Serialize(heartbeat)}}}""";

        var (ok, _, error) = Parse(json);

        Assert.False(ok);
        Assert.Contains("heartbeatMapName", error);
    }

    [Fact]
    public void EmptyHeartbeat_IsAllowedAndMeansNoHeartbeat()
    {
        var (ok, cfg, error) = Parse("""{"enabled":true,"heartbeatMapName":""}""");

        Assert.True(ok, error);
        Assert.Equal(string.Empty, cfg.HeartbeatMapName);
        Assert.Contains("hb=n/a", cfg.ToSummary());
    }

    [Fact]
    public void NoExchangeMapNames_DoesNotRejectDefaultMap()
    {
        var ok = FastLegConfig.TryParse("""{"enabled":true}""", null, "", out var cfg, out var error);

        Assert.True(ok, error);
        Assert.True(cfg.IsActive);
    }

    [Fact]
    public void Summary_ContainsEveryKnob()
    {
        var (_, cfg, _) = Parse(LongJson);

        Assert.Equal(
            @"filter|quiet|move5|win200|age1000|defer3000|failOpen0|exit1|map=Local\MT_C_Tick|hb=Local\MT_C_Trades|sym=XAUUSD",
            cfg.ToSummary());
    }

    [Theory]
    [InlineData(@"Local\MT_C_Tick", @"Local\MT_C_Trades")]
    [InlineData(@"Local\MT_Z_TICK", @"Local\MT_Z_Trades")]
    [InlineData(@"Local\SomethingElse", "")]
    [InlineData(null, "")]
    public void DeriveHeartbeatMapName_FollowsEaConvention(string? tick, string expected)
    {
        Assert.Equal(expected, FastLegConfig.DeriveHeartbeatMapName(tick));
    }
}
