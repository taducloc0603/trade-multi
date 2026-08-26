using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using TradeDesktop.Application.Models;

namespace TradeDesktop.App.Services;

public sealed class TradeSessionFileLogger : ITradeSessionFileLogger
{
    private sealed record QueuedLog(string Line, bool IsGapStabilityRaw);

    private static readonly TimeSpan DrainShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HealthLogInterval = TimeSpan.FromSeconds(60);
    private const int DefaultQueueCapacity = 50_000;

    private readonly object _sync = new();
    private StreamWriter? _writer;
    private StreamWriter? _gapStabilityRawWriter;
    private DateTimeOffset? _sessionStartedAt;
    private string? _sessionHostName;
    private string? _sessionFileBasePath;
    private string? _gapStabilityRawFileBasePath;
    private long _currentFileBytes;
    private long _currentGapStabilityRawFileBytes;
    private long _maxFileSizeBytes = 50L * 1024 * 1024;
    private int _rotationIndex;
    private int _gapStabilityRawRotationIndex;
    private TradeLogLevel _minLevel = TradeLogLevel.Info;
    private BlockingCollection<QueuedLog>? _writeQueue;
    private Task? _drainTask;
    private long _droppedLogCount;
    private long _totalDroppedLogCount;
    private long _enqueuedLogCount;
    private long _writtenMainLogCount;
    private long _writtenGapRawLogCount;
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
                _currentFileBytes = 0;
                _currentGapStabilityRawFileBytes = 0;
                Interlocked.Exchange(ref _droppedLogCount, 0);
                Interlocked.Exchange(ref _totalDroppedLogCount, 0);
                Interlocked.Exchange(ref _enqueuedLogCount, 0);
                Interlocked.Exchange(ref _writtenMainLogCount, 0);
                Interlocked.Exchange(ref _writtenGapRawLogCount, 0);
                Interlocked.Exchange(ref _queueHighWaterMark, 0);
                _nextHealthLogAt = startedAtLocal.Add(HealthLogInterval);
                _sessionHostName = hostName;

                var fileName = $"{startedAtLocal:yyyyMMdd_HHmmss}-trade-log.log";
                var filePath = Path.Combine(logDirectory, fileName);
                _sessionFileBasePath = Path.Combine(logDirectory, $"{startedAtLocal:yyyyMMdd_HHmmss}-trade-log");
                _gapStabilityRawFileBasePath = Path.Combine(
                    logDirectory,
                    $"{startedAtLocal:yyyyMMdd_HHmmss}-gap-stability-raw");

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

                _sessionStartedAt = startedAtLocal;
                CurrentLogFilePath = filePath;

                WriteLineCore($"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] ===== TRADE SESSION START =====");
                WriteLineCore($"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] Host: {hostName}");
                WriteLineCore($"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] File: {filePath}");
                WriteGapStabilityRawLineCore(
                    $"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] ===== GAP STABILITY RAW START =====");
                WriteGapStabilityRawLineCore(
                    $"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] Host: {hostName}");

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
            isGapStabilityRaw: true);

    private void LogCore(
        TradeLogLevel level,
        string message,
        bool publishRealtime,
        bool isGapStabilityRaw = false)
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
            if (TryEnqueue(queue, new QueuedLog(line, isGapStabilityRaw), level) && publishRealtime)
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
                        else if (queuedLog.IsGapStabilityRaw)
                        {
                            WriteGapStabilityRawLineCore(queuedLog.Line);
                        }
                        else
                        {
                            WriteLineCore(queuedLog.Line);
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

    private static bool IsMergeableCloseGapRaw(QueuedLog queuedLog) =>
        queuedLog.IsGapStabilityRaw
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
                if (queuedLog.IsGapStabilityRaw)
                {
                    WriteGapStabilityRawLineCore(queuedLog.Line);
                }
                else
                {
                    WriteLineCore(queuedLog.Line);
                }
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
            $"dropped_info={Interlocked.Read(ref _totalDroppedLogCount)}");
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
        }
        finally
        {
            _writer = null;
            _gapStabilityRawWriter = null;
            _sessionStartedAt = null;
            _sessionHostName = null;
            _sessionFileBasePath = null;
            _gapStabilityRawFileBasePath = null;
            _currentFileBytes = 0;
            _currentGapStabilityRawFileBytes = 0;
            _rotationIndex = 0;
            _gapStabilityRawRotationIndex = 0;
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
