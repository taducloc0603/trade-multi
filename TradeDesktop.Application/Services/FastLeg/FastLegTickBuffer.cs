namespace TradeDesktop.Application.Services.FastLeg;

// Thời điểm của mẫu là GetTickCount64 do EA ghi: cùng đồng hồ với Environment.TickCount64 của app.
public readonly record struct FastLegTickSample(long TimestampMs, decimal Bid, decimal Ask)
{
    public decimal Mid => (Bid + Ask) / 2m;
}

public readonly record struct FastLegHeartbeat(long TimestampMs, bool Connected);

public readonly record struct FastLegBufferSnapshot(
    FastLegTickSample? Reference,
    FastLegTickSample? Last,
    FastLegHeartbeat? Heartbeat,
    string? DownReason,
    long TotalTicks);

/// <summary>
/// Buffer tick sàn C. Thread sampler ghi, UI thread và router đọc nên mọi truy cập đều qua lock (giữ rất ngắn).
/// Luôn giữ đúng 1 mẫu ở hoặc trước đầu cửa sổ làm tham chiếu, nên biên độ đo trên trọn cửa sổ dù sàn C thưa tick.
/// </summary>
public sealed class FastLegTickBuffer
{
    // Trần cứng: EA ghi mỗi OnTick, không throttle. Không được để buffer phình dù tick dồn dập (CLAUDE.md §5).
    public const int MaxSamples = 1024;

    private readonly object _sync = new();
    private readonly List<FastLegTickSample> _samples = new(capacity: 64);
    private int _retentionMs = 200;
    private FastLegHeartbeat? _heartbeat;
    private string? _downReason;
    private long _totalTicks;

    public void SetRetentionMs(int retentionMs)
    {
        lock (_sync)
        {
            _retentionMs = Math.Max(1, retentionMs);
        }
    }

    /// <summary>Thêm mẫu; mẫu có thời điểm lùi so với mẫu cuối bị bỏ để danh sách luôn tăng dần.</summary>
    public bool Push(FastLegTickSample sample)
    {
        lock (_sync)
        {
            if (_samples.Count > 0 && sample.TimestampMs < _samples[^1].TimestampMs)
            {
                return false;
            }

            _samples.Add(sample);
            _totalTicks++;
            Prune(sample.TimestampMs);
            return true;
        }
    }

    public void SetHeartbeat(FastLegHeartbeat? heartbeat)
    {
        lock (_sync)
        {
            _heartbeat = heartbeat;
        }
    }

    public void SetDownReason(string? reason)
    {
        lock (_sync)
        {
            _downReason = reason;
        }
    }

    /// <summary>Xoá mẫu và heartbeat (đổi map / tắt F). Không đụng lý do Down.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _samples.Clear();
            _heartbeat = null;
        }
    }

    /// <summary>
    /// Không cấp phát: quy tắc quiet chỉ cần mẫu tham chiếu và mẫu cuối, mà hàm này chạy mỗi tick trên UI thread.
    /// Tham chiếu = mẫu mới nhất có ts ≤ now − window; không có ⇒ mẫu cũ nhất đang giữ.
    /// </summary>
    public FastLegBufferSnapshot Capture(int windowMs, long nowMs)
    {
        lock (_sync)
        {
            if (_samples.Count == 0)
            {
                return new FastLegBufferSnapshot(null, null, _heartbeat, _downReason, _totalTicks);
            }

            var windowStart = nowMs - windowMs;
            var reference = _samples[0];
            for (var i = _samples.Count - 1; i >= 0; i--)
            {
                if (_samples[i].TimestampMs <= windowStart)
                {
                    reference = _samples[i];
                    break;
                }
            }

            return new FastLegBufferSnapshot(reference, _samples[^1], _heartbeat, _downReason, _totalTicks);
        }
    }

    private void Prune(long newestMs)
    {
        // Mẫu mới nhất có ts ≤ newest − retention vẫn phải giữ: nó có thể là tham chiếu của lần Capture kế tiếp.
        var windowStart = newestMs - _retentionMs;
        var keepFrom = 0;
        for (var i = _samples.Count - 1; i >= 0; i--)
        {
            if (_samples[i].TimestampMs <= windowStart)
            {
                keepFrom = i;
                break;
            }
        }

        if (_samples.Count - keepFrom > MaxSamples)
        {
            keepFrom = _samples.Count - MaxSamples;
        }

        if (keepFrom > 0)
        {
            _samples.RemoveRange(0, keepFrom);
        }
    }
}
