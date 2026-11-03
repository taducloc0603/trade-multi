using System.Globalization;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.FastLeg;

/// <summary>
/// Gom verdict theo chu kỳ và sinh một dòng <c>[FAST_LEG][STATS]</c>: dữ liệu chính để chỉnh <c>maxMovePts</c> và quyết
/// lại D9 (<c>stale_hb_alive_pct</c> = Stale trong khi terminal vẫn sống). Không phải thread-safe: chỉ gọi từ UI thread.
/// </summary>
public sealed class FastLegStats(long intervalMs = FastLegStats.DefaultIntervalMs)
{
    public const long DefaultIntervalMs = 60_000;

    private readonly List<decimal> _absMoves = new(capacity: 1300);
    private readonly List<long> _ages = new(capacity: 1300);
    private long _windowStartMs = -1;
    private long _ticksAtWindowStart = -1;
    private int _observations;
    private int _tooFast;
    private int _stale;
    private int _staleHeartbeatAlive;
    private int _down;
    private long _maxAge;

    /// <summary>Trả về dòng log khi đủ chu kỳ, ngược lại null. Không phân bổ chuỗi giữa hai lần flush.</summary>
    public string? Observe(FastLegVerdict verdict, long nowMs, long totalTicks)
    {
        if (_windowStartMs < 0)
        {
            _windowStartMs = nowMs;
            _ticksAtWindowStart = totalTicks;
        }

        _observations++;
        switch (verdict.Kind)
        {
            case FastLegVerdictKind.TooFast:
                _tooFast++;
                break;
            case FastLegVerdictKind.Stale:
                _stale++;
                if (verdict.HeartbeatState == FastLegHeartbeatState.Alive)
                {
                    _staleHeartbeatAlive++;
                }

                break;
            case FastLegVerdictKind.Down:
                _down++;
                break;
        }

        if (verdict.Kind != FastLegVerdictKind.Down)
        {
            _absMoves.Add(Math.Abs(verdict.MovePts));
            if (verdict.AgeMs is { } age)
            {
                _ages.Add(age);
                _maxAge = Math.Max(_maxAge, age);
            }
        }

        if (nowMs - _windowStartMs < intervalMs)
        {
            return null;
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"[FAST_LEG][STATS] ticks={totalTicks - _ticksAtWindowStart} samples={_observations} p50_age={Percentile(_ages, 0.5)} max_age={_maxAge} p50_abs_move={Percentile(_absMoves, 0.5):0.##} p90_abs_move={Percentile(_absMoves, 0.9):0.##} toofast_pct={Pct(_tooFast):0.#} stale_pct={Pct(_stale):0.#} stale_hb_alive_pct={Pct(_staleHeartbeatAlive):0.#} down_pct={Pct(_down):0.#}");
        Reset();
        return line;
    }

    public void Reset()
    {
        _windowStartMs = -1;
        _ticksAtWindowStart = -1;
        _observations = 0;
        _tooFast = 0;
        _stale = 0;
        _staleHeartbeatAlive = 0;
        _down = 0;
        _maxAge = 0;
        _absMoves.Clear();
        _ages.Clear();
    }

    private double Pct(int count) => _observations == 0 ? 0d : count * 100d / _observations;

    private static T Percentile<T>(List<T> values, double percentile) where T : struct, IComparable<T>
    {
        if (values.Count == 0)
        {
            return default;
        }

        values.Sort();
        var index = (int)Math.Ceiling(percentile * values.Count) - 1;
        return values[Math.Clamp(index, 0, values.Count - 1)];
    }
}
