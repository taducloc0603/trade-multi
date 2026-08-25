using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.Tests;

public sealed class GapStabilityCalculatorTests
{
    private static readonly GapStabilityConfig DefaultConfig =
        new(10, 0.50, 4.0, 3, 0.35, 0.40);

    [Fact]
    public void Median_OddSampleCount_ReturnsMiddleValue()
    {
        Assert.Equal(120d, GapStabilityCalculator.Median([100, 120, 150, 110, 160]));
    }

    [Fact]
    public void Median_EvenSampleCount_ReturnsMeanOfTwoMiddleValues()
    {
        Assert.Equal(45d, GapStabilityCalculator.Median([20, 30, 40, 50, 60, 70]));
    }

    [Fact]
    public void Calculate_LargeStableSequence_MatchesSpecification()
    {
        var result = GapStabilityCalculator.Calculate([100, 120, 150, 110, 160], DefaultConfig);

        Assert.Equal(120d, result.Center);
        Assert.Equal(20d, result.Mad);
        Assert.Equal(80d, result.Tolerance);
        Assert.Equal(20d / 120d, result.Dispersion, precision: 12);
        Assert.Equal(110d, result.EarlyCenter);
        Assert.Equal(135d, result.LateCenter);
        Assert.Equal(25d / 120d, result.Drift, precision: 12);
        Assert.Equal(5, result.SampleCount);
    }

    [Fact]
    public void Calculate_SmallStableSequence_UsesAbsoluteFloor()
    {
        var result = GapStabilityCalculator.Calculate([2, 5, 6, 5, 2, 7, 4], DefaultConfig);

        Assert.Equal(5d, result.Center);
        Assert.Equal(1d, result.Mad);
        Assert.Equal(10d, result.Tolerance);
        Assert.Equal(0.20d, result.Dispersion, precision: 12);
    }

    [Fact]
    public void Calculate_DriftingSequence_MatchesSpecification()
    {
        var result = GapStabilityCalculator.Calculate([20, 30, 40, 50, 60, 70], DefaultConfig);

        Assert.Equal(45d, result.Center);
        Assert.Equal(30d, result.EarlyCenter);
        Assert.Equal(60d, result.LateCenter);
        Assert.Equal(2d / 3d, result.Drift, precision: 12);
    }

    [Fact]
    public void Calculate_WidelyDispersedSequence_MatchesSpecification()
    {
        var result = GapStabilityCalculator.Calculate([100, 250, 80, 300, 70, 280], DefaultConfig);

        Assert.Equal(175d, result.Center);
        Assert.Equal(100d, result.Mad);
        Assert.Equal(100d / 175d, result.Dispersion, precision: 12);
    }

    [Fact]
    public void Calculate_NegativeSellGaps_UsesMagnitudeWithoutMutatingInput()
    {
        int[] gaps = [-100, -120, -150, -110, -160];

        var result = GapStabilityCalculator.Calculate(gaps, DefaultConfig);

        Assert.Equal(120d, result.Center);
        Assert.Equal(20d, result.Mad);
        Assert.True(gaps.SequenceEqual([-100, -120, -150, -110, -160]));
    }

    [Fact]
    public void Calculate_ZeroCenter_UsesOneAsDenominator()
    {
        var result = GapStabilityCalculator.Calculate([0, 0, 0], DefaultConfig);

        Assert.Equal(0d, result.Center);
        Assert.Equal(0d, result.Mad);
        Assert.Equal(0d, result.Dispersion);
        Assert.Equal(0d, result.Drift);
        Assert.Equal(10d, result.Tolerance);
    }

    [Fact]
    public void Calculate_OneSample_HasZeroDispersionAndDrift()
    {
        var result = GapStabilityCalculator.Calculate([100], DefaultConfig);

        Assert.Equal(100d, result.Center);
        Assert.Equal(0d, result.Mad);
        Assert.Equal(50d, result.Tolerance);
        Assert.Equal(0d, result.Dispersion);
        Assert.Equal(0d, result.Drift);
    }

    [Fact]
    public void Calculate_TwoSamples_UsesEachSampleAsOneHalf()
    {
        var result = GapStabilityCalculator.Calculate([100, 120], DefaultConfig);

        Assert.Equal(110d, result.Center);
        Assert.Equal(10d, result.Mad);
        Assert.Equal(100d, result.EarlyCenter);
        Assert.Equal(120d, result.LateCenter);
        Assert.Equal(20d / 110d, result.Drift, precision: 12);
    }

    [Fact]
    public void CalculateDelta_UsesAbsoluteMagnitudeForBuyAndSell()
    {
        Assert.Equal(30d, GapStabilityCalculator.CalculateDelta(150, 120));
        Assert.Equal(30d, GapStabilityCalculator.CalculateDelta(-150, 120));
        Assert.Equal(18d, GapStabilityCalculator.CalculateDelta(2, 20));
    }

    [Fact]
    public void Calculate_IntMinValue_DoesNotOverflow()
    {
        var result = GapStabilityCalculator.Calculate([int.MinValue], DefaultConfig);

        Assert.Equal(2147483648d, result.Center);
    }

    [Fact]
    public void EmptyInput_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            GapStabilityCalculator.Calculate([], DefaultConfig));
        Assert.Throws<ArgumentException>(() =>
            GapStabilityCalculator.Median([]));
    }

    [Fact]
    public void InvalidConfig_IsRejected()
    {
        var invalid = DefaultConfig with { MinStableSamples = 2 };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GapStabilityCalculator.Calculate([100], invalid));
    }
}
