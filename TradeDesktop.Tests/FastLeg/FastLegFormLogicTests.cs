using TradeDesktop.Application.Services.FastLeg;

namespace TradeDesktop.Tests.FastLeg;

public sealed class FastLegFormLogicTests
{
    // ---------- Số thập phân: chỉ dấu chấm ----------

    [Theory]
    [InlineData("5", 5)]
    [InlineData("5.5", 5.5)]
    [InlineData(" 12.25 ", 12.25)]
    [InlineData("0.5", 0.5)]
    public void TryParseDecimal_DotDecimal_IsAccepted(string text, double expected)
    {
        Assert.True(FastLegFormLogic.TryParseDecimal(text, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("5,5")]      // NumberStyles.Number sẽ đọc thành 55 — đúng lỗi phải chặn
    [InlineData("1,000")]
    [InlineData("-5")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(null)]
    public void TryParseDecimal_CommaSignOrGarbage_IsRejected(string? text)
    {
        Assert.False(FastLegFormLogic.TryParseDecimal(text, out _));
    }

    [Theory]
    [InlineData("200", true, 200)]
    [InlineData(" 1000 ", true, 1000)]
    [InlineData("1,000", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("2.5", false, 0)]
    public void TryParseInt_OnlyPlainDigits(string text, bool ok, int expected)
    {
        Assert.Equal(ok, FastLegFormLogic.TryParseInt(text, out var value));
        if (ok)
        {
            Assert.Equal(expected, value);
        }
    }

    // ---------- Quyết định ghi cột fast_leg ----------

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void NotDirty_NeverWrites(bool formEnabled, bool hasStoredJson, bool runtimeActive)
    {
        // Lưu chỉ để sửa A/B không được ghi đè cột fast_leg (giữ heartbeat tuỳ chỉnh, không thay cấu hình hỏng bằng mặc định).
        Assert.Equal(FastLegWriteKind.None, FastLegFormLogic.Decide(formEnabled, formDirty: false, hasStoredJson, runtimeActive));
    }

    [Fact]
    public void DirtyAndTicked_WritesEnabled()
    {
        Assert.Equal(FastLegWriteKind.Enabled, FastLegFormLogic.Decide(true, true, hasStoredJson: false, runtimeActive: false));
    }

    [Fact]
    public void DirtyUntickedOnMachineWithoutFastLeg_WritesNothing()
    {
        // Máy chưa từng cấu hình sàn C: tick rồi bỏ tick ⇒ cột giữ NULL, hành vi y như cũ.
        Assert.Equal(FastLegWriteKind.None, FastLegFormLogic.Decide(false, true, hasStoredJson: false, runtimeActive: false));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DirtyUnticked_WithStoredOrRunningFastLeg_WritesDisabled(bool hasStoredJson, bool runtimeActive)
    {
        Assert.Equal(FastLegWriteKind.Disabled, FastLegFormLogic.Decide(false, true, hasStoredJson, runtimeActive));
    }
}
