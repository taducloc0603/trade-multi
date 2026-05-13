using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public sealed class TradingFlowEngine(
    IOpenSignalEngine openSignalEngine,
    ICloseSignalEngine closeSignalEngine) : ITradingFlowEngine
{
    private const string GapCooldownSkipReason = "GAP_COOLDOWN_ACTIVE";
    private readonly Random _random = new();
    private static readonly TimeSpan SnapshotWallClockTolerance = TimeSpan.FromMinutes(5);
    private bool _isCloseExecutionPending;
    private DateTime? _openedAtRuntimeUtc;
    private DateTime? _closedAtRuntimeUtc;
    private int _openQualifyingCount;
    private int _closeQualifyingCount;
    private decimal? _previousAAsk;
    private decimal? _previousBAsk;
    private DateTime? _gapCoolDownUntilUtc;
    // FIX: Holding floor fallback.
    // Nếu vì bất kỳ lý do gì mà CurrentHoldingSeconds về 0 khi engine đang
    // WaitingClose (ví dụ ForceWaitingClose được gọi để sync state với live
    // pair khi app restart giữa chu kỳ), close-gate trong CanCheckClose sẽ
    // fallback sang giá trị của config.StartTimeHold gần nhất để không bao
    // giờ mở cổng close "free" trong khi đang giữ lệnh.
    // Giá trị này được cache từ config lần cuối engine nhìn thấy trong
    // ProcessSnapshot (config có tính stateless ở caller).
    private int _lastSeenStartTimeHold;
    private int _lastSeenEndTimeHold;

    public TradingFlowPhase CurrentPhase { get; private set; } = TradingFlowPhase.WaitingOpen;
    public TradingOpenMode CurrentOpenMode { get; private set; } = TradingOpenMode.None;
    public TradingPositionSide CurrentPositionSide { get; private set; } = TradingPositionSide.None;
    public DateTime? OpenedAtUtc { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }
    public int CurrentHoldingSeconds { get; private set; }
    public int CurrentWaitSeconds { get; private set; }
    public int CurrentOpenQualifyingCount => _openQualifyingCount;
    public int CurrentCloseQualifyingCount => _closeQualifyingCount;
    public TradingFlowSkipDiagnostic? LastSkipDiagnostic { get; private set; }

    public GapSignalTriggerResult? ProcessSnapshot(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config)
    {
        LastSkipDiagnostic = null;

        // FIX: cache config hold range cho close-gate fallback
        _lastSeenStartTimeHold = Math.Max(0, config.StartTimeHold);
        _lastSeenEndTimeHold = Math.Max(0, config.EndTimeHold);

        var effectiveNow = ResolveEffectiveNowUtc(snapshot.TimestampUtc);
        // [TEMPORARILY DISABLED] Gap_tick cooldown
        // ApplyGapSpikeCoolDown(snapshot, config, effectiveNow);
        // if (IsGapCoolDownActive(effectiveNow))
        // {
        //     SetGapCooldownSkipDiagnostic(config, effectiveNow);
        //     return null;
        // }

        if (CurrentPhase == TradingFlowPhase.WaitingOpen)
        {
            if (!CanCheckOpen(snapshot.TimestampUtc))
            {
                return null;
            }

            var openTrigger = openSignalEngine
                .ProcessSnapshot(snapshot, config)
                .FirstOrDefault(r => r.Triggered && r.Action == GapSignalAction.Open);

            if (openTrigger is null)
            {
                return null;
            }

            CurrentOpenMode = openTrigger.TriggerType == GapSignalTriggerType.OpenByGapBuy
                ? TradingOpenMode.GapBuy
                : TradingOpenMode.GapSell;

            CurrentPositionSide = openTrigger.PrimarySide == GapSignalSide.Buy
                ? TradingPositionSide.Buy
                : TradingPositionSide.Sell;

            OpenedAtUtc = openTrigger.TriggeredAtUtc;
            _openedAtRuntimeUtc = DateTime.UtcNow;
            _closedAtRuntimeUtc = null;
            CurrentHoldingSeconds = NextSecondsInRange(config.StartTimeHold, config.EndTimeHold);
            CurrentPhase = CurrentOpenMode == TradingOpenMode.GapBuy
                ? TradingFlowPhase.WaitingCloseFromGapBuy
                : TradingFlowPhase.WaitingCloseFromGapSell;
            _isCloseExecutionPending = false;
            closeSignalEngine.Reset();
            openSignalEngine.Reset();
            return openTrigger;
        }

        if ((CurrentPhase != TradingFlowPhase.WaitingCloseFromGapBuy && CurrentPhase != TradingFlowPhase.WaitingCloseFromGapSell)
            || CurrentOpenMode == TradingOpenMode.None
            || CurrentPositionSide == TradingPositionSide.None)
        {
            return null;
        }

        if (!CanCheckClose(snapshot.TimestampUtc))
        {
            return null;
        }

        var closeTrigger = closeSignalEngine.ProcessSnapshot(snapshot, config, CurrentOpenMode);
        if (closeTrigger is null || !closeTrigger.Triggered || closeTrigger.Action != GapSignalAction.Close)
        {
            return null;
        }

        _isCloseExecutionPending = true;
        ClosedAtUtc = null;
        CurrentWaitSeconds = 0;
        openSignalEngine.Reset();
        closeSignalEngine.Reset();
        return closeTrigger;
    }

    public void BeginWaitAfterClose(
        DateTime closeCompletedAtUtc,
        int startWaitSeconds,
        int endWaitSeconds)
    {
        var isWaitingClosePhase = CurrentPhase == TradingFlowPhase.WaitingCloseFromGapBuy
            || CurrentPhase == TradingFlowPhase.WaitingCloseFromGapSell;

        // Keep this transition idempotent and resilient: even if the pending flag was cleared
        // by a race/path outside normal happy-flow, external close confirmation should still be
        // able to move the state machine back to WaitingOpen.
        if (!_isCloseExecutionPending && (!isWaitingClosePhase || CurrentOpenMode == TradingOpenMode.None))
        {
            return;
        }

        _isCloseExecutionPending = false;
        CurrentPhase = TradingFlowPhase.WaitingOpen;
        CurrentOpenMode = TradingOpenMode.None;
        CurrentPositionSide = TradingPositionSide.None;
        OpenedAtUtc = null;
        _openedAtRuntimeUtc = null;
        CurrentHoldingSeconds = 0;
        ClosedAtUtc = closeCompletedAtUtc;
        _closedAtRuntimeUtc = DateTime.UtcNow;
        CurrentWaitSeconds = NextSecondsInRange(startWaitSeconds, endWaitSeconds);
        openSignalEngine.Reset();
        closeSignalEngine.Reset();
        ResetQualifyingCounters();
    }

    public void AbortPendingCloseExecution()
    {
        if (!_isCloseExecutionPending)
        {
            return;
        }

        _isCloseExecutionPending = false;
        ClosedAtUtc = null;
        _closedAtRuntimeUtc = null;
        CurrentWaitSeconds = 0;
        closeSignalEngine.Reset();
    }

    public void AbortPendingOpenExecution()
    {
        var isWaitingClose = CurrentPhase == TradingFlowPhase.WaitingCloseFromGapBuy
                          || CurrentPhase == TradingFlowPhase.WaitingCloseFromGapSell;
        if (!isWaitingClose || CurrentOpenMode == TradingOpenMode.None)
        {
            return;
        }

        _isCloseExecutionPending = false;
        CurrentPhase = TradingFlowPhase.WaitingOpen;
        CurrentOpenMode = TradingOpenMode.None;
        CurrentPositionSide = TradingPositionSide.None;
        OpenedAtUtc = null;
        _openedAtRuntimeUtc = null;
        ClosedAtUtc = null;
        _closedAtRuntimeUtc = null;
        CurrentHoldingSeconds = 0;
        CurrentWaitSeconds = 0;

        openSignalEngine.Reset();
        closeSignalEngine.Reset();
        // Keep qualifying counters for skip/guard-reject flow.
    }

    public bool TryConsumeQualifyingForOpen(int requiredN)
    {
        var effectiveN = Math.Max(1, requiredN);
        _openQualifyingCount++;

        if (_openQualifyingCount >= effectiveN)
        {
            _openQualifyingCount = 0;
            return true;
        }

        return false;
    }

    public bool TryConsumeQualifyingForClose(int requiredN)
    {
        var effectiveN = Math.Max(1, requiredN);
        _closeQualifyingCount++;

        if (_closeQualifyingCount >= effectiveN)
        {
            _closeQualifyingCount = 0;
            return true;
        }

        return false;
    }

    public void ResetQualifyingCounters()
    {
        _openQualifyingCount = 0;
        _closeQualifyingCount = 0;
    }

    public void ForceWaitingClose(TradingPositionSide positionSide)
    {
        if (positionSide == TradingPositionSide.None)
        {
            return;
        }

        _isCloseExecutionPending = false;
        CurrentWaitSeconds = 0;
        ClosedAtUtc = null;
        _closedAtRuntimeUtc = null;

        CurrentPositionSide = positionSide;
        CurrentOpenMode = positionSide == TradingPositionSide.Buy
            ? TradingOpenMode.GapBuy
            : TradingOpenMode.GapSell;
        CurrentPhase = CurrentOpenMode == TradingOpenMode.GapBuy
            ? TradingFlowPhase.WaitingCloseFromGapBuy
            : TradingFlowPhase.WaitingCloseFromGapSell;

        OpenedAtUtc ??= DateTime.UtcNow;
        _openedAtRuntimeUtc ??= DateTime.UtcNow;

        // FIX: KHÔNG reset CurrentHoldingSeconds về 0.
        // Nếu đã có giá trị hợp lệ từ trước (random khi open trigger) thì giữ nguyên.
        // Nếu chưa có (path sync state với live pair khi app vừa start),
        // random lại từ config hold range gần nhất để close-gate luôn có ý nghĩa.
        if (CurrentHoldingSeconds <= 0)
        {
            CurrentHoldingSeconds = NextSecondsInRange(_lastSeenStartTimeHold, _lastSeenEndTimeHold);
        }

        openSignalEngine.Reset();
        closeSignalEngine.Reset();
        ResetQualifyingCounters();
    }

    public void ForceWaitingOpen()
    {
        _isCloseExecutionPending = false;
        CurrentPhase = TradingFlowPhase.WaitingOpen;
        CurrentOpenMode = TradingOpenMode.None;
        CurrentPositionSide = TradingPositionSide.None;
        OpenedAtUtc = null;
        _openedAtRuntimeUtc = null;
        ClosedAtUtc = null;
        _closedAtRuntimeUtc = null;
        CurrentHoldingSeconds = 0;
        CurrentWaitSeconds = 0;
        openSignalEngine.Reset();
        closeSignalEngine.Reset();
        ResetQualifyingCounters();
    }

    public void Reset()
    {
        ForceWaitingOpen();
    }

    private bool CanCheckOpen(DateTime snapshotTimestampUtc)
    {
        var effectiveNow = ResolveEffectiveNowUtc(snapshotTimestampUtc);
        // [TEMPORARILY DISABLED] Gap_tick cooldown
        // if (IsGapCoolDownActive(effectiveNow))
        // {
        //     return false;
        // }

        if (!ClosedAtUtc.HasValue || CurrentWaitSeconds <= 0)
        {
            return true;
        }

        var baseline = _closedAtRuntimeUtc ?? ClosedAtUtc.Value;
        var elapsed = effectiveNow - baseline;
        return elapsed >= TimeSpan.FromSeconds(CurrentWaitSeconds);
    }

    private bool CanCheckClose(DateTime snapshotTimestampUtc)
    {
        if (_isCloseExecutionPending)
        {
            return false;
        }

        var effectiveNow = ResolveEffectiveNowUtc(snapshotTimestampUtc);
        // [TEMPORARILY DISABLED] Gap_tick cooldown
        // if (IsGapCoolDownActive(effectiveNow))
        // {
        //     return false;
        // }

        // FIX: close-gate safety floor.
        // Nếu chưa có OpenedAtUtc → thực sự chưa mở lệnh → cho qua (no-op)
        if (!OpenedAtUtc.HasValue)
        {
            return true;
        }

        // Resolve effective holding seconds với floor từ config đã thấy gần nhất.
        // Mục tiêu: nếu CurrentHoldingSeconds bị reset về 0 (race/bug/ForceWaitingClose
        // path cũ), vẫn áp mức sàn = _lastSeenStartTimeHold để không mở cổng close tự do.
        var effectiveHoldingSeconds = CurrentHoldingSeconds > 0
            ? CurrentHoldingSeconds
            : _lastSeenStartTimeHold;

        if (effectiveHoldingSeconds <= 0)
        {
            // Config cũng không yêu cầu hold → cho qua (hành vi cũ khi feature tắt)
            return true;
        }

        var baseline = _openedAtRuntimeUtc ?? OpenedAtUtc.Value;
        var elapsed = effectiveNow - baseline;
        return elapsed >= TimeSpan.FromSeconds(effectiveHoldingSeconds);
    }

    private void ApplyGapSpikeCoolDown(
        GapSignalSnapshot snapshot,
        GapSignalConfirmationConfig config,
        DateTime effectiveNow)
    {
        var thresholdPts = CurrentPhase == TradingFlowPhase.WaitingOpen
            ? Math.Max(0, config.OpenGapTick)
            : Math.Max(0, config.CloseGapTick);

        var coolDownSeconds = Math.Max(0, config.CoolDownGapTick);
        var pointMultiplier = Math.Max(1, snapshot.PointMultiplier);

        var currentAAsk = snapshot.ExchangeAAsk;
        var currentBAsk = snapshot.ExchangeBAsk;

        var deltaAPts = CalculateAskDeltaPts(_previousAAsk, currentAAsk, pointMultiplier);
        var deltaBPts = CalculateAskDeltaPts(_previousBAsk, currentBAsk, pointMultiplier);

        if (thresholdPts > 0 && coolDownSeconds > 0 && (deltaAPts > thresholdPts || deltaBPts > thresholdPts))
        {
            var nextUntil = effectiveNow.AddSeconds(coolDownSeconds);
            if (!_gapCoolDownUntilUtc.HasValue || nextUntil > _gapCoolDownUntilUtc.Value)
            {
                _gapCoolDownUntilUtc = nextUntil;
            }
        }

        if (currentAAsk.HasValue)
        {
            _previousAAsk = currentAAsk;
        }

        if (currentBAsk.HasValue)
        {
            _previousBAsk = currentBAsk;
        }
    }

    private bool IsGapCoolDownActive(DateTime effectiveNow)
        => _gapCoolDownUntilUtc.HasValue && effectiveNow < _gapCoolDownUntilUtc.Value;

    private void SetGapCooldownSkipDiagnostic(
        GapSignalConfirmationConfig config,
        DateTime effectiveNow)
    {
        var cooldownLeftMs = _gapCoolDownUntilUtc.HasValue
            ? Math.Max(0, (int)Math.Ceiling((_gapCoolDownUntilUtc.Value - effectiveNow).TotalMilliseconds))
            : 0;

        LastSkipDiagnostic = new TradingFlowSkipDiagnostic(
            Reason: GapCooldownSkipReason,
            Phase: CurrentPhase,
            CooldownLeftMs: cooldownLeftMs,
            OpenGapTick: Math.Max(0, config.OpenGapTick),
            CloseGapTick: Math.Max(0, config.CloseGapTick));
    }

    private static int CalculateAskDeltaPts(decimal? previousAsk, decimal? currentAsk, int pointMultiplier)
    {
        if (!previousAsk.HasValue || !currentAsk.HasValue)
        {
            return 0;
        }

        var delta = Math.Abs(currentAsk.Value - previousAsk.Value);
        return (int)(delta * pointMultiplier);
    }

    private static DateTime ResolveEffectiveNowUtc(DateTime snapshotTimestampUtc)
    {
        var snapshotUtc = snapshotTimestampUtc.Kind switch
        {
            DateTimeKind.Utc => snapshotTimestampUtc,
            DateTimeKind.Local => snapshotTimestampUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(snapshotTimestampUtc, DateTimeKind.Utc)
        };

        var wallUtc = DateTime.UtcNow;
        var delta = snapshotUtc - wallUtc;

        // For live streams (timestamp close to wall clock), protect against stale/frozen snapshot time
        // by advancing with wall clock. For synthetic/backtest timestamps far from wall clock,
        // keep original behavior to preserve deterministic test scenarios.
        if (Math.Abs(delta.TotalMinutes) <= SnapshotWallClockTolerance.TotalMinutes)
        {
            return snapshotUtc > wallUtc ? snapshotUtc : wallUtc;
        }

        return snapshotUtc;
    }

    private int NextSecondsInRange(int minSeconds, int maxSeconds)
    {
        var min = Math.Max(0, minSeconds);
        var max = Math.Max(0, maxSeconds);
        if (min > max)
        {
            (min, max) = (max, min);
        }

        if (min == max)
        {
            return min;
        }

        return _random.Next(min, max + 1);
    }
}
