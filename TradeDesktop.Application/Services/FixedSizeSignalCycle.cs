namespace TradeDesktop.Application.Services;

public enum FixedSizeCycleStatus
{
    Empty = 0,
    Collecting = 1,
    Completed = 2
}

public sealed record FixedSizeCycleUpdate(
    string CycleId,
    FixedSizeCycleStatus Status,
    int Count,
    int RequiredSize,
    bool WasReset,
    bool WasRestarted,
    bool DuplicateIgnored,
    string Reason);

/// <summary>
/// Tracks the fixed number of accepted events in one signal cycle. The caller owns
/// domain validation and decides whether an event is added, starts a new cycle, or
/// resets the current cycle.
/// </summary>
public sealed class FixedSizeSignalCycle<T>
{
    private readonly List<T> _values = [];
    private string? _lastFingerprint;

    public FixedSizeSignalCycle(int requiredSize)
    {
        RequiredSize = ValidateRequiredSize(requiredSize);
    }

    public string? CycleId { get; private set; }
    public int RequiredSize { get; private set; }
    public int Count => _values.Count;
    public FixedSizeCycleStatus Status { get; private set; } = FixedSizeCycleStatus.Empty;
    public bool IsComplete => Status == FixedSizeCycleStatus.Completed;
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? LastUpdatedAtUtc { get; private set; }
    public string? LastResetReason { get; private set; }
    public IReadOnlyList<T> Values => _values;

    public FixedSizeCycleUpdate Add(T value, DateTime timestampUtc, string fingerprint)
    {
        ValidateEvent(timestampUtc, fingerprint);

        if (LastUpdatedAtUtc.HasValue && timestampUtc < LastUpdatedAtUtc.Value)
        {
            return Reset("TIMESTAMP_REGRESSION");
        }

        if (_lastFingerprint is not null
            && string.Equals(_lastFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return Snapshot("DUPLICATE_IGNORED", duplicateIgnored: true);
        }

        if (IsComplete)
        {
            throw new InvalidOperationException(
                "Fixed-size cycle đã hoàn thành; phải reset hoặc restart trước khi thêm phần tử mới.");
        }

        if (Status == FixedSizeCycleStatus.Empty)
        {
            CycleId = Guid.NewGuid().ToString("N");
            StartedAtUtc = timestampUtc;
            Status = FixedSizeCycleStatus.Collecting;
        }

        _values.Add(value);
        _lastFingerprint = fingerprint;
        LastUpdatedAtUtc = timestampUtc;
        LastResetReason = null;

        if (Count == RequiredSize)
        {
            Status = FixedSizeCycleStatus.Completed;
            return Snapshot("CYCLE_COMPLETED");
        }

        return Snapshot(Count == 1 ? "CYCLE_STARTED" : "ELEMENT_ADDED");
    }

    public FixedSizeCycleUpdate RestartWith(
        T value,
        DateTime timestampUtc,
        string fingerprint,
        string reason)
    {
        ValidateEvent(timestampUtc, fingerprint);
        ValidateReason(reason);

        ResetState(reason);
        var update = Add(value, timestampUtc, fingerprint);
        return update with
        {
            WasReset = true,
            WasRestarted = true,
            Reason = reason
        };
    }

    public FixedSizeCycleUpdate Reset(string reason)
    {
        ValidateReason(reason);
        var previousCycleId = CycleId ?? string.Empty;
        var hadState = Status != FixedSizeCycleStatus.Empty || Count > 0;
        ResetState(reason);

        return new FixedSizeCycleUpdate(
            previousCycleId,
            FixedSizeCycleStatus.Empty,
            0,
            RequiredSize,
            WasReset: hadState,
            WasRestarted: false,
            DuplicateIgnored: false,
            Reason: reason);
    }

    public FixedSizeCycleUpdate Resize(int requiredSize)
    {
        var normalized = ValidateRequiredSize(requiredSize);
        if (normalized == RequiredSize)
        {
            return Snapshot("CYCLE_SIZE_UNCHANGED");
        }

        var previousCycleId = CycleId ?? string.Empty;
        var hadState = Status != FixedSizeCycleStatus.Empty || Count > 0;
        RequiredSize = normalized;
        ResetState("CYCLE_SIZE_CHANGED");

        return new FixedSizeCycleUpdate(
            previousCycleId,
            FixedSizeCycleStatus.Empty,
            0,
            RequiredSize,
            WasReset: hadState,
            WasRestarted: false,
            DuplicateIgnored: false,
            Reason: "CYCLE_SIZE_CHANGED");
    }

    private FixedSizeCycleUpdate Snapshot(
        string reason,
        bool duplicateIgnored = false) =>
        new(
            CycleId ?? string.Empty,
            Status,
            Count,
            RequiredSize,
            WasReset: false,
            WasRestarted: false,
            DuplicateIgnored: duplicateIgnored,
            Reason: reason);

    private void ResetState(string reason)
    {
        _values.Clear();
        _lastFingerprint = null;
        CycleId = null;
        Status = FixedSizeCycleStatus.Empty;
        StartedAtUtc = null;
        LastUpdatedAtUtc = null;
        LastResetReason = reason;
    }

    private static int ValidateRequiredSize(int requiredSize)
    {
        if (requiredSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredSize),
                "Kích thước signal cycle phải >= 1.");
        }

        return requiredSize;
    }

    private static void ValidateEvent(DateTime timestampUtc, string fingerprint)
    {
        if (timestampUtc == default)
        {
            throw new ArgumentException("Timestamp của cycle event không hợp lệ.", nameof(timestampUtc));
        }

        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ArgumentException("Fingerprint của cycle event không được để trống.", nameof(fingerprint));
        }
    }

    private static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Reset reason không được để trống.", nameof(reason));
        }
    }
}
