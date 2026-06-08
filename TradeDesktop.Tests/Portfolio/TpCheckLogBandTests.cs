using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

// Band resolver dùng để dedup log [SLOT][TP_CHECK]: log lại ngay khi band đổi (profit vượt confirm/TP).
public sealed class TpCheckLogBandTests
{
    [Fact]
    public void NullProfit_ReturnsIncomplete()
    {
        Assert.Equal(TpCheckLogBand.Incomplete, TpCheckLogBand.Resolve(profit: null, confirmThreshold: 1d, tpThreshold: 3d));
    }

    [Fact]
    public void ProfitBelowConfirm_ReturnsBelow()
    {
        // Ví dụ thực tế trong log spam: profit=-49, confirm=1, tp=3.
        Assert.Equal(TpCheckLogBand.Below, TpCheckLogBand.Resolve(profit: -49d, confirmThreshold: 1d, tpThreshold: 3d));
    }

    [Fact]
    public void ProfitAtConfirmThreshold_ReturnsConfirm()
    {
        Assert.Equal(TpCheckLogBand.Confirm, TpCheckLogBand.Resolve(profit: 1d, confirmThreshold: 1d, tpThreshold: 3d));
    }

    [Fact]
    public void ProfitBetweenConfirmAndTp_ReturnsConfirm()
    {
        Assert.Equal(TpCheckLogBand.Confirm, TpCheckLogBand.Resolve(profit: 2d, confirmThreshold: 1d, tpThreshold: 3d));
    }

    [Fact]
    public void ProfitAtTpThreshold_ReturnsTp()
    {
        Assert.Equal(TpCheckLogBand.Tp, TpCheckLogBand.Resolve(profit: 3d, confirmThreshold: 1d, tpThreshold: 3d));
    }

    [Fact]
    public void ProfitAboveTp_ReturnsTp()
    {
        Assert.Equal(TpCheckLogBand.Tp, TpCheckLogBand.Resolve(profit: 5d, confirmThreshold: 1d, tpThreshold: 3d));
    }

    [Fact]
    public void NegativeThresholds_UseAbsoluteValue()
    {
        // Threshold âm vẫn lấy Math.Abs giống format log hiện tại.
        Assert.Equal(TpCheckLogBand.Confirm, TpCheckLogBand.Resolve(profit: 2d, confirmThreshold: -1d, tpThreshold: -3d));
        Assert.Equal(TpCheckLogBand.Tp, TpCheckLogBand.Resolve(profit: 3d, confirmThreshold: -1d, tpThreshold: -3d));
        Assert.Equal(TpCheckLogBand.Below, TpCheckLogBand.Resolve(profit: 0.5d, confirmThreshold: -1d, tpThreshold: -3d));
    }
}
