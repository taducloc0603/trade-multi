using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests.Portfolio;

public sealed class SosCloseConfigResolverTests
{
    [Fact]
    public void SosActive_WithValidThresholds_UsesSosValues()
    {
        var result = SosCloseConfigResolver.ResolveGapThresholds(true, 30, 40, -12, -8);

        Assert.True(result.UsesSos);
        Assert.Equal(-12, result.ConfirmGapPts);
        Assert.Equal(-8, result.CloseGapPts);
    }

    [Theory]
    [InlineData(0, -8)]
    [InlineData(-12, 0)]
    [InlineData(0, 0)]
    public void SosActive_WithAnyZeroThreshold_FallsBackToNormal(int sosConfirm, int sosClose)
    {
        var result = SosCloseConfigResolver.ResolveGapThresholds(true, 30, 40, sosConfirm, sosClose);

        Assert.False(result.UsesSos);
        Assert.Equal(30, result.ConfirmGapPts);
        Assert.Equal(40, result.CloseGapPts);
    }

    [Fact]
    public void LatestSosGap_MeetsSosButNotNormal_IsAllowed()
    {
        var error = SosCloseConfigResolver.ValidateLatestGap(
            GapSignalTriggerType.CloseByGapSell,
            gapBuy: null,
            gapSell: -15,
            confirmGapPts: -12,
            closeGapPts: -8,
            limitMaxGap: 30);

        Assert.Null(error);
    }

    [Fact]
    public void LatestGap_ExceedsLimit_IsRejected()
    {
        var error = SosCloseConfigResolver.ValidateLatestGap(
            GapSignalTriggerType.CloseByGapSell,
            gapBuy: null,
            gapSell: -50,
            confirmGapPts: -12,
            closeGapPts: -8,
            limitMaxGap: 30);

        Assert.Equal("LATEST_GAP_EXCEEDS_LIMIT", error);
    }

    [Theory]
    [InlineData(GapSignalTriggerType.CloseByGapSell, null, -7)]
    [InlineData(GapSignalTriggerType.CloseByGapBuy, 7, null)]
    public void LatestGap_BelowEffectiveThreshold_IsRejected(
        GapSignalTriggerType triggerType,
        int? gapBuy,
        int? gapSell)
    {
        var error = SosCloseConfigResolver.ValidateLatestGap(
            triggerType, gapBuy, gapSell, -12, -8, 30);

        Assert.Equal("LATEST_CLOSE_CONDITION_INVALID", error);
    }

    [Theory]
    [InlineData(GapSignalTriggerType.CloseByGapSell, null, 8)]
    [InlineData(GapSignalTriggerType.CloseByGapSell, null, 7)]
    [InlineData(GapSignalTriggerType.CloseByGapBuy, -8, null)]
    [InlineData(GapSignalTriggerType.CloseByGapBuy, -7, null)]
    public void LatestSosGap_InwardCloseCondition_IsAllowed(
        GapSignalTriggerType triggerType,
        int? gapBuy,
        int? gapSell)
    {
        var error = SosCloseConfigResolver.ValidateLatestSosGap(
            triggerType, gapBuy, gapSell, closeGapPts: -8, limitMaxGap: 30);

        Assert.Null(error);
    }

    [Theory]
    [InlineData(GapSignalTriggerType.CloseByGapSell, null, 9)]
    [InlineData(GapSignalTriggerType.CloseByGapBuy, -9, null)]
    public void LatestSosGap_BeforeCloseThreshold_IsRejected(
        GapSignalTriggerType triggerType,
        int? gapBuy,
        int? gapSell)
    {
        var error = SosCloseConfigResolver.ValidateLatestSosGap(
            triggerType, gapBuy, gapSell, closeGapPts: -8, limitMaxGap: 30);

        Assert.Equal("LATEST_SOS_CLOSE_CONDITION_INVALID", error);
    }
}
