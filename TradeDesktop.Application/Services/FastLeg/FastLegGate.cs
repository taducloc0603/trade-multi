using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.FastLeg;

public sealed class FastLegGate(
    IRuntimeConfigProvider runtimeConfigProvider,
    FastLegTickBuffer buffer,
    Func<long>? tickCount = null) : IFastLegGate
{
    private readonly Func<long> _tickCount = tickCount ?? (() => Environment.TickCount64);

    public long TotalTicks => buffer.Capture(0, _tickCount()).TotalTicks;

    public FastLegVerdict? EvaluateNow()
    {
        var config = runtimeConfigProvider.CurrentFastLegConfig;
        if (!config.IsActive)
        {
            return null;
        }

        var nowMs = _tickCount();
        var snapshot = buffer.Capture(config.WindowMs, nowMs);
        return FastLegEvaluator.Evaluate(in snapshot, nowMs, config, runtimeConfigProvider.CurrentPoint);
    }
}
