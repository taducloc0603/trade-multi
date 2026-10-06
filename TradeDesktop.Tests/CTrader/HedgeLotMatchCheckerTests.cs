using TradeDesktop.Application.Services.CTrader;

namespace TradeDesktop.Tests.CTrader;

// Kiểm tra lot thật của cặp Auto Open đầu tiên (chế độ cTrader). Unknown được xử lý như lệch (fail-closed).
public sealed class HedgeLotMatchCheckerTests
{
    [Theory]
    [InlineData(0.15, 0.15)]
    [InlineData(0.15, 0.1500000001)] // nhiễu double từ VolumeUnits / contractSizeB
    [InlineData(0.01, 0.01)]
    [InlineData(1.0, 1.0)]
    public void SameLot_IsMatch(double lotA, double lotB)
    {
        Assert.Equal(HedgeLotMatchLevel.Match, HedgeLotMatchChecker.Check(lotA, lotB).Level);
    }

    [Theory]
    [InlineData(0.15, 0.01)]
    [InlineData(0.15, 0.16)]
    [InlineData(1.0, 0.1)]
    public void DifferentLot_IsMismatch(double lotA, double lotB)
    {
        var result = HedgeLotMatchChecker.Check(lotA, lotB);

        Assert.Equal(HedgeLotMatchLevel.Mismatch, result.Level);
        Assert.Contains("lotA=", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingLegA_IsUnknown_NamesLeg()
    {
        var result = HedgeLotMatchChecker.Check(null, 0.15);

        Assert.Equal(HedgeLotMatchLevel.Unknown, result.Level);
        Assert.Contains("leg=A", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.15)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidLegB_IsUnknown(double lotB)
    {
        var result = HedgeLotMatchChecker.Check(0.15, lotB);

        Assert.Equal(HedgeLotMatchLevel.Unknown, result.Level);
        Assert.Contains("leg=B", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BothMissing_IsUnknown()
    {
        Assert.Contains("leg=A,B", HedgeLotMatchChecker.Check(null, null).Message, StringComparison.Ordinal);
    }
}
