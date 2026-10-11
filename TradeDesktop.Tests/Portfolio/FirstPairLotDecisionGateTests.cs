using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Tests.Portfolio;

public sealed class FirstPairLotDecisionGateTests
{
    private static readonly DateTime T0 = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NewGate_IsIdle_NotBlocking()
    {
        var gate = new FirstPairLotDecisionGate();

        Assert.Equal(FirstPairLotDecisionState.Idle, gate.State);
        Assert.False(gate.IsOpenBlocked);
        Assert.False(gate.Decide(0, LotMismatchChoice.Continue, T0));
    }

    [Fact]
    public void Raise_BlocksOpen_UntilDecision()
    {
        var gate = new FirstPairLotDecisionGate();

        gate.Raise(1, T0);

        Assert.True(gate.IsAwaitingDecision);
        Assert.True(gate.IsOpenBlocked);
        Assert.False(gate.IsStopRequested);
    }

    [Fact]
    public void Continue_UnblocksOpen()
    {
        var gate = new FirstPairLotDecisionGate();
        gate.Raise(1, T0);

        Assert.True(gate.Decide(1, LotMismatchChoice.Continue, T0.AddSeconds(5)));

        Assert.Equal(FirstPairLotDecisionState.Continued, gate.State);
        Assert.False(gate.IsOpenBlocked);
    }

    [Fact]
    public void Stop_KeepsOpenBlocked_AndRequestsStop()
    {
        var gate = new FirstPairLotDecisionGate();
        gate.Raise(1, T0);

        Assert.True(gate.Decide(1, LotMismatchChoice.Stop, T0.AddSeconds(5)));

        Assert.True(gate.IsStopRequested);
        Assert.True(gate.IsOpenBlocked);
    }

    [Fact]
    public void AnswerFromOtherSession_IsIgnored()
    {
        var gate = new FirstPairLotDecisionGate();
        gate.Raise(2, T0);

        Assert.False(gate.Decide(1, LotMismatchChoice.Continue, T0));
        Assert.True(gate.IsAwaitingDecision);
    }

    [Fact]
    public void SecondAnswer_IsIgnored()
    {
        var gate = new FirstPairLotDecisionGate();
        gate.Raise(1, T0);
        gate.Decide(1, LotMismatchChoice.Stop, T0);

        Assert.False(gate.Decide(1, LotMismatchChoice.Continue, T0));
        Assert.True(gate.IsStopRequested);
    }

    [Fact]
    public void Reset_ReturnsToIdle_AndLateAnswerIsIgnored()
    {
        var gate = new FirstPairLotDecisionGate();
        gate.Raise(1, T0);

        gate.Reset();

        Assert.Equal(FirstPairLotDecisionState.Idle, gate.State);
        Assert.False(gate.IsOpenBlocked);
        Assert.False(gate.Decide(1, LotMismatchChoice.Continue, T0));
    }

    [Fact]
    public void ShouldRemind_EveryInterval_OnlyWhileAwaiting()
    {
        var gate = new FirstPairLotDecisionGate();
        var interval = TimeSpan.FromMinutes(15);
        Assert.False(gate.ShouldRemind(T0, interval));

        gate.Raise(1, T0);
        Assert.False(gate.ShouldRemind(T0.AddMinutes(14), interval));
        Assert.True(gate.ShouldRemind(T0.AddMinutes(15), interval));
        Assert.False(gate.ShouldRemind(T0.AddMinutes(20), interval));
        Assert.True(gate.ShouldRemind(T0.AddMinutes(30), interval));

        gate.Decide(1, LotMismatchChoice.Continue, T0.AddMinutes(31));
        Assert.False(gate.ShouldRemind(T0.AddHours(2), interval));
    }
}
