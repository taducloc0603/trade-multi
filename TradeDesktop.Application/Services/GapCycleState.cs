using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public enum GapCycleStatus
{
    Empty = 0,
    Collecting = 1,
    Stable = 2,
    Unstable = 3,
    Rejected = 4
}

public enum GapCycleTransition
{
    None = 0,
    Started = 1,
    Joined = 2,
    NewCycle = 3,
    BecameStable = 4,
    BecameUnstable = 5,
    Rejected = 6,
    ResetMissingData = 7,
    ResetConfirmNotSatisfied = 8,
    ResetTimestamp = 9,
    ResetExplicitly = 10
}

public sealed record GapCycleSnapshot(
    string CycleId,
    GapCycleStatus Status,
    DateTime? StartedAtUtc,
    DateTime? LastTickUtc,
    IReadOnlyList<int> Gaps,
    double? Center,
    double? Mad,
    double? Tolerance,
    double? Dispersion,
    double? EarlyCenter,
    double? LateCenter,
    double? Drift,
    double DurationMs,
    string Reason)
{
    public int SampleCount => Gaps.Count;
}

public sealed record GapCycleUpdateResult(
    GapCycleTransition Transition,
    GapCycleSnapshot CurrentCycle,
    GapCycleSnapshot? CompletedCycle = null);

/// <summary>
/// Quản lý một Cycle Gap trong memory. Instance này không quyết định hướng Buy/Sell;
/// caller phải cung cấp đúng chuỗi Gap và kết quả điều kiện Confirm tương ứng.
/// </summary>
public sealed class GapCycleState
{
    private readonly List<int> _gaps = [];
    private FixedSizeSignalCycle<int>? _fixedCycle;
    private string _cycleId = string.Empty;
    private DateTime? _startedAtUtc;
    private DateTime? _lastTickUtc;
    private GapStabilityCalculator.Metrics? _metrics;
    private GapCycleStatus _status = GapCycleStatus.Empty;
    private string _reason = "Chưa có Cycle.";

    public GapCycleSnapshot Current => _fixedCycle is null
        ? BuildSnapshot()
        : BuildFixedSnapshot();
    public int FixedRequiredSize => _fixedCycle?.RequiredSize ?? 0;

    public GapCycleUpdateResult ProcessFixedSize(
        DateTime timestampUtc,
        int? gap,
        bool hasRequiredData,
        bool confirmSatisfied,
        GapStabilityConfig config,
        int requiredSize,
        string fingerprint,
        int limitMaxGap = 0)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.TryValidate(out var configError))
        {
            throw new ArgumentOutOfRangeException(nameof(config), configError);
        }

        if (requiredSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredSize), "Signal cycle size must be >= 1.");
        }

        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ArgumentException("Fingerprint is required.", nameof(fingerprint));
        }

        EnsureFixedCycle(requiredSize);

        if (!hasRequiredData || !gap.HasValue)
        {
            return ResetFixed(GapCycleTransition.ResetMissingData, "Missing required Gap/Bid/Ask data.");
        }

        if (!confirmSatisfied)
        {
            return ResetFixed(GapCycleTransition.ResetConfirmNotSatisfied, "Gap does not satisfy confirm threshold.");
        }

        var gapValue = gap.Value;
        if (limitMaxGap > 0 && Magnitude(gapValue) > limitMaxGap)
        {
            var completed = _fixedCycle!.Count > 0 ? BuildFixedSnapshot() : null;
            _fixedCycle.Reset("LIMIT_MAX_GAP");
            _metrics = null;
            _status = GapCycleStatus.Rejected;
            _reason = $"Gap {gapValue} exceeds limit_max_gap {limitMaxGap}.";
            return new GapCycleUpdateResult(GapCycleTransition.Rejected, BuildFixedSnapshot(), completed);
        }

        if (_fixedCycle!.LastUpdatedAtUtc.HasValue
            && timestampUtc < _fixedCycle.LastUpdatedAtUtc.Value)
        {
            var completed = BuildFixedSnapshot();
            StartFixedCycle(timestampUtc, gapValue, fingerprint, config, "Timestamp regressed; started a new cycle.");
            return new GapCycleUpdateResult(GapCycleTransition.ResetTimestamp, BuildFixedSnapshot(), completed);
        }

        if (_fixedCycle.Count == 0)
        {
            StartFixedCycle(timestampUtc, gapValue, fingerprint, config, "Started fixed-size cycle.");
            return CompleteFixedCycleIfReady(config, GapCycleTransition.Started, fingerprint);
        }

        // Stability đo trên |gap|, nên một Cycle trộn hai dấu (ví dụ -5, +5, -5) bị chấm là ổn định.
        // Với ngưỡng dương, confirm gate đã ép cả Cycle về cùng dấu nên guard này không bao giờ chạm.
        // Với ngưỡng âm thì Cycle được phép chứa hai dấu, nên phải chặn ở đây.
        if (HasSignConflict(_fixedCycle.Values, gapValue))
        {
            var signCompleted = BuildFixedSnapshot();
            StartFixedCycle(
                timestampUtc,
                gapValue,
                fingerprint,
                config,
                "Gap sign flipped; started a new cycle.");
            return new GapCycleUpdateResult(
                GapCycleTransition.NewCycle,
                BuildFixedSnapshot(),
                signCompleted);
        }

        var currentMetrics = GapStabilityCalculator.Calculate(_fixedCycle.Values, config);
        var delta = GapStabilityCalculator.CalculateDelta(gapValue, currentMetrics.Center);
        if (delta > currentMetrics.Tolerance)
        {
            var completed = BuildFixedSnapshot(currentMetrics);
            StartFixedCycle(
                timestampUtc,
                gapValue,
                fingerprint,
                config,
                $"Delta {delta:0.####} exceeds Tolerance {currentMetrics.Tolerance:0.####}; started a new cycle.");
            return new GapCycleUpdateResult(GapCycleTransition.NewCycle, BuildFixedSnapshot(), completed);
        }

        var add = _fixedCycle.Add(gapValue, timestampUtc, fingerprint);
        if (add.DuplicateIgnored)
        {
            _reason = "Duplicate snapshot ignored.";
            return new GapCycleUpdateResult(GapCycleTransition.None, BuildFixedSnapshot(currentMetrics));
        }

        _metrics = GapStabilityCalculator.Calculate(_fixedCycle.Values, config);
        _status = GapCycleStatus.Collecting;
        _reason = $"Collecting fixed-size cycle: {_fixedCycle.Count}/{_fixedCycle.RequiredSize}.";
        return CompleteFixedCycleIfReady(config, GapCycleTransition.Joined, fingerprint);
    }

    public GapCycleUpdateResult Process(
        DateTime timestampUtc,
        int? gap,
        bool hasRequiredData,
        bool confirmSatisfied,
        GapStabilityConfig config,
        int holdConfirmMs,
        int limitMaxGap = 0)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.TryValidate(out var configError))
        {
            throw new ArgumentOutOfRangeException(nameof(config), configError);
        }

        if (holdConfirmMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(holdConfirmMs),
                "Hold Confirm phải >= 0.");
        }

        if (!hasRequiredData || !gap.HasValue)
        {
            return ResetInternal(
                GapCycleTransition.ResetMissingData,
                "Thiếu Gap hoặc Bid/Ask cần thiết; reset Cycle.");
        }

        if (!confirmSatisfied)
        {
            return ResetInternal(
                GapCycleTransition.ResetConfirmNotSatisfied,
                "Gap không đạt điều kiện Confirm; reset Cycle.");
        }

        var gapValue = gap.Value;
        if (limitMaxGap > 0 && Magnitude(gapValue) > limitMaxGap)
        {
            var completed = _gaps.Count > 0 ? BuildSnapshot() : null;
            Clear();
            _status = GapCycleStatus.Rejected;
            _reason = $"Gap {gapValue} vượt limit_max_gap {limitMaxGap}; không tạo Cycle mới.";
            return new GapCycleUpdateResult(
                GapCycleTransition.Rejected,
                BuildSnapshot(),
                completed);
        }

        if (_lastTickUtc.HasValue && timestampUtc < _lastTickUtc.Value)
        {
            var completed = BuildSnapshot();
            StartNewCycle(timestampUtc, gapValue, config, "Timestamp bị lùi; bắt đầu Cycle mới.");
            return new GapCycleUpdateResult(
                GapCycleTransition.ResetTimestamp,
                BuildSnapshot(),
                completed);
        }

        if (_gaps.Count == 0)
        {
            StartNewCycle(timestampUtc, gapValue, config, "Bắt đầu Cycle từ mẫu hợp lệ đầu tiên.");
            return new GapCycleUpdateResult(
                GapCycleTransition.Started,
                BuildSnapshot());
        }

        // Xem chú thích ở ProcessFixedSize: chặn Cycle trộn hai dấu vì Stability đo trên |gap|.
        if (HasSignConflict(_gaps, gapValue))
        {
            var signCompleted = BuildSnapshot();
            StartNewCycle(timestampUtc, gapValue, config, "Gap đổi dấu; bắt đầu Cycle mới.");
            return new GapCycleUpdateResult(
                GapCycleTransition.NewCycle,
                BuildSnapshot(),
                signCompleted);
        }

        var currentMetrics = GapStabilityCalculator.Calculate(_gaps, config);
        var delta = GapStabilityCalculator.CalculateDelta(gapValue, currentMetrics.Center);
        if (delta > currentMetrics.Tolerance)
        {
            var completed = BuildSnapshot();
            StartNewCycle(
                timestampUtc,
                gapValue,
                config,
                $"Delta {delta:0.####} vượt Tolerance {currentMetrics.Tolerance:0.####}; tạo Cycle mới.");
            return new GapCycleUpdateResult(
                GapCycleTransition.NewCycle,
                BuildSnapshot(),
                completed);
        }

        var previousStatus = _status;
        _gaps.Add(gapValue);
        _lastTickUtc = timestampUtc;

        var metrics = GapStabilityCalculator.Calculate(_gaps, config);
        _metrics = metrics;
        var durationMs = CalculateDurationMs();
        var enoughSamples = _gaps.Count >= config.MinStableSamples;
        var enoughDuration = durationMs >= holdConfirmMs;

        if (!enoughSamples || !enoughDuration)
        {
            _status = GapCycleStatus.Collecting;
            _reason = !enoughSamples
                ? $"Chưa đủ mẫu: {_gaps.Count}/{config.MinStableSamples}."
                : $"Chưa đủ Hold Confirm: {durationMs:0.####}/{holdConfirmMs} ms.";
            return new GapCycleUpdateResult(
                GapCycleTransition.Joined,
                BuildSnapshot(metrics));
        }

        if (metrics.Dispersion > config.MaxDispersion
            || metrics.Drift > config.MaxDrift)
        {
            _status = GapCycleStatus.Unstable;
            _reason = BuildUnstableReason(metrics, config);
            var completed = BuildSnapshot(metrics);

            // Giữ mẫu mới nhất làm điểm bắt đầu vùng kế tiếp, tránh Cycle lỗi tăng vô hạn.
            StartNewCycle(
                timestampUtc,
                gapValue,
                config,
                "Bắt đầu lại từ mẫu cuối của Cycle Unstable.");
            return new GapCycleUpdateResult(
                GapCycleTransition.BecameUnstable,
                BuildSnapshot(),
                completed);
        }

        _status = GapCycleStatus.Stable;
        _reason = "Cycle đủ mẫu, đủ Hold Confirm và đạt Dispersion/Drift.";
        return new GapCycleUpdateResult(
            previousStatus == GapCycleStatus.Stable
                ? GapCycleTransition.Joined
                : GapCycleTransition.BecameStable,
            BuildSnapshot(metrics));
    }

    public GapCycleUpdateResult Reset(string reason = "Reset Cycle theo yêu cầu.") =>
        _fixedCycle is null
            ? ResetInternal(GapCycleTransition.ResetExplicitly, reason)
            : ResetFixed(GapCycleTransition.ResetExplicitly, reason);

    private GapCycleUpdateResult CompleteFixedCycleIfReady(
        GapStabilityConfig config,
        GapCycleTransition collectingTransition,
        string finalFingerprint)
    {
        if (!_fixedCycle!.IsComplete)
        {
            return new GapCycleUpdateResult(collectingTransition, BuildFixedSnapshot());
        }

        var metrics = GapStabilityCalculator.Calculate(_fixedCycle.Values, config);
        _metrics = metrics;
        if (metrics.Dispersion > config.MaxDispersion || metrics.Drift > config.MaxDrift)
        {
            _status = GapCycleStatus.Unstable;
            _reason = BuildUnstableReason(metrics, config);
            var completed = BuildFixedSnapshot(metrics);
            var lastGap = _fixedCycle.Values[^1];
            var lastTimestamp = _fixedCycle.LastUpdatedAtUtc!.Value;
            StartFixedCycle(
                lastTimestamp,
                lastGap,
                finalFingerprint,
                config,
                "Restarted from final Gap of unstable fixed-size cycle.");
            return new GapCycleUpdateResult(
                GapCycleTransition.BecameUnstable,
                BuildFixedSnapshot(),
                completed);
        }

        _status = GapCycleStatus.Stable;
        _reason = $"Fixed-size cycle has {_fixedCycle.RequiredSize} Gaps and passed Dispersion/Drift.";
        return new GapCycleUpdateResult(GapCycleTransition.BecameStable, BuildFixedSnapshot(metrics));
    }

    private void EnsureFixedCycle(int requiredSize)
    {
        if (_fixedCycle is null)
        {
            _fixedCycle = new FixedSizeSignalCycle<int>(requiredSize);
            return;
        }

        if (_fixedCycle.RequiredSize != requiredSize)
        {
            _fixedCycle.Resize(requiredSize);
            _metrics = null;
            _status = GapCycleStatus.Empty;
            _reason = "Signal cycle size changed; reset fixed-size cycle.";
        }
    }

    private void StartFixedCycle(
        DateTime timestampUtc,
        int gap,
        string fingerprint,
        GapStabilityConfig config,
        string reason)
    {
        if (_fixedCycle!.Count == 0)
        {
            _fixedCycle.Add(gap, timestampUtc, fingerprint);
        }
        else
        {
            _fixedCycle.RestartWith(gap, timestampUtc, fingerprint, reason);
        }

        _metrics = GapStabilityCalculator.Calculate(_fixedCycle.Values, config);
        _status = GapCycleStatus.Collecting;
        _reason = reason;
    }

    private GapCycleUpdateResult ResetFixed(
        GapCycleTransition transition,
        string reason)
    {
        var completed = _fixedCycle!.Count > 0 ? BuildFixedSnapshot() : null;
        _fixedCycle.Reset(reason);
        _metrics = null;
        _status = GapCycleStatus.Empty;
        _reason = reason;
        return new GapCycleUpdateResult(transition, BuildFixedSnapshot(), completed);
    }

    private GapCycleSnapshot BuildFixedSnapshot(
        GapStabilityCalculator.Metrics? metrics = null)
    {
        metrics ??= _metrics;
        return new GapCycleSnapshot(
            _fixedCycle?.CycleId ?? string.Empty,
            _status,
            _fixedCycle?.StartedAtUtc,
            _fixedCycle?.LastUpdatedAtUtc,
            _fixedCycle?.Values.ToArray() ?? [],
            metrics?.Center,
            metrics?.Mad,
            metrics?.Tolerance,
            metrics?.Dispersion,
            metrics?.EarlyCenter,
            metrics?.LateCenter,
            metrics?.Drift,
            CalculateFixedDurationMs(),
            _reason);
    }

    private double CalculateFixedDurationMs() =>
        _fixedCycle?.StartedAtUtc is { } started
        && _fixedCycle.LastUpdatedAtUtc is { } last
            ? Math.Max(0d, (last - started).TotalMilliseconds)
            : 0d;

    private GapCycleUpdateResult ResetInternal(
        GapCycleTransition transition,
        string reason)
    {
        var completed = _gaps.Count > 0 ? BuildSnapshot() : null;
        Clear();
        _reason = reason;
        return new GapCycleUpdateResult(transition, BuildSnapshot(), completed);
    }

    private void StartNewCycle(
        DateTime timestampUtc,
        int gap,
        GapStabilityConfig config,
        string reason)
    {
        Clear();
        _startedAtUtc = timestampUtc;
        _lastTickUtc = timestampUtc;
        _cycleId = Guid.NewGuid().ToString("N");
        _gaps.Add(gap);
        _metrics = GapStabilityCalculator.Calculate(_gaps, config);
        _status = GapCycleStatus.Collecting;
        _reason = reason;
    }

    private void Clear()
    {
        _gaps.Clear();
        _cycleId = string.Empty;
        _startedAtUtc = null;
        _lastTickUtc = null;
        _metrics = null;
        _status = GapCycleStatus.Empty;
        _reason = "Chưa có Cycle.";
    }

    private GapCycleSnapshot BuildSnapshot(
        GapStabilityCalculator.Metrics? metrics = null)
    {
        metrics ??= _metrics;

        return new GapCycleSnapshot(
            _cycleId,
            _status,
            _startedAtUtc,
            _lastTickUtc,
            _gaps.ToArray(),
            metrics?.Center,
            metrics?.Mad,
            metrics?.Tolerance,
            metrics?.Dispersion,
            metrics?.EarlyCenter,
            metrics?.LateCenter,
            metrics?.Drift,
            CalculateDurationMs(),
            _reason);
    }

    private double CalculateDurationMs() =>
        _startedAtUtc.HasValue && _lastTickUtc.HasValue
            ? Math.Max(0d, (_lastTickUtc.Value - _startedAtUtc.Value).TotalMilliseconds)
            : 0d;

    private static string BuildUnstableReason(
        GapStabilityCalculator.Metrics metrics,
        GapStabilityConfig config)
    {
        var reasons = new List<string>(capacity: 2);
        if (metrics.Dispersion > config.MaxDispersion)
        {
            reasons.Add($"Dispersion {metrics.Dispersion:0.####} > {config.MaxDispersion:0.####}");
        }

        if (metrics.Drift > config.MaxDrift)
        {
            reasons.Add($"Drift {metrics.Drift:0.####} > {config.MaxDrift:0.####}");
        }

        return $"Cycle Unstable: {string.Join("; ", reasons)}.";
    }

    private static long Magnitude(int gap) => Math.Abs((long)gap);

    /// <summary>
    /// Mẫu mới có ngược dấu với dấu đầu tiên khác 0 của Cycle hiện tại hay không.
    /// Gap bằng 0 là trung tính, tương thích với cả hai chiều.
    /// </summary>
    private static bool HasSignConflict(IReadOnlyList<int> gaps, int newGap)
    {
        if (newGap == 0)
        {
            return false;
        }

        var newSign = Math.Sign(newGap);
        for (var i = 0; i < gaps.Count; i++)
        {
            var sign = Math.Sign(gaps[i]);
            if (sign == 0)
            {
                continue;
            }

            return sign != newSign;
        }

        return false;
    }
}
