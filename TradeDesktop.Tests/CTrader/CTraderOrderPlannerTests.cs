using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.CTrader;

namespace TradeDesktop.Tests.CTrader;

// Phần QUYẾT ĐỊNH của executor sàn B (tầng App không test được vì TradeDesktop.Tests không tham chiếu
// TradeDesktop.App). Khoá ba thứ dễ gây mất tiền: sai đơn vị khối lượng (R5), đóng nhầm ticket của sàn kia
// (R4), và đóng mò khi thiếu dữ liệu position.
public sealed class CTraderOrderPlannerTests
{
    private static CTraderFixConfig Config(decimal volumeUnits = 1m, decimal contractSize = 100m)
        => CTraderFixConfig.Empty with { SymbolId = 41, VolumeBUnits = volumeUnits, ContractSizeB = contractSize };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlanOpen_KhongGan721_DeSanTaoPositionMoi(bool isBuy)
    {
        var plan = CTraderOrderPlanner.PlanOpen(Config(), isBuy, "B-1-1");

        Assert.True(plan.IsValid);
        Assert.Null(plan.Request!.PositionId);
        Assert.Equal(isBuy, plan.Request.IsBuy);
        Assert.Equal(1m, plan.Request.QuantityUnits);
    }

    // R5: tag 38 là ĐƠN VỊ CƠ SỞ. Giá trị rác ở đây = đặt sai khối lượng trên tiền thật.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PlanOpen_VolumeKhongHopLe_TuChoi(int volume)
    {
        var plan = CTraderOrderPlanner.PlanOpen(Config(volumeUnits: volume), isBuy: true, "B-1-1");

        Assert.False(plan.IsValid);
        Assert.Contains("volumeBUnits", plan.Error);
    }

    [Fact]
    public void PlanOpen_ContractSizeKhongHopLe_TuChoi()
    {
        var plan = CTraderOrderPlanner.PlanOpen(Config(contractSize: 0m), isBuy: true, "B-1-1");

        Assert.False(plan.IsValid);
        Assert.Contains("contractSizeB", plan.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlanClose_NguocChieuVaDuVolume_KemTag721(bool positionIsBuy)
    {
        var ticket = CTraderTicketCodec.Encode(623650688);

        var plan = CTraderOrderPlanner.PlanClose(Config(), ticket, (positionIsBuy, 3m), "B-2-1");

        Assert.True(plan.IsValid);
        Assert.Equal(623650688, plan.Request!.PositionId);
        Assert.Equal(!positionIsBuy, plan.Request.IsBuy);
        // Đóng theo volume CỦA POSITION, không phải volumeBUnits trong config.
        Assert.Equal(3m, plan.Request.QuantityUnits);
    }

    // R4: ticket MT không mang bit namespace 62. Gửi lệnh với nó là đóng nhầm sàn.
    [Fact]
    public void PlanClose_TicketKhongPhaiCTrader_TuChoi()
    {
        var plan = CTraderOrderPlanner.PlanClose(Config(), ticket: 123456UL, (true, 1m), "B-2-1");

        Assert.False(plan.IsValid);
        Assert.Contains("namespace cTrader", plan.Error);
    }

    [Fact]
    public void PlanClose_KhongCoPositionTrongCache_KhongDongMo()
    {
        var ticket = CTraderTicketCodec.Encode(999);

        var plan = CTraderOrderPlanner.PlanClose(Config(), ticket, position: null, "B-2-1");

        Assert.False(plan.IsValid);
        Assert.Contains("không đóng mò", plan.Error);
    }

    [Fact]
    public void PlanClose_VolumePositionBang0_TuChoi()
    {
        var ticket = CTraderTicketCodec.Encode(999);

        var plan = CTraderOrderPlanner.PlanClose(Config(), ticket, (true, 0m), "B-2-1");

        Assert.False(plan.IsValid);
        Assert.Contains("khối lượng không hợp lệ", plan.Error);
    }
}
