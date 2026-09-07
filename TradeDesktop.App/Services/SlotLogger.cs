using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.App.Services;

/// <summary>
/// Forward Application-layer ISlotLogger calls to App-layer ITradeSessionFileLogger.
/// </summary>
public sealed class SlotLogger : ISlotLogger, IGapStabilityRawLogger, ISignalOutcomeRawLogger
{
    private readonly ITradeSessionFileLogger _sessionLogger;

    public SlotLogger(ITradeSessionFileLogger sessionLogger)
    {
        _sessionLogger = sessionLogger;
    }

    public void Log(string message) => _sessionLogger.Log(message);

    // Ghi file nhưng bỏ qua PublishRealtimeLog: log per-tick không được đi vào
    // đường realtime UI (Parse + Dispatcher) vì chi phí nhân theo số slot đang mở.
    public void LogVerbose(string message) => _sessionLogger.LogFileOnly(message);

    public bool IsCycleProgressEnabled => _sessionLogger.IsCycleProgressEnabled;

    public void LogGapStabilityRaw(string message) =>
        _sessionLogger.LogGapStabilityRaw(message);

    public void LogSignalOutcomeRaw(string message) =>
        _sessionLogger.LogSignalOutcomeRaw(message);
}
