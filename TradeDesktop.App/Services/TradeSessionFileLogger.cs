using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using TradeDesktop.Application.Models;

namespace TradeDesktop.App.Services;

public sealed class TradeSessionFileLogger : ITradeSessionFileLogger
{
    /// <summary>
    /// Kênh file đích của một dòng log. Dùng enum thay vì cờ bool để mọi điểm dispatch
    /// là một switch một chiều — thêm kênh mới thì compiler bắt được chỗ còn sót.
    /// </summary>
    private enum LogChannel
    {
        Main = 0,
        GapStabilityRaw = 1,
        SignalOutcome = 2,
        GapTick = 3
    }

    private sealed record QueuedLog(string Line, LogChannel Channel);

    private static readonly TimeSpan DrainShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HealthLogInterval = TimeSpan.FromSeconds(60);
    private const int DefaultQueueCapacity = 50_000;

    private readonly object _sync = new();
    private StreamWriter? _writer;
    private StreamWriter? _gapStabilityRawWriter;
    private StreamWriter? _signalOutcomeWriter;
    private StreamWriter? _gapTickWriter;
    private DateTimeOffset? _sessionStartedAt;
    private string? _sessionHostName;
    private string? _sessionFileBasePath;
    private string? _gapStabilityRawFileBasePath;
    private string? _signalOutcomeFileBasePath;
    private string? _gapTickFileBasePath;
    private long _currentFileBytes;
    private long _currentGapStabilityRawFileBytes;
    private long _currentSignalOutcomeFileBytes;
    private long _currentGapTickFileBytes;
    private long _maxFileSizeBytes = 50L * 1024 * 1024;
    private int _rotationIndex;
    private int _gapStabilityRawRotationIndex;
    private int _signalOutcomeRotationIndex;
    private int _gapTickRotationIndex;
    private TradeLogLevel _minLevel = TradeLogLevel.Info;
    private BlockingCollection<QueuedLog>? _writeQueue;
    private Task? _drainTask;
    private long _droppedLogCount;
    private long _totalDroppedLogCount;
    private long _enqueuedLogCount;
    private long _writtenMainLogCount;
    private long _writtenGapRawLogCount;
    private long _writtenSignalOutcomeLogCount;
    private long _writtenGapTickLogCount;
    private long _droppedGapTickLogCount;
    private long _queueHighWaterMark;
    private DateTimeOffset _nextHealthLogAt;

    public event Action<SystemLogItem>? RealtimeLogAccepted;

    public bool IsSessionActive
    {
        get
        {
            lock (_sync)
            {
                return _writer is not null;
            }
        }
    }

    public string? CurrentLogFilePath { get; private set; }

    public void StartSession(DateTimeOffset startedAtLocal, string hostName)
    {
        // Drain any leftover queue from a previous session before re-initializing
        // so messages are not lost across restarts.
        DrainAndCloseQueue();

        BlockingCollection<QueuedLog>? queueToStart = null;

        lock (_sync)
        {
            try
            {
                if (_writer is not null)
                {
                    StopSessionInternal(startedAtLocal, writeFooter: true);
                }

                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrWhiteSpace(desktopPath))
                {
                    return;
                }

                var logDirectory = Path.Combine(desktopPath, "trade-log");
                Directory.CreateDirectory(logDirectory);

                _minLevel = ResolveMinLevelFromEnvironment();
                _maxFileSizeBytes = ResolveMaxFileSizeFromEnvironment();
                _rotationIndex = 0;
                _gapStabilityRawRotationIndex = 0;
                _signalOutcomeRotationIndex = 0;
                _gapTickRotationIndex = 0;
                _currentFileBytes = 0;
                _currentGapStabilityRawFileBytes = 0;
                _currentSignalOutcomeFileBytes = 0;
                _currentGapTickFileBytes = 0;
                Interlocked.Exchange(ref _droppedLogCount, 0);
                Interlocked.Exchange(ref _totalDroppedLogCount, 0);
                Interlocked.Exchange(ref _enqueuedLogCount, 0);
                Interlocked.Exchange(ref _writtenMainLogCount, 0);
                Interlocked.Exchange(ref _writtenGapRawLogCount, 0);
                Interlocked.Exchange(ref _writtenSignalOutcomeLogCount, 0);
                Interlocked.Exchange(ref _writtenGapTickLogCount, 0);
                Interlocked.Exchange(ref _droppedGapTickLogCount, 0);
                Interlocked.Exchange(ref _queueHighWaterMark, 0);
                _nextHealthLogAt = startedAtLocal.Add(HealthLogInterval);
                _sessionHostName = hostName;

                var fileName = $"{startedAtLocal:yyyyMMdd_HHmmss}-trade-log.log";
                var filePath = Path.Combine(logDirectory, fileName);
                _sessionFileBasePath = Path.Combine(logDirectory, $"{startedAtLocal:yyyyMMdd_HHmmss}-trade-log");
                _gapStabilityRawFileBasePath = Path.Combine(
                    logDirectory,
                    $"{startedAtLocal:yyyyMMdd_HHmmss}-gap-stability-raw");
                _signalOutcomeFileBasePath = Path.Combine(
                    logDirectory,
                    $"{startedAtLocal:yyyyMMdd_HHmmss}-signal-outcome");
                _gapTickFileBasePath = Path.Combine(
                    logDirectory,
                    $"{startedAtLocal:yyyyMMdd_HHmmss}-gap-tick");

                var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };

                var gapRawPath = $"{_gapStabilityRawFileBasePath}.log";
                var gapRawStream = new FileStream(
                    gapRawPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                _gapStabilityRawWriter = new StreamWriter(
                    gapRawStream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };

                var signalOutcomePath = $"{_signalOutcomeFileBasePath}.log";
                var signalOutcomeStream = new FileStream(
                    signalOutcomePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                _signalOutcomeWriter = new StreamWriter(
                    signalOutcomeStream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };

                // Kênh gap tick là diagnostics phụ. Mở file trong try/catch RIÊNG để một lỗi
                // ở đây (đĩa đầy, file bị khoá) không rơi vào catch lớn của StartSession —
                // rơi vào đó thì _writeQueue không được tạo và CẢ PHIÊN mất log.
                string? gapTickOpenError = null;
                try
                {
                    var gapTickPath = $"{_gapTickFileBasePath}.log";
                    var gapTickStream = new FileStream(
                        gapTickPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.ReadWrite);
                    _gapTickWriter = new StreamWriter(
                        gapTickStream,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                    {
                        AutoFlush = true
                    };
                }
                catch (Exception ex)
                {
                    _gapTickWriter = null;
                    gapTickOpenError = ex.Message;
                    SafeDebug($"StartSession gap tick file open failed: {ex}");
                }

                _sessionStartedAt = startedAtLocal;
                CurrentLogFilePath = filePath;

                WriteLineCore($"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] ===== TRADE SESSION START =====");
                WriteLineCore($"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] Host: {hostName}");
                WriteLineCore($"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] File: {filePath}");
                WriteGapStabilityRawLineCore(
                    $"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] ===== GAP STABILITY RAW START =====");
                WriteGapStabilityRawLineCore(
                    $"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] Host: {hostName}");
                WriteSignalOutcomeLineCore(
                    $"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] ===== SIGNAL OUTCOME START =====");
                WriteSignalOutcomeLineCore(
                    $"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] Host: {hostName}");
                WriteSignalOutcomeLineCore(
                    $"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] [SIGNAL_OUTCOME][LEGEND] " +
                    "stt=so thu tu lenh tren UI (grid Trade/History, nut Close) " +
                    "| signal_gaps=day gap cua chu ky da xac nhan signal " +
                    "| future_gaps=gap cua cac tick ngay sau signal " +
                    "| gap_at_signal=gap tai thoi diem signal (moc so sanh) " +
                    "| gaps_unit=point gaps_order=oldest_to_newest " +
                    "| status=COMPLETED|SESSION_STOP|STALE|EVICTED " +
                    "| pham vi=signal OPEN va NORMAL CLOSE da dispatch");
                WriteGapTickLineCore(
                    $"[{startedAtLocal:HH:mm:ss.fff}] ===== GAP TICK START =====");
                // Các dòng dữ liệu chỉ có giờ, nên ngày của phiên phải nằm ở header.
                WriteGapTickLineCore(
                    $"[{startedAtLocal:HH:mm:ss.fff}] Date: {startedAtLocal:yyyy-MM-dd}");
                WriteGapTickLineCore(
                    $"[{startedAtLocal:HH:mm:ss.fff}] Host: {hostName}");
                WriteGapTickLineCore(
                    $"[{startedAtLocal:HH:mm:ss.fff}] [GAP_TICK][LEGEND] " +
                    "moi dong = mot tick snapshot tu shared memory " +
                    "| timestamp=gio local cua tick (HH:mm:ss.fff) " +
                    "| gap_buy=(B.Bid-A.Ask) gap_sell=(B.Ask-A.Bid) gaps_unit=point " +
                    "| a_*=san A b_*=san B (bid/ask=gia tho, spread=point, lat=latency ms) " +
                    "| point=he so nhan point dang dung " +
                    "| '-' = gia tri khong san sang tai tick do");
                if (gapTickOpenError is not null)
                {
                    // Báo vào main log để không im lặng mất file gap; phiên vẫn chạy đủ 3 kênh cũ.
                    WriteLineCore(
                        $"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] [LOGGER][WARN] " +
                        $"Gap tick file disabled for this session: {gapTickOpenError}");
                }

                _writeQueue = new BlockingCollection<QueuedLog>(ResolveQueueCapacityFromEnvironment());
                queueToStart = _writeQueue;
            }
            catch (Exception ex)
            {
                SafeDebug($"StartSession failed: {ex}");
            }
        }

        if (queueToStart is not null)
        {
            _drainTask = Task.Factory.StartNew(
                () => DrainQueue(queueToStart),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    public void Log(TradeLogLevel level, string message)
        => LogCore(level, message, publishRealtime: true);

    public void Log(string message)
        => LogCore(InferLevelFromMessage(message), message, publishRealtime: true);

    public void LogFileOnly(string message)
        => LogCore(InferLevelFromMessage(message), message, publishRealtime: false);

    public void LogGapStabilityRaw(string message)
        => LogCore(
            InferLevelFromMessage(message),
            message,
            publishRealtime: false,
            channel: LogChannel.GapStabilityRaw);

    public void LogSignalOutcomeRaw(string message)
        => LogCore(
            InferLevelFromMessage(message),
            message,
            publishRealtime: false,
            channel: LogChannel.SignalOutcome);

    /// <summary>
    /// Ghi nguyên văn một dòng gap theo tick. Không đi qua <see cref="LogCore"/> vì:
    /// (1) timestamp phải là thời điểm của tick do caller cung cấp, không phải thời điểm
    /// enqueue; (2) không được lọc theo <c>_minLevel</c> để <c>LOG_LEVEL=Warn</c> không
    /// làm tắt file gap. Dùng <c>TryAdd</c> non-blocking để không bao giờ chặn UI thread.
    /// </summary>
    public void LogGapTickRaw(string message)
    {
        var queue = _writeQueue;
        if (queue is null || queue.IsAddingCompleted)
        {
            return;
        }

        try
        {
            if (queue.TryAdd(new QueuedLog(message, LogChannel.GapTick)))
            {
                Interlocked.Increment(ref _enqueuedLogCount);
                UpdateQueueHighWaterMark(queue.Count);
            }
            else
            {
                // Bộ đếm RIÊNG: _droppedLogCount sinh ra dòng [LOGGER][WARN] trong main log và
                // publish realtime lên panel Signal. Gap tick chảy ~20 dòng/giây nên nếu tính
                // chung sẽ spam đúng chỗ operator theo dõi lỗi giao dịch. Drop ở đây chỉ báo
                // trong [LOGGER][HEALTH].
                Interlocked.Increment(ref _droppedGapTickLogCount);
            }
        }
        catch (ObjectDisposedException)
        {
            // Queue was disposed concurrently with stop; drop silently.
        }
        catch (InvalidOperationException)
        {
            // Queue was completed between the IsAddingCompleted check and TryAdd; drop silently.
        }
        catch (Exception ex)
        {
            SafeDebug($"LogGapTickRaw enqueue failed: {ex}");
        }
    }

    private void LogCore(
        TradeLogLevel level,
        string message,
        bool publishRealtime,
        LogChannel channel = LogChannel.Main)
    {
        var queue = _writeQueue;
        if (queue is null || queue.IsAddingCompleted)
        {
            return;
        }

        if (level < _minLevel)
        {
            return;
        }

        try
        {
            var timestamp = DateTimeOffset.Now;
            var line = $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            if (TryEnqueue(queue, new QueuedLog(line, channel), level) && publishRealtime)
            {
                PublishRealtimeLog(timestamp.LocalDateTime, message, level);
            }
        }
        catch (ObjectDisposedException)
        {
            // Queue was disposed concurrently with stop; drop silently.
        }
        catch (InvalidOperationException)
        {
            // Queue was completed between the IsAddingCompleted check and Add; drop silently.
        }
        catch (Exception ex)
        {
            SafeDebug($"Log(level) enqueue failed: {ex}");
        }
    }

    public void StopSession(DateTimeOffset stoppedAtLocal)
    {
        // Drain pending log messages first so they land in the file before the session footer.
        DrainAndCloseQueue();

        lock (_sync)
        {
            try
            {
                StopSessionInternal(stoppedAtLocal, writeFooter: true);
            }
            catch (Exception ex)
            {
                SafeDebug($"StopSession failed: {ex}");
            }
        }
    }

    private void DrainQueue(BlockingCollection<QueuedLog> queue)
    {
        QueuedLog? pendingGapRaw = null;
        try
        {
            foreach (var queuedLog in queue.GetConsumingEnumerable())
            {
                try
                {
                    lock (_sync)
                    {
                        WriteDroppedLogSummaryIfNeeded();
                        WriteLoggerHealthIfDue(queue);

                        // Kênh gap tick chảy mỗi ~50ms. Ghi thẳng và KHÔNG chạm vào
                        // pendingGapRaw: nếu nó flush bản ghi đang chờ thì cửa sổ gộp
                        // gap-stability hiện có sẽ bị thu hẹp — đổi behavior sẵn có.
                        if (queuedLog.Channel == LogChannel.GapTick)
                        {
                            WriteGapTickLineCore(queuedLog.Line);
                            continue;
                        }

                        if (pendingGapRaw is not null)
                        {
                            if (TryMergeCloseGapRaw(pendingGapRaw, queuedLog, out var merged))
                            {
                                pendingGapRaw = merged;
                                continue;
                            }

                            WriteGapStabilityRawLineCore(pendingGapRaw.Line);
                            pendingGapRaw = null;
                        }

                        if (IsMergeableCloseGapRaw(queuedLog))
                        {
                            // Các Close engine theo slot thường phát cùng một diagnostics
                            // liên tiếp trên một market snapshot. Giữ tối đa một record chờ
                            // để gộp cycle_id/slot_ids trước khi ghi file.
                            pendingGapRaw = queuedLog;
                        }
                        else
                        {
                            WriteToChannel(queuedLog);
                        }
                    }
                }
                catch (Exception ex)
                {
                    SafeDebug($"DrainQueue write failed: {ex}");
                }
            }
        }
        catch (Exception ex)
        {
            SafeDebug($"DrainQueue failed: {ex}");
        }
        finally
        {
            if (pendingGapRaw is not null)
            {
                lock (_sync)
                {
                    WriteGapStabilityRawLineCore(pendingGapRaw.Line);
                }
            }
        }
    }

    private void WriteToChannel(QueuedLog queuedLog)
    {
        switch (queuedLog.Channel)
        {
            case LogChannel.GapStabilityRaw:
                WriteGapStabilityRawLineCore(queuedLog.Line);
                break;
            case LogChannel.SignalOutcome:
                WriteSignalOutcomeLineCore(queuedLog.Line);
                break;
            case LogChannel.GapTick:
                WriteGapTickLineCore(queuedLog.Line);
                break;
            default:
                WriteLineCore(queuedLog.Line);
                break;
        }
    }

    private static bool IsMergeableCloseGapRaw(QueuedLog queuedLog) =>
        queuedLog.Channel == LogChannel.GapStabilityRaw
        && queuedLog.Line.Contains(
            "[GAP_STABILITY_RAW][CYCLE_COMPLETED]",
            StringComparison.Ordinal)
        && queuedLog.Line.Contains(" action=CLOSE ", StringComparison.Ordinal);

    private static bool TryMergeCloseGapRaw(
        QueuedLog pending,
        QueuedLog current,
        out QueuedLog merged)
    {
        merged = pending;
        if (!IsMergeableCloseGapRaw(pending) || !IsMergeableCloseGapRaw(current))
        {
            return false;
        }

        var pendingSignature = NormalizeGapRawMergeFields(pending.Line);
        var currentSignature = NormalizeGapRawMergeFields(current.Line);
        if (!string.Equals(pendingSignature, currentSignature, StringComparison.Ordinal))
        {
            return false;
        }

        var cycleIds = JoinDistinctFieldValues(
            ReadField(pending.Line, "cycle_id", quoted: false),
            ReadField(current.Line, "cycle_id", quoted: false));
        var slotIds = JoinDistinctFieldValues(
            ReadField(pending.Line, "slot_ids", quoted: true),
            ReadField(current.Line, "slot_ids", quoted: true));
        var line = ReplaceField(pending.Line, "cycle_id", cycleIds, quoted: false);
        line = ReplaceField(line, "slot_ids", slotIds, quoted: true);
        merged = pending with { Line = line };
        return true;
    }

    private static string NormalizeGapRawMergeFields(string line)
    {
        var eventStart = line.IndexOf(
            "[GAP_STABILITY_RAW][CYCLE_COMPLETED]",
            StringComparison.Ordinal);
        var normalized = eventStart >= 0 ? line[eventStart..] : line;
        normalized = ReplaceField(normalized, "cycle_id", "*", quoted: false);
        return ReplaceField(normalized, "slot_ids", "*", quoted: true);
    }

    private static string JoinDistinctFieldValues(string first, string second)
    {
        var values = first.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Concat(second.Split('|', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal);
        return string.Join('|', values);
    }

    private static string ReadField(string line, string key, bool quoted)
    {
        var marker = quoted ? $"{key}=\"" : $"{key}=";
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += marker.Length;
        var end = quoted
            ? line.IndexOf('"', start)
            : line.IndexOf(' ', start);
        if (end < 0)
        {
            end = line.Length;
        }

        return line[start..end];
    }

    private static string ReplaceField(string line, string key, string value, bool quoted)
    {
        var marker = quoted ? $"{key}=\"" : $"{key}=";
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return line;
        }

        var valueStart = start + marker.Length;
        var valueEnd = quoted
            ? line.IndexOf('"', valueStart)
            : line.IndexOf(' ', valueStart);
        if (valueEnd < 0)
        {
            valueEnd = line.Length;
        }

        return line[..valueStart] + value + line[valueEnd..];
    }

    private bool TryEnqueue(
        BlockingCollection<QueuedLog> queue,
        QueuedLog queuedLog,
        TradeLogLevel level)
    {
        if (queue.TryAdd(queuedLog))
        {
            Interlocked.Increment(ref _enqueuedLogCount);
            UpdateQueueHighWaterMark(queue.Count);
            return true;
        }

        // Preserve operationally important records even during a log storm. This may
        // briefly block only WARN/ERROR producers; INFO/DEBUG are dropped and summarized.
        if (level >= TradeLogLevel.Warn)
        {
            lock (_sync)
            {
                WriteDroppedLogSummaryIfNeeded();
                WriteToChannel(queuedLog);
            }
            return true;
        }

        Interlocked.Increment(ref _droppedLogCount);
        Interlocked.Increment(ref _totalDroppedLogCount);
        return false;
    }

    private void UpdateQueueHighWaterMark(int queueCount)
    {
        var current = Interlocked.Read(ref _queueHighWaterMark);
        while (queueCount > current)
        {
            var observed = Interlocked.CompareExchange(ref _queueHighWaterMark, queueCount, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private void WriteLoggerHealthIfDue(BlockingCollection<QueuedLog> queue)
    {
        var now = DateTimeOffset.Now;
        if (now < _nextHealthLogAt)
        {
            return;
        }

        _nextHealthLogAt = now.Add(HealthLogInterval);
        WriteLineCore(
            $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [LOGGER][HEALTH] " +
            $"queue_count={queue.Count} queue_capacity={queue.BoundedCapacity} " +
            $"queue_high_water={Interlocked.Read(ref _queueHighWaterMark)} " +
            $"enqueued={Interlocked.Read(ref _enqueuedLogCount)} " +
            $"written_main={Interlocked.Read(ref _writtenMainLogCount)} " +
            $"written_gap_raw={Interlocked.Read(ref _writtenGapRawLogCount)} " +
            $"written_signal_outcome={Interlocked.Read(ref _writtenSignalOutcomeLogCount)} " +
            $"written_gap_tick={Interlocked.Read(ref _writtenGapTickLogCount)} " +
            $"dropped_info={Interlocked.Read(ref _totalDroppedLogCount)} " +
            $"dropped_gap_tick={Interlocked.Read(ref _droppedGapTickLogCount)}");
    }

    private void PublishRealtimeLog(DateTime timestamp, string message, TradeLogLevel level)
    {
        try
        {
            var severity = level switch
            {
                TradeLogLevel.Debug => SystemLogSeverity.Debug,
                TradeLogLevel.Warn => SystemLogSeverity.Warn,
                TradeLogLevel.Error => SystemLogSeverity.Error,
                _ => SystemLogSeverity.Info
            };
            RealtimeLogAccepted?.Invoke(SystemLogItem.Parse(timestamp, message, severity));
        }
        catch (Exception ex)
        {
            SafeDebug($"Realtime log subscriber failed: {ex}");
        }
    }

    private void WriteDroppedLogSummaryIfNeeded()
    {
        var dropped = Interlocked.Exchange(ref _droppedLogCount, 0);
        if (dropped <= 0)
        {
            return;
        }

        var timestamp = DateTimeOffset.Now;
        var message = $"[LOGGER][WARN] Dropped {dropped} log lines because the bounded queue was full.";
        WriteLineCore($"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] {message}");
        PublishRealtimeLog(timestamp.LocalDateTime, message, TradeLogLevel.Warn);
    }

    private void DrainAndCloseQueue()
    {
        BlockingCollection<QueuedLog>? queue;
        Task? task;

        lock (_sync)
        {
            queue = _writeQueue;
            task = _drainTask;
            _writeQueue = null;
            _drainTask = null;
        }

        if (queue is null)
        {
            return;
        }

        try
        {
            queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }
        catch (Exception ex)
        {
            SafeDebug($"DrainAndCloseQueue complete-adding failed: {ex}");
        }

        if (task is not null)
        {
            try
            {
                task.Wait(DrainShutdownTimeout);
            }
            catch (Exception ex)
            {
                SafeDebug($"DrainAndCloseQueue wait failed: {ex}");
            }
        }

        try
        {
            queue.Dispose();
        }
        catch
        {
            // ignored
        }
    }

    private void StopSessionInternal(DateTimeOffset stoppedAtLocal, bool writeFooter)
    {
        try
        {
            if (_writer is null)
            {
                return;
            }

            if (writeFooter)
            {
                WriteDroppedLogSummaryIfNeeded();
                if (_sessionStartedAt.HasValue)
                {
                    var duration = stoppedAtLocal - _sessionStartedAt.Value;
                    WriteLineCore($"[{stoppedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] ===== TRADE SESSION STOP =====");
                    WriteLineCore($"[{stoppedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] Duration: {duration:hh\\:mm\\:ss}");
                }
                else
                {
                    WriteLineCore($"[{stoppedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] ===== TRADE SESSION STOP =====");
                }
            }

            _writer.Flush();
            _writer.Dispose();
            if (_gapStabilityRawWriter is not null)
            {
                WriteGapStabilityRawLineCore(
                    $"[{stoppedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] ===== GAP STABILITY RAW STOP =====");
                _gapStabilityRawWriter.Flush();
                _gapStabilityRawWriter.Dispose();
            }

            if (_signalOutcomeWriter is not null)
            {
                WriteSignalOutcomeLineCore(
                    $"[{stoppedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] ===== SIGNAL OUTCOME STOP =====");
                _signalOutcomeWriter.Flush();
                _signalOutcomeWriter.Dispose();
            }

            if (_gapTickWriter is not null)
            {
                WriteGapTickLineCore(
                    $"[{stoppedAtLocal:HH:mm:ss.fff}] ===== GAP TICK STOP =====");
                _gapTickWriter.Flush();
                _gapTickWriter.Dispose();
            }
        }
        finally
        {
            _writer = null;
            _gapStabilityRawWriter = null;
            _signalOutcomeWriter = null;
            _gapTickWriter = null;
            _sessionStartedAt = null;
            _sessionHostName = null;
            _sessionFileBasePath = null;
            _gapStabilityRawFileBasePath = null;
            _signalOutcomeFileBasePath = null;
            _gapTickFileBasePath = null;
            _currentFileBytes = 0;
            _currentGapStabilityRawFileBytes = 0;
            _currentSignalOutcomeFileBytes = 0;
            _currentGapTickFileBytes = 0;
            _rotationIndex = 0;
            _gapStabilityRawRotationIndex = 0;
            _signalOutcomeRotationIndex = 0;
            _gapTickRotationIndex = 0;
            CurrentLogFilePath = null;
        }
    }

    private void WriteLineCore(string line)
    {
        if (_writer is null)
        {
            return;
        }

        var lineBytes = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
        if (_currentFileBytes + lineBytes > _maxFileSizeBytes && _currentFileBytes > 0)
        {
            RotateFile();
        }

        _writer.WriteLine(line);
        _currentFileBytes += lineBytes;
        Interlocked.Increment(ref _writtenMainLogCount);
    }

    private void WriteGapStabilityRawLineCore(string line)
    {
        if (_gapStabilityRawWriter is null)
        {
            return;
        }

        var lineBytes = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
        if (_currentGapStabilityRawFileBytes + lineBytes > _maxFileSizeBytes
            && _currentGapStabilityRawFileBytes > 0)
        {
            RotateGapStabilityRawFile();
        }

        _gapStabilityRawWriter.WriteLine(line);
        _currentGapStabilityRawFileBytes += lineBytes;
        Interlocked.Increment(ref _writtenGapRawLogCount);
    }

    private void WriteSignalOutcomeLineCore(string line)
    {
        if (_signalOutcomeWriter is null)
        {
            return;
        }

        var lineBytes = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
        if (_currentSignalOutcomeFileBytes + lineBytes > _maxFileSizeBytes
            && _currentSignalOutcomeFileBytes > 0)
        {
            RotateSignalOutcomeFile();
        }

        _signalOutcomeWriter.WriteLine(line);
        _currentSignalOutcomeFileBytes += lineBytes;
        Interlocked.Increment(ref _writtenSignalOutcomeLogCount);
    }

    private void WriteGapTickLineCore(string line)
    {
        if (_gapTickWriter is null)
        {
            return;
        }

        var lineBytes = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
        if (_currentGapTickFileBytes + lineBytes > _maxFileSizeBytes
            && _currentGapTickFileBytes > 0)
        {
            RotateGapTickFile();
        }

        _gapTickWriter.WriteLine(line);
        _currentGapTickFileBytes += lineBytes;
        Interlocked.Increment(ref _writtenGapTickLogCount);
    }

    private void RotateGapTickFile()
    {
        if (_gapTickWriter is null
            || _sessionStartedAt is null
            || string.IsNullOrWhiteSpace(_gapTickFileBasePath))
        {
            return;
        }

        try
        {
            _gapTickRotationIndex++;
            var nextFilePath = $"{_gapTickFileBasePath}.{_gapTickRotationIndex:000}.log";
            var stream = new FileStream(nextFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            var nextWriter = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            _gapTickWriter.Flush();
            _gapTickWriter.Dispose();
            _gapTickWriter = nextWriter;
            _currentGapTickFileBytes = 0;

            WriteGapTickLineCore(
                $"Continuation of Gap Tick session {_sessionStartedAt.Value:yyyy-MM-dd HH:mm:ss} " +
                $"part {_gapTickRotationIndex:000}");
        }
        catch (Exception ex)
        {
            SafeDebug($"RotateGapTickFile failed: {ex}");
        }
    }

    private void RotateSignalOutcomeFile()
    {
        if (_signalOutcomeWriter is null
            || _sessionStartedAt is null
            || string.IsNullOrWhiteSpace(_signalOutcomeFileBasePath))
        {
            return;
        }

        try
        {
            _signalOutcomeRotationIndex++;
            var nextFilePath = $"{_signalOutcomeFileBasePath}.{_signalOutcomeRotationIndex:000}.log";
            var stream = new FileStream(nextFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            var nextWriter = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            _signalOutcomeWriter.Flush();
            _signalOutcomeWriter.Dispose();
            _signalOutcomeWriter = nextWriter;
            _currentSignalOutcomeFileBytes = 0;

            WriteSignalOutcomeLineCore(
                $"Continuation of Signal Outcome session {_sessionStartedAt.Value:yyyy-MM-dd HH:mm:ss} " +
                $"part {_signalOutcomeRotationIndex:000}");
        }
        catch (Exception ex)
        {
            SafeDebug($"RotateSignalOutcomeFile failed: {ex}");
        }
    }

    private void RotateGapStabilityRawFile()
    {
        if (_gapStabilityRawWriter is null
            || _sessionStartedAt is null
            || string.IsNullOrWhiteSpace(_gapStabilityRawFileBasePath))
        {
            return;
        }

        try
        {
            _gapStabilityRawRotationIndex++;
            var nextFilePath = $"{_gapStabilityRawFileBasePath}.{_gapStabilityRawRotationIndex:000}.log";
            var stream = new FileStream(nextFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            var nextWriter = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            _gapStabilityRawWriter.Flush();
            _gapStabilityRawWriter.Dispose();
            _gapStabilityRawWriter = nextWriter;
            _currentGapStabilityRawFileBytes = 0;

            WriteGapStabilityRawLineCore(
                $"Continuation of Gap Stability raw session {_sessionStartedAt.Value:yyyy-MM-dd HH:mm:ss} " +
                $"part {_gapStabilityRawRotationIndex:000}");
        }
        catch (Exception ex)
        {
            SafeDebug($"RotateGapStabilityRawFile failed: {ex}");
        }
    }

    private void RotateFile()
    {
        if (_writer is null || _sessionStartedAt is null || string.IsNullOrWhiteSpace(_sessionFileBasePath))
        {
            return;
        }

        try
        {
            _rotationIndex++;
            var nextFilePath = $"{_sessionFileBasePath}.{_rotationIndex:000}.log";
            var stream = new FileStream(nextFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            var nextWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            _writer.Flush();
            _writer.Dispose();
            _writer = nextWriter;

            _currentFileBytes = 0;
            CurrentLogFilePath = nextFilePath;

            WriteLineCore("=========================================================");
            WriteLineCore($" Continuation of session {_sessionStartedAt.Value:yyyy-MM-dd HH:mm:ss} part {_rotationIndex:000}");
            WriteLineCore($" From         : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}");
            if (!string.IsNullOrWhiteSpace(_sessionHostName))
            {
                WriteLineCore($" Host         : {_sessionHostName}");
            }

            WriteLineCore($" File         : {nextFilePath}");
            WriteLineCore("=========================================================");
        }
        catch (Exception ex)
        {
            SafeDebug($"RotateFile failed: {ex}");
        }
    }

    private static TradeLogLevel ResolveMinLevelFromEnvironment()
    {
        var levelRaw = Environment.GetEnvironmentVariable("LOG_LEVEL") ?? "INFO";
        return Enum.TryParse<TradeLogLevel>(levelRaw, ignoreCase: true, out var parsed)
            ? parsed
            : TradeLogLevel.Info;
    }

    private static long ResolveMaxFileSizeFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("LOG_MAX_FILE_SIZE_MB") ?? "50";
        if (int.TryParse(raw, out var mb) && mb > 0)
        {
            return (long)mb * 1024 * 1024;
        }

        return 50L * 1024 * 1024;
    }

    private static int ResolveQueueCapacityFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("LOG_QUEUE_CAPACITY");
        return int.TryParse(raw, out var capacity) && capacity > 0
            ? capacity
            : DefaultQueueCapacity;
    }

    private static TradeLogLevel InferLevelFromMessage(string message)
    {
        if (message.Contains("][ERROR]", StringComparison.OrdinalIgnoreCase))
        {
            return TradeLogLevel.Error;
        }

        if (message.Contains("][WARN]", StringComparison.OrdinalIgnoreCase))
        {
            return TradeLogLevel.Warn;
        }

        if (message.Contains("][DEBUG]", StringComparison.OrdinalIgnoreCase))
        {
            return TradeLogLevel.Debug;
        }

        return TradeLogLevel.Info;
    }

    private static void SafeDebug(string message)
    {
        try
        {
            Debug.WriteLine($"[TradeSessionFileLogger] {message}");
        }
        catch
        {
            // ignored
        }
    }
}
