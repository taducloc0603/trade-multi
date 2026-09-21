using TradeDesktop.Application.Services.CTrader;

namespace TradeDesktop.Tests.Config;

// Phase 2 câu 4 — ba ca bắt buộc của plan + các ca biên.
public sealed class PlatformBSwitchGuardTests
{
    [Fact]
    public void OneOpenSlot_Mt5ToCTrader_IsRejectedWithSlotCount()
    {
        var decision = PlatformBSwitchGuard.Evaluate("mt5", "ctrader", openSlots: 1);

        Assert.False(decision.Allowed);
        Assert.Equal("Đang có 1 slot mở — đóng hết trước khi đổi nền tảng sàn B.", decision.Message);
    }

    [Fact]
    public void OneOpenSlot_Mt4ToMt5_IsAllowedAsBefore()
    {
        var decision = PlatformBSwitchGuard.Evaluate("mt4", "mt5", openSlots: 1);

        Assert.True(decision.Allowed);
        Assert.Null(decision.Message);
    }

    [Fact]
    public void NoOpenSlot_Mt5ToCTrader_IsAllowed()
    {
        Assert.True(PlatformBSwitchGuard.Evaluate("mt5", "ctrader", openSlots: 0).Allowed);
    }

    [Theory]
    [InlineData("ctrader", "mt5", 2)]
    [InlineData("ctrader", "mt4", 1)]
    [InlineData("mt4", "ctrader", 3)]
    [InlineData("MT5", " CTrader ", 1)]
    public void OpenSlots_AnySwitchInvolvingCTrader_IsRejected(string running, string next, int slots)
    {
        var decision = PlatformBSwitchGuard.Evaluate(running, next, slots);

        Assert.False(decision.Allowed);
        Assert.Contains($"Đang có {slots} slot mở", decision.Message);
    }

    [Theory]
    [InlineData("ctrader", "ctrader", 5)]
    [InlineData("ctrader", "CTRADER", 5)]
    [InlineData("mt5", "mt5", 5)]
    [InlineData("mt5", "mt4", 5)]
    public void OpenSlots_NoRelevantChange_IsAllowed(string running, string next, int slots)
    {
        Assert.True(PlatformBSwitchGuard.Evaluate(running, next, slots).Allowed);
    }

    [Theory]
    [InlineData(null, "ctrader", 1, false)]
    [InlineData("ctrader", null, 1, false)]
    [InlineData(null, null, 1, true)]
    [InlineData("mt5", "ctrader", -1, true)]
    public void NullAndNegativeInputs_DoNotThrow(string? running, string? next, int slots, bool expectedAllowed)
    {
        Assert.Equal(expectedAllowed, PlatformBSwitchGuard.Evaluate(running, next, slots).Allowed);
    }
}
