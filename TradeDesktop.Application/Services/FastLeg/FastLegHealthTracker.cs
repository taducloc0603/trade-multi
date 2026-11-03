using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.FastLeg;

public enum FastLegHealthEvent
{
    None,
    // Sàn C hỏng (Down/Stale) liên tục quá ngưỡng: phát MỘT lần cho mỗi đợt hỏng.
    Down,
    // Sàn C có lại phán quyết (Confirmed/TooFast) sau khi đã phát Down.
    Recovered
}

/// <summary>
/// Theo dõi sức khoẻ sàn C để báo Telegram (Phase 5, quyết định D6): Down/Stale chập chờn vài trăm ms là chuyện bình
/// thường với feed thưa, nên chỉ báo khi hỏng LIÊN TỤC quá <c>thresholdMs</c>, và không spam trong cùng một đợt.
/// Thuần, không I/O; caller (UI thread) quyết định có gửi hay không.
/// </summary>
public sealed class FastLegHealthTracker(long thresholdMs = FastLegHealthTracker.DefaultThresholdMs)
{
    public const long DefaultThresholdMs = 30_000;

    private long? _badSinceMs;
    private bool _alerted;

    /// <summary>Lý do của tick hỏng gần nhất (DownReason, hoặc "Stale"), để đưa vào nội dung cảnh báo.</summary>
    public string? LastBadReason { get; private set; }

    public long BadDurationMs(long nowMs) => _badSinceMs is { } since ? Math.Max(0L, nowMs - since) : 0L;

    /// <param name="verdict">null = sàn C tắt ⇒ xoá trạng thái, không phát gì (tắt có chủ ý không phải sự cố).</param>
    public FastLegHealthEvent Observe(FastLegVerdict? verdict, long nowMs)
    {
        if (verdict is null)
        {
            Reset();
            return FastLegHealthEvent.None;
        }

        if (verdict.Kind is FastLegVerdictKind.Down or FastLegVerdictKind.Stale)
        {
            _badSinceMs ??= nowMs;
            LastBadReason = verdict.DownReason ?? verdict.Kind.ToString();
            if (!_alerted && nowMs - _badSinceMs.Value >= thresholdMs)
            {
                _alerted = true;
                return FastLegHealthEvent.Down;
            }

            return FastLegHealthEvent.None;
        }

        _badSinceMs = null;
        if (_alerted)
        {
            _alerted = false;
            return FastLegHealthEvent.Recovered;
        }

        return FastLegHealthEvent.None;
    }

    public void Reset()
    {
        _badSinceMs = null;
        _alerted = false;
        LastBadReason = null;
    }
}
