namespace TradeDesktop.App.Services;

using TradeDesktop.Application.Models;

public enum TradeLogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

public interface ITradeSessionFileLogger
{
    event Action<SystemLogItem>? RealtimeLogAccepted;

    bool IsSessionActive { get; }
    string? CurrentLogFilePath { get; }

    void StartSession(DateTimeOffset startedAtLocal, string hostName);
    void Log(TradeLogLevel level, string message);
    void Log(string message);
    void LogFileOnly(string message);
    void LogGapStabilityRaw(string message);
    void LogSignalOutcomeRaw(string message);

    /// <summary>
    /// Ghi một dòng gap theo tick vào file <c>*-gap-tick.log</c> của phiên.
    /// Caller cung cấp NGUYÊN VĂN cả dòng (kể cả prefix <c>[HH:mm:ss.fff]</c>) vì timestamp
    /// phải là thời điểm của tick, không phải thời điểm enqueue.
    /// </summary>
    void LogGapTickRaw(string message);
    void StopSession(DateTimeOffset stoppedAtLocal);
}
