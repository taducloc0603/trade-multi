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
    void StopSession(DateTimeOffset stoppedAtLocal);
}
