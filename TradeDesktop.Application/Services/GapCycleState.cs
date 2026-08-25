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
    private DateTime? _startedAtUtc;
    private DateTime? _lastTickUtc;
    private GapStabilityCalculator.Metrics? _metrics;
    private GapCycleStatus _status = GapCycleStatus.Empty;
    private string _reason = "Chưa có Cycle.";

    public GapCycleSnapshot Current => BuildSnapshot();

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
        ResetInternal(GapCycleTransition.ResetExplicitly, reason);

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
        _gaps.Add(gap);
        _metrics = GapStabilityCalculator.Calculate(_gaps, config);
        _status = GapCycleStatus.Collecting;
        _reason = reason;
    }

    private void Clear()
    {
        _gaps.Clear();
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
}
