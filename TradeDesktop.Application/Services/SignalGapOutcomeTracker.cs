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
    int SignalCycleSize);

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

    private readonly ISignalOutcomeRawLogger _logger;
    private readonly int _futureTickCount;
    private readonly int _maxConcurrentTraces;
    private readonly int _staleTraceMs;
    private readonly int _attachGraceTicks;
    private readonly List<Trace> _traces = new();

    public SignalGapOutcomeTracker(
        ISignalOutcomeRawLogger logger,
        int futureTickCount = 50,
        int maxConcurrentTraces = 16,
        int staleTraceMs = 30_000,
        // ~30s ở nhịp tick 50ms. Phải rộng hơn NHIỀU so với cửa sổ 50 tick: đường dispatch
        // có thể chờ physical mutex không giới hạn, cộng DelayMs mỗi leg và thời gian click
        // native. Grace quá ngắn sẽ vứt bỏ bản ghi của một lệnh vào thật.
        // Cửa sổ chờ attach thực tế = min(attachGraceTicks nhịp tick, staleTraceMs đồng hồ),
        // tức ~30s ở cả hai đầu với giá trị mặc định.
        int attachGraceTicks = 600)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _futureTickCount = Math.Max(1, futureTickCount);
        _maxConcurrentTraces = Math.Max(1, maxConcurrentTraces);
        _staleTraceMs = Math.Max(0, staleTraceMs);
        _attachGraceTicks = Math.Max(1, attachGraceTicks);
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
    /// Mở trace tại đúng tick signal. Chưa ghi ra file — chờ <see cref="AttachStt"/>.
    /// Bỏ qua nếu gap của chiều đang theo dõi không có giá trị (không có baseline để so).
    /// </summary>
    public void OnSignalPending(SignalOutcomeSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var trackBuy = signal.Side == GapSignalSide.Buy;
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
    /// Feed một tick vào mọi trace đang mở. O(số trace), không allocation.
    /// </summary>
    public void OnTick(in SignalOutcomeTick tick)
    {
        if (_traces.Count == 0)
        {
            return;
        }

        var nowMs = Environment.TickCount64;

        for (var i = _traces.Count - 1; i >= 0; i--)
        {
            var trace = _traces[i];
            trace.TicksSinceOpen++;

            // Chưa attach quá lâu => signal đã bị chặn ở gate phía sau, bỏ im lặng.
            if (!trace.SignalLineWritten && trace.TicksSinceOpen > _attachGraceTicks)
            {
                _traces.RemoveAt(i);
                continue;
            }

            if (_staleTraceMs > 0 && nowMs - trace.OpenedAtTickMs > _staleTraceMs)
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
    }

    /// <summary>Xoá mọi trace đang mở, không ghi gì. Dùng khi bắt đầu session mới.</summary>
    public void Reset() => _traces.Clear();

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
        sb.Append("[SIGNAL_OUTCOME][").Append(SignalEvent).Append("] ");
        AppendIdentity(sb, trace);
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
        sb.Append("[SIGNAL_OUTCOME][").Append(EndEvent).Append("] ");
        AppendIdentity(sb, trace);
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
    private static void AppendIdentity(StringBuilder sb, Trace trace)
    {
        var s = trace.Signal;
        sb.Append("stt=").Append(Value(trace.Stt))
          .Append(" pair_id=").Append(Text(trace.PairId))
          .Append(" signal_id=").Append(Text(s.SignalId))
          .Append(" cycle_id=").Append(Text(s.CycleId))
          .Append(" slot_id=").Append(Value(trace.SlotIdOverride ?? s.SlotId))
          .Append(" action=").Append(s.Action == GapSignalAction.Open ? "OPEN" : "CLOSE")
          .Append(" side=").Append(s.Side == GapSignalSide.Buy ? "BUY" : "SELL")
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
        public bool SignalLineWritten { get; set; }
        public int Count { get; set; }
        public int SkippedNullTicks { get; set; }
        public int TicksSinceOpen { get; set; }
        public DateTime? FirstTickUtc { get; set; }
        public DateTime? LastTickUtc { get; set; }
    }
}
