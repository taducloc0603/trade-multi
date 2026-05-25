using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace TradeDesktop.App.Services;

public sealed class TradeSessionFileLogger : ITradeSessionFileLogger
{
    private static readonly TimeSpan DrainShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private StreamWriter? _writer;
    private DateTimeOffset? _sessionStartedAt;
    private string? _sessionHostName;
    private string? _sessionFileBasePath;
    private long _currentFileBytes;
    private long _maxFileSizeBytes = 50L * 1024 * 1024;
    private int _rotationIndex;
    private TradeLogLevel _minLevel = TradeLogLevel.Info;
    private BlockingCollection<string>? _writeQueue;
    private Task? _drainTask;

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

        BlockingCollection<string>? queueToStart = null;

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
                _currentFileBytes = 0;
                _sessionHostName = hostName;

                var fileName = $"{startedAtLocal:yyyyMMdd_HHmmss}-trade-log.log";
                var filePath = Path.Combine(logDirectory, fileName);
                _sessionFileBasePath = Path.Combine(logDirectory, $"{startedAtLocal:yyyyMMdd_HHmmss}-trade-log");

                var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };

                _sessionStartedAt = startedAtLocal;
                CurrentLogFilePath = filePath;

                WriteLineCore($"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] ===== TRADE SESSION START =====");
                WriteLineCore($"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] Host: {hostName}");
                WriteLineCore($"[{startedAtLocal:yyyy-MM-dd HH:mm:ss.fff}] File: {filePath}");

                _writeQueue = new BlockingCollection<string>();
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
            queue.Add(line);
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

    public void Log(string message)
    {
        var queue = _writeQueue;
        if (queue is null || queue.IsAddingCompleted)
        {
            return;
        }

        if (InferLevelFromMessage(message) < _minLevel)
        {
            return;
        }

        try
        {
            var timestamp = DateTimeOffset.Now;
            var line = $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            queue.Add(line);
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
            SafeDebug($"Log enqueue failed: {ex}");
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

    private void DrainQueue(BlockingCollection<string> queue)
    {
        try
        {
            foreach (var line in queue.GetConsumingEnumerable())
            {
                try
                {
                    lock (_sync)
                    {
                        WriteLineCore(line);
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
    }

    private void DrainAndCloseQueue()
    {
        BlockingCollection<string>? queue;
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
        }
        finally
        {
            _writer = null;
            _sessionStartedAt = null;
            _sessionHostName = null;
            _sessionFileBasePath = null;
            _currentFileBytes = 0;
            _rotationIndex = 0;
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
