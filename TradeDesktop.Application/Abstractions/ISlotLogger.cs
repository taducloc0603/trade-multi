namespace TradeDesktop.Application.Abstractions;

/// <summary>
/// Logger abstraction để PortfolioCoordinator (Application layer) ghi log
/// mà không phụ thuộc ITradeSessionFileLogger (App layer). Implementation
/// thực tế ở App project forward tới session logger.
/// </summary>
public interface ISlotLogger
{
    void Log(string message);
}

/// <summary>
/// Kênh diagnostics Gap Stability chi tiết. Implementation có thể ghi ra file riêng
/// mà không publish realtime lên UI. Tách interface để không ảnh hưởng các logger cũ.
/// </summary>
public interface IGapStabilityRawLogger
{
    void LogGapStabilityRaw(string message);
}
