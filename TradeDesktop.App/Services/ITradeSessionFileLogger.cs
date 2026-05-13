namespace TradeDesktop.App.Services;

public enum TradeLogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

public interface ITradeSessionFileLogger
{
    bool IsSessionActive { get; }
    string? CurrentLogFilePath { get; }

    void StartSession(DateTimeOffset startedAtLocal, string hostName);
    void Log(TradeLogLevel level, string message);
    void Log(string message);
    void StopSession(DateTimeOffset stoppedAtLocal);
}
