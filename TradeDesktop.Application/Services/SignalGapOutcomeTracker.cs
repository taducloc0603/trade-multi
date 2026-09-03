using System.Globalization;
using System.Text;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

/// <summary>
/// Một tick giá được feed vào tracker. Chỉ mang gap (đơn vị point) nên không giữ
/// tham chiếu tới snapshot/metrics, tránh chi phí trên UI thread.
/// </summary>
public readonly record struct SignalOutcomeTick(DateTime TimestampUtc, int? GapBuy, int? GapSell);

/// <summary>
/// Ảnh chụp trạng thái tại thời điểm một signal được dispatch. Đây là dữ liệu thuần,
/// tracker không đọc lại engine/coordinator.
/// </summary>
public sealed record SignalOutcomeSignal(
    string SignalId,
    string CycleId,
    int? SlotId,
    GapSignalAction Action,
    GapSignalSide Side,
    GapSignalTriggerType TriggerType,
    DateTime TriggeredAtUtc,
    string Symbol,
    int PointMultiplier,
    decimal? ABid,
    decimal? AAsk,
    decimal? BBid,
    decimal? BAsk,
    int? GapBuy,
    int? GapSell,
    IReadOnlyList<int> SignalGaps,
    int ConfirmGapPts,
    int OpenPts,
    int CloseConfirmGapPts,
    int ClosePts,
    int LimitMaxGap,
    int MaxGap,
    int SignalCycleSize,
    // Nhánh TIME: hai giá trị quyết định chu kỳ. SignalCycleSize ở trên chỉ để đối chiếu
    // với log của nhánh TICK.
    int HoldConfirmMs = 0,
    int CloseHoldConfirmMs = 0);

/// <summary>
/// Ghi lại gap tại thời điểm signal kèm dãy gap của N tick kế tiếp, phục vụ đánh giá
/// hậu nghiệm chất lượng signal. Đây là thành phần CHỈ LOGGING: không tạo, không chặn
/// và không quan sát bất kỳ quyết định open/close nào.
///
/// Vòng đời một trace gồm 2 pha:
/// <list type="number">
/// <item><see cref="OnSignalPending"/> mở trace ngay tại tick signal (khoá baseline gap và
/// bắt đầu đếm tick) nhưng CHƯA ghi gì ra file.</item>
/// <item><see cref="AttachStt"/> gắn số STT hiển thị trên UI (chỉ có sau khi pairId được
/// sinh ra trong luồng dispatch) và ghi dòng <c>[SIGNAL]</c>.</item>
/// </list>
/// Trace không được attach trong <c>attachGraceTicks</c> sẽ bị bỏ im lặng — đó chính là
/// các signal bị chặn ở những gate nằm sau điểm mở trace.
///
/// LƯU Ý: class này KHÔNG thread-safe. Nó được thiết kế để chỉ gọi từ UI thread
/// (mọi call-site nằm trong <c>Dispatcher.Invoke</c> của DashboardViewModel).
/// </summary>
public sealed class SignalGapOutcomeTracker
{
    private const string SignalEvent = "SIGNAL";
    private const string EndEvent = "END";
    private const string ExecTag = "EXEC";
    private const string BlockedTag = "BLOCKED";

    /// <summary>Lý do gán cho trace không được dispatch cũng không được báo chặn — xem <see cref="OnTick"/>.</summary>
    public const string UnresolvedBlockReason = "UNRESOLVED";

    private readonly ISignalOutcomeRawLogger _logger;
    private readonly int _futureTickCount;
    private readonly int _maxConcurrentTraces;
    private readonly int _staleTraceMs;
    private readonly int _attachGraceTicks;
    private readonly int _blockStreakIdleMs;
    private readonly int _maxConcurrentStreaks;
    private readonly List<Trace> _traces = new();
    private readonly Dictionary<(GapSignalAction Action, GapSignalSide Side, string Reason), BlockStreak> _streaks = new();

    public SignalGapOutcomeTracker(
        ISignalOutcomeRawLogger logger,
        int futureTickCount = 50,
        // Mở trace cho cả signal bị chặn nên cần biên rộng hơn: cap quá thấp sẽ evict nhầm
        // một trace EXEC đang thu dở (eviction ghi [END] status=EVICTED, tức cắt cụt dữ liệu thật).
        int maxConcurrentTraces = 32,
        int staleTraceMs = 30_000,
        // ~30s ở nhịp tick 50ms. Phải rộng hơn NHIỀU so với cửa sổ 50 tick: đường dispatch
        // có thể chờ physical mutex không giới hạn, cộng DelayMs mỗi leg và thời gian click
        // native. Grace quá ngắn sẽ vứt bỏ bản ghi của một lệnh vào thật.
        // Cửa sổ chờ attach thực tế = min(attachGraceTicks nhịp tick, staleTraceMs đồng hồ),
        // tức ~30s ở cả hai đầu với giá trị mặc định.
        int attachGraceTicks = 600,
        int blockStreakIdleMs = 3_000,
        int maxConcurrentStreaks = 16)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _futureTickCount = Math.Max(1, futureTickCount);
        _maxConcurrentTraces = Math.Max(1, maxConcurrentTraces);
        _staleTraceMs = Math.Max(0, staleTraceMs);
        _attachGraceTicks = Math.Max(1, attachGraceTicks);
        _blockStreakIdleMs = Math.Max(0, blockStreakIdleMs);
        _maxConcurrentStreaks = Math.Max(1, maxConcurrentStreaks);
    }

    /// <summary>
    /// Chỉ theo dõi signal OPEN và signal CLOSE loại Normal (gap đảo chiều thường).
    /// SOS close và TP close nằm ngoài phạm vi.
    /// </summary>
    public static bool ShouldTrack(GapSignalAction action, CloseSignalReason reason, CloseGapMode mode)
        => action == GapSignalAction.Open
            || (action == GapSignalAction.Close
                && reason == CloseSignalReason.Gap
                && mode == CloseGapMode.Normal);

    /// <summary>
    /// Chiều gap thực sự dẫn dắt signal — dùng để chọn đúng gap list, gap baseline và chiều
    /// gap theo dõi cho <c>future_gaps</c>.
    ///
    /// CẢNH BÁO: KHÔNG được suy ra từ <c>PrimarySide</c>. Với CLOSE, <c>PrimarySide</c> là chiều
    /// của VỊ THẾ đang đóng, không phải chiều gap: một vị thế Buy được đóng bằng gap Sell đảo
    /// chiều, nên <c>CloseSignalEngine</c> điền <c>SellGaps</c> và để <c>BuyGaps</c> rỗng
    /// (CloseSignalEngine.cs:320-321) trong khi <c>PrimarySide</c> vẫn là Buy. Chỉ
    /// <c>TriggerType</c> mới khớp với gap list mà engine đã điền ở cả hai nhánh Open/Close.
    /// </summary>
    public static bool TracksBuyGap(GapSignalTriggerType triggerType)
        => triggerType is GapSignalTriggerType.OpenByGapBuy or GapSignalTriggerType.CloseByGapBuy;

    /// <summary>
    /// Mở trace tại đúng tick signal. Chưa ghi ra file — chờ <see cref="AttachStt"/>.
    /// Bỏ qua nếu gap của chiều đang theo dõi không có giá trị (không có baseline để so).
    /// </summary>
    public void OnSignalPending(SignalOutcomeSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        // Không có id thì AttachStt/MarkBlocked đều không tìm lại được trace -> nó sẽ sống tới
        // hết grace rồi phát UNRESOLVED giả, và tệ hơn là chiếm chỗ đẩy trace EXEC thật ra khỏi
        // danh sách khi chạm cap. Từ chối ngay, cùng chuẩn với hai hàm gắn nhãn.
        if (string.IsNullOrWhiteSpace(signal.SignalId))
        {
            return;
        }

        var trackBuy = TracksBuyGap(signal.TriggerType);
        var baseline = trackBuy ? signal.GapBuy : signal.GapSell;
        if (baseline is null)
        {
            return;
        }

        if (_traces.Count >= _maxConcurrentTraces)
        {
            EmitAndRemoveAt(0, "EVICTED");
        }

        _traces.Add(new Trace(signal, trackBuy, baseline.Value, _futureTickCount));
    }

    /// <summary>
    /// Gắn STT hiển thị trên UI + pairId cho trace và ghi dòng <c>[SIGNAL]</c>.
    /// <paramref name="slotId"/> dùng cho OPEN: tại thời điểm signal slot chưa được cấp,
    /// tới lúc attach mới biết. <paramref name="stt"/> có thể null nếu pair chưa được UI cấp
    /// số — khi đó ghi <c>stt=-</c> và dò ngược bằng <c>pair_id</c>; tuyệt đối không tự cấp số
    /// mới vì đó là trạng thái hiển thị của UI.
    /// Idempotent: gọi lại trên cùng signal không sinh thêm dòng nào.
    /// </summary>
    public void AttachStt(string signalId, int? stt, string pairId, int? slotId = null)
    {
        if (string.IsNullOrWhiteSpace(signalId))
        {
            return;
        }

        for (var i = 0; i < _traces.Count; i++)
        {
            var trace = _traces[i];
            if (!string.Equals(trace.Signal.SignalId, signalId, StringComparison.Ordinal))
            {
                continue;
            }

            if (trace.SignalLineWritten)
            {
                return;
            }

            trace.Stt = stt;
            trace.PairId = pairId;
            trace.SlotIdOverride = slotId;
            trace.SignalLineWritten = true;
            _logger.LogSignalOutcomeRaw(FormatSignalLine(trace));

            // Có lệnh vào thật cùng chiều => đợt bị chặn trước đó đã kết thúc.
            CloseStreaksFor(trace.Signal.Action, trace.Signal.Side, "EXEC");

            // Dispatch chậm hơn cả cửa sổ quan sát: dữ liệu đã đủ, ghi luôn dòng END.
            if (trace.Count >= _futureTickCount)
            {
                EmitEndLine(trace, "COMPLETED");
                _traces.RemoveAt(i);
            }

            return;
        }
    }

    /// <summary>
    /// Đánh dấu một signal đã bị chặn, không vào lệnh. Ghi dòng <c>[SIGNAL][BLOCKED]</c> cho
    /// signal ĐẦU TIÊN của mỗi đợt; các signal sau cùng lý do được gộp vào streak và không ghi
    /// dòng nào — chống ngập file khi một lý do chặn kéo dài (engine reset cycle ngay khi phát
    /// trigger nên signal có thể tái phát mỗi vài trăm ms).
    /// No-op nếu signal chưa từng được track, hoặc đã ghi dòng <c>[SIGNAL]</c> rồi (EXEC luôn thắng).
    /// </summary>
    public void MarkBlocked(
        string signalId,
        string blockReason,
        int? stt = null,
        string? pairId = null,
        int? slotId = null)
    {
        if (string.IsNullOrWhiteSpace(signalId))
        {
            return;
        }

        for (var i = 0; i < _traces.Count; i++)
        {
            var trace = _traces[i];
            if (!string.Equals(trace.Signal.SignalId, signalId, StringComparison.Ordinal))
            {
                continue;
            }

            // Đã dispatch (hoặc đã ghi BLOCKED) => giữ nguyên, không ghi đè nhãn.
            if (trace.SignalLineWritten)
            {
                return;
            }

            MarkBlockedAt(i, trace, blockReason, stt, pairId, slotId);
            return;
        }
    }

    /// <returns><c>true</c> nếu trace đã được gỡ khỏi <see cref="_traces"/>.</returns>
    private bool MarkBlockedAt(
        int index,
        Trace trace,
        string blockReason,
        int? stt,
        string? pairId,
        int? slotId)
    {
        var reason = NormalizeBlockReason(blockReason);
        var key = (trace.Signal.Action, trace.Signal.Side, reason);
        var nowUtc = DateTime.UtcNow;

        if (_streaks.TryGetValue(key, out var streak))
        {
            // Đợt đang chạy: gộp, không ghi dòng nào.
            streak.Count++;
            streak.LastAtUtc = nowUtc;
            streak.LastSignalId = trace.Signal.SignalId;
            _traces.RemoveAt(index);
            return true;
        }

        if (_streaks.Count >= _maxConcurrentStreaks)
        {
            EvictOldestStreak();
        }

        _streaks[key] = new BlockStreak(trace.Signal.SignalId, nowUtc);

        trace.BlockReason = reason;
        trace.Stt = stt;
        trace.PairId = pairId;
        trace.SlotIdOverride = slotId;
        trace.SignalLineWritten = true;
        _logger.LogSignalOutcomeRaw(FormatSignalLine(trace));

        // Bị chặn muộn hơn cả cửa sổ quan sát: dữ liệu đã đủ, ghi luôn dòng END.
        if (trace.Count >= _futureTickCount)
        {
            EmitEndLine(trace, "COMPLETED");
            _traces.RemoveAt(index);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Feed một tick vào mọi trace đang mở. O(số trace), không allocation.
    /// </summary>
    public void OnTick(in SignalOutcomeTick tick)
    {
        // Phải kiểm cả _streaks: streak cuối của một đợt chỉ được flush từ đây, kể cả khi
        // không còn trace nào đang mở.
        if (_traces.Count == 0 && _streaks.Count == 0)
        {
            return;
        }

        CloseIdleStreaks();

        if (_traces.Count == 0)
        {
            return;
        }

        var nowMs = Environment.TickCount64;

        for (var i = _traces.Count - 1; i >= 0; i--)
        {
            var trace = _traces[i];
            trace.TicksSinceOpen++;

            // Hết grace mà không dispatch cũng không báo chặn => có gate chưa hook. Ghi
            // block_reason=UNRESOLVED (qua streak nên không ngập) làm chỉ báo coverage:
            // file sạch UNRESOLVED nghĩa là mọi gate đã được bắt.
            if (!trace.SignalLineWritten && trace.TicksSinceOpen > _attachGraceTicks)
            {
                // Chỉ bỏ qua tick này nếu trace đã bị gỡ; nếu còn sống thì vẫn thu gap của
                // chính tick đó để captured không lệch 1.
                if (MarkBlockedAt(i, trace, UnresolvedBlockReason, stt: null, pairId: null, slotId: null))
                {
                    continue;
                }
            }

            // STALE CHỈ áp cho trace ĐÃ gắn nhãn — nghĩa là "đã ghi [SIGNAL] nhưng feed tick đứt,
            // không thu đủ cửa sổ". Nếu áp cho cả trace chưa gắn nhãn thì nó tranh với grace:
            // attachGraceTicks(600) x PollInterval(50ms) = đúng 30_000ms = staleTraceMs, nên nhánh
            // nào thắng phụ thuộc jitter, và STALE gỡ trace chưa gắn nhãn mà KHÔNG ghi gì
            // (EmitAndRemoveAt chỉ ghi khi SignalLineWritten) => UNRESOLVED thành code chết.
            if (trace.SignalLineWritten
                && _staleTraceMs > 0
                && nowMs - trace.OpenedAtTickMs > _staleTraceMs)
            {
                EmitAndRemoveAt(i, "STALE");
                continue;
            }

            var gap = trace.TrackBuy ? tick.GapBuy : tick.GapSell;
            if (gap is null)
            {
                trace.SkippedNullTicks++;
                continue;
            }

            if (trace.Count >= _futureTickCount)
            {
                continue;
            }

            if (trace.Count == 0)
            {
                trace.FirstTickUtc = tick.TimestampUtc;
            }

            trace.Gaps[trace.Count++] = gap.Value;
            trace.LastTickUtc = tick.TimestampUtc;

            if (trace.Count >= _futureTickCount && trace.SignalLineWritten)
            {
                EmitEndLine(trace, "COMPLETED");
                _traces.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Ghi dòng END dở dang cho mọi trace đã attach rồi xoá sạch. Dùng khi dừng session.
    /// </summary>
    public void FlushAll(string status)
    {
        for (var i = _traces.Count - 1; i >= 0; i--)
        {
            EmitAndRemoveAt(i, status);
        }

        CloseAllStreaks("FLUSH");
    }

    /// <summary>Xoá mọi trace và streak đang mở, không ghi gì. Dùng khi bắt đầu session mới.</summary>
    public void Reset()
    {
        _traces.Clear();
        _streaks.Clear();
    }

    private void CloseIdleStreaks()
    {
        if (_streaks.Count == 0)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        List<(GapSignalAction, GapSignalSide, string)>? expired = null;

        foreach (var pair in _streaks)
        {
            if ((nowUtc - pair.Value.LastAtUtc).TotalMilliseconds > _blockStreakIdleMs)
            {
                (expired ??= new(2)).Add(pair.Key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var key in expired)
        {
            EmitStreakSummary(key, _streaks[key], "IDLE");
            _streaks.Remove(key);
        }
    }

    private void CloseStreaksFor(GapSignalAction action, GapSignalSide side, string closedBy)
    {
        if (_streaks.Count == 0)
        {
            return;
        }

        List<(GapSignalAction, GapSignalSide, string)>? matched = null;

        foreach (var pair in _streaks)
        {
            if (pair.Key.Action == action && pair.Key.Side == side)
            {
                (matched ??= new(2)).Add(pair.Key);
            }
        }

        if (matched is null)
        {
            return;
        }

        foreach (var key in matched)
        {
            EmitStreakSummary(key, _streaks[key], closedBy);
            _streaks.Remove(key);
        }
    }

    private void CloseAllStreaks(string closedBy)
    {
        if (_streaks.Count == 0)
        {
            return;
        }

        foreach (var pair in _streaks)
        {
            EmitStreakSummary(pair.Key, pair.Value, closedBy);
        }

        _streaks.Clear();
    }

    private void EvictOldestStreak()
    {
        (GapSignalAction, GapSignalSide, string)? oldestKey = null;
        var oldestAt = DateTime.MaxValue;

        foreach (var pair in _streaks)
        {
            if (pair.Value.LastAtUtc < oldestAt)
            {
                oldestAt = pair.Value.LastAtUtc;
                oldestKey = pair.Key;
            }
        }

        if (oldestKey is null)
        {
            return;
        }

        EmitStreakSummary(oldestKey.Value, _streaks[oldestKey.Value], "EVICTED");
        _streaks.Remove(oldestKey.Value);
    }

    private void EmitStreakSummary(
        (GapSignalAction Action, GapSignalSide Side, string Reason) key,
        BlockStreak streak,
        string closedBy)
    {
        // Không nén được signal nào thì dòng tổng kết không thêm thông tin gì so với cặp
        // [SIGNAL]/[END] đã ghi — bỏ đi để chặn đơn lẻ (guard, qualifying) chỉ tốn 2 dòng.
        if (streak.Count <= 1)
        {
            return;
        }

        var durationMs = (int)Math.Max(0, (streak.LastAtUtc - streak.FirstAtUtc).TotalMilliseconds);
        _logger.LogSignalOutcomeRaw(
            "[SIGNAL_OUTCOME][BLOCK_STREAK] " +
            $"action={ActionText(key.Action)} side={SideText(key.Side)} " +
            $"block_reason={Text(key.Reason)} " +
            $"blocked_count={Value(streak.Count)} suppressed_count={Value(streak.Count - 1)} " +
            $"logged_signal_id={Text(streak.FirstSignalId)} last_signal_id={Text(streak.LastSignalId)} " +
            $"first_at={Timestamp(streak.FirstAtUtc)} last_at={Timestamp(streak.LastAtUtc)} " +
            $"duration_ms={Value(durationMs)} closed_by={Text(closedBy)}");
    }

    private void EmitAndRemoveAt(int index, string status)
    {
        var trace = _traces[index];
        if (trace.SignalLineWritten)
        {
            EmitEndLine(trace, status);
        }

        _traces.RemoveAt(index);
    }

    private void EmitEndLine(Trace trace, string status) => _logger.LogSignalOutcomeRaw(FormatEndLine(trace, status));

    private string FormatSignalLine(Trace trace)
    {
        var s = trace.Signal;
        var sb = new StringBuilder(512);
        sb.Append("[SIGNAL_OUTCOME][").Append(SignalEvent).Append("][").Append(OutcomeTag(trace)).Append("] ");
        AppendIdentity(sb, trace);
        AppendBlockReason(sb, trace);
        sb.Append("trigger_type=").Append(s.TriggerType)
          .Append(" triggered_at=").Append(Timestamp(s.TriggeredAtUtc))
          .Append(" symbol=\"").Append(Escape(Text(s.Symbol))).Append('"')
          .Append(" point=").Append(Value(s.PointMultiplier))
          .Append(" confirm_gap_pts=").Append(Value(s.ConfirmGapPts))
          .Append(" open_pts=").Append(Value(s.OpenPts))
          .Append(" close_confirm_gap_pts=").Append(Value(s.CloseConfirmGapPts))
          .Append(" close_pts=").Append(Value(s.ClosePts))
          .Append(" limit_max_gap=").Append(Value(s.LimitMaxGap))
          .Append(" max_gap=").Append(Value(s.MaxGap))
          .Append(" signal_cycle_size=").Append(Value(s.SignalCycleSize))
          .Append(" open_hold_confirm_ms=").Append(Value(s.HoldConfirmMs))
          .Append(" close_hold_confirm_ms=").Append(Value(s.CloseHoldConfirmMs))
          .Append(" confirmation_mode=TIME_AND_MIN_SAMPLES")
          .Append(" a_bid=").Append(Price(s.ABid))
          .Append(" a_ask=").Append(Price(s.AAsk))
          .Append(" b_bid=").Append(Price(s.BBid))
          .Append(" b_ask=").Append(Price(s.BAsk))
          .Append(" gap_buy=").Append(Value(s.GapBuy))
          .Append(" gap_sell=").Append(Value(s.GapSell))
          .Append(" track_gap=").Append(trace.TrackBuy ? "BUY" : "SELL")
          .Append(" gap_at_signal=").Append(Value(trace.BaselineGap))
          .Append(" gaps_order=oldest_to_newest gaps_unit=point signal_gaps=\"");
        AppendGaps(sb, s.SignalGaps);
        sb.Append("\" future_ticks_planned=").Append(Value(_futureTickCount));
        return sb.ToString();
    }

    private string FormatEndLine(Trace trace, string status)
    {
        var s = trace.Signal;
        var sb = new StringBuilder(768);
        sb.Append("[SIGNAL_OUTCOME][").Append(EndEvent).Append("][").Append(OutcomeTag(trace)).Append("] ");
        AppendIdentity(sb, trace);
        AppendBlockReason(sb, trace);
        sb.Append("track_gap=").Append(trace.TrackBuy ? "BUY" : "SELL")
          .Append(" status=").Append(Text(status))
          .Append(" captured=").Append(Value(trace.Count)).Append('/').Append(Value(_futureTickCount))
          .Append(" skipped_null_ticks=").Append(Value(trace.SkippedNullTicks))
          .Append(" gap_at_signal=").Append(Value(trace.BaselineGap))
          .Append(" elapsed_ms=").Append(Value(ElapsedMs(trace)))
          .Append(" first_tick_at=").Append(Timestamp(trace.FirstTickUtc))
          .Append(" last_tick_at=").Append(Timestamp(trace.LastTickUtc))
          .Append(" gaps_order=oldest_to_newest gaps_unit=point signal_gaps=\"");
        AppendGaps(sb, s.SignalGaps);
        sb.Append("\" future_gaps=\"");
        AppendGaps(sb, trace.Gaps, trace.Count);
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Phần định danh chung của cả 2 dòng: <c>stt</c> là ĐÚNG số STT hiển thị trên UI
    /// của lệnh (cùng nguồn <c>ResolveStt</c> theo pairId), nên tra ngược log ↔ grid khớp.
    /// </summary>
    /// <summary>
    /// Nhãn phân biệt ở đầu dòng: EXEC = lệnh đã được gửi tới sàn, BLOCKED = signal bị chặn.
    /// </summary>
    private static string OutcomeTag(Trace trace) => trace.BlockReason is null ? ExecTag : BlockedTag;

    /// <summary>
    /// <c>block_reason</c> CHỈ xuất hiện ở dòng BLOCKED, nên parser cũ đọc dòng EXEC không gặp field lạ.
    /// </summary>
    private static void AppendBlockReason(StringBuilder sb, Trace trace)
    {
        if (trace.BlockReason is not null)
        {
            sb.Append("block_reason=").Append(Text(trace.BlockReason)).Append(' ');
        }
    }

    /// <summary>
    /// Ép lý do chặn về đúng MỘT token không khoảng trắng. Caller đã chuẩn hoá, nhưng tracker
    /// sở hữu định dạng <c>key=value</c> nên phải tự bảo vệ: một chuỗi tự do lọt vào
    /// <c>block_reason=</c> sẽ vỡ định dạng dòng và làm nổ số key streak.
    /// </summary>
    private static string NormalizeBlockReason(string? blockReason)
    {
        if (string.IsNullOrWhiteSpace(blockReason))
        {
            return "UNKNOWN";
        }

        var trimmed = blockReason.Trim();
        var separator = trimmed.IndexOfAny([' ', '\t', '(', '"']);
        var token = separator >= 0 ? trimmed[..separator] : trimmed;
        return token.Length == 0 ? "UNKNOWN" : token;
    }

    private static string ActionText(GapSignalAction action)
        => action == GapSignalAction.Open ? "OPEN" : "CLOSE";

    private static string SideText(GapSignalSide side)
        => side == GapSignalSide.Buy ? "BUY" : "SELL";

    private static void AppendIdentity(StringBuilder sb, Trace trace)
    {
        var s = trace.Signal;
        sb.Append("stt=").Append(Value(trace.Stt))
          .Append(" pair_id=").Append(Text(trace.PairId))
          .Append(" signal_id=").Append(Text(s.SignalId))
          .Append(" cycle_id=").Append(Text(s.CycleId))
          .Append(" slot_id=").Append(Value(trace.SlotIdOverride ?? s.SlotId))
          .Append(" action=").Append(ActionText(s.Action))
          .Append(" side=").Append(SideText(s.Side))
          .Append(' ');
    }

    private static void AppendGaps(StringBuilder sb, IReadOnlyList<int> gaps)
        => AppendGaps(sb, gaps, gaps.Count);

    private static void AppendGaps(StringBuilder sb, IReadOnlyList<int> gaps, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append('|');
            }

            sb.Append(gaps[i].ToString(CultureInfo.InvariantCulture));
        }
    }

    private static int? ElapsedMs(Trace trace)
        => trace.FirstTickUtc is null || trace.LastTickUtc is null
            ? null
            : (int)Math.Max(0, (trace.LastTickUtc.Value - trace.FirstTickUtc.Value).TotalMilliseconds);

    private static string Value(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string Price(decimal? value) => value?.ToString("0.#####", CultureInfo.InvariantCulture) ?? "-";

    private static string Timestamp(DateTime? value) => value?.ToString("O", CultureInfo.InvariantCulture) ?? "-";

    private static string Escape(string value) => value.Replace("\"", "'", StringComparison.Ordinal);

    private sealed class Trace
    {
        public Trace(SignalOutcomeSignal signal, bool trackBuy, int baselineGap, int futureTickCount)
        {
            Signal = signal;
            TrackBuy = trackBuy;
            BaselineGap = baselineGap;
            Gaps = new int[futureTickCount];
            OpenedAtTickMs = Environment.TickCount64;
        }

        public SignalOutcomeSignal Signal { get; }
        public bool TrackBuy { get; }
        public int BaselineGap { get; }
        public int[] Gaps { get; }
        public long OpenedAtTickMs { get; }

        public int? Stt { get; set; }
        public string? PairId { get; set; }
        public int? SlotIdOverride { get; set; }

        /// <summary>null = signal đã vào lệnh (EXEC). Non-null = bị chặn, giá trị là lý do.</summary>
        public string? BlockReason { get; set; }
        public bool SignalLineWritten { get; set; }
        public int Count { get; set; }
        public int SkippedNullTicks { get; set; }
        public int TicksSinceOpen { get; set; }
        public DateTime? FirstTickUtc { get; set; }
        public DateTime? LastTickUtc { get; set; }
    }

    /// <summary>
    /// Một đợt signal bị chặn liên tiếp cùng (action, side, lý do). Signal đầu đợt được ghi đầy đủ
    /// 2 dòng; các signal sau chỉ tăng <see cref="Count"/> và được tổng kết bằng một dòng
    /// <c>[BLOCK_STREAK]</c> khi đợt kết thúc.
    /// </summary>
    private sealed class BlockStreak
    {
        public BlockStreak(string firstSignalId, DateTime firstAtUtc)
        {
            FirstSignalId = firstSignalId;
            LastSignalId = firstSignalId;
            FirstAtUtc = firstAtUtc;
            LastAtUtc = firstAtUtc;
            Count = 1;
        }

        public string FirstSignalId { get; }
        public DateTime FirstAtUtc { get; }

        public string LastSignalId { get; set; }
        public DateTime LastAtUtc { get; set; }
        public int Count { get; set; }
    }
}
