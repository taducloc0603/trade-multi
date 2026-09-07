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

    /// <summary>
    /// Co ghi cac dong tien do lap moi tick (<c>[*_CYCLE][PROGRESS]</c>) hay khong.
    /// Bat bang bien moi truong <c>LOG_CYCLE_PROGRESS</c>; MAC DINH TAT vi nhom dong nay chiem
    /// ~95% khoi luong ghi dia khi nhieu slot mo.
    ///
    /// BAT BUOC doc KHONG KHOA: property nay duoc goi tren duong chay moi tick. Khac hoan toan
    /// voi <see cref="IsSessionActive"/> - property do lay lock(_sync), ma drain thread giu dung
    /// lock ay quanh moi lan ghi file, nen goi moi tick se keo UI thread vao tranh chap lock.
    /// </summary>
    bool IsCycleProgressEnabled { get; }

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
