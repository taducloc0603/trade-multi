namespace TradeDesktop.Application.Abstractions;

/// <summary>
/// Logger abstraction để PortfolioCoordinator (Application layer) ghi log
/// mà không phụ thuộc ITradeSessionFileLogger (App layer). Implementation
/// thực tế ở App project forward tới session logger.
/// </summary>
public interface ISlotLogger
{
    void Log(string message);

    /// <summary>
    /// Kênh cho log lặp lại mỗi tick (ví dụ <c>[*_CYCLE][PROGRESS]</c>). Implementation nên
    /// ghi file nhưng KHÔNG publish realtime lên UI: mỗi dòng realtime phải qua
    /// <c>SystemLogItem.Parse</c> + đường Dispatcher, chi phí đó nhân theo số slot đang mở
    /// và đủ sức làm nghẽn UI thread. Mặc định forward về <see cref="Log"/> để các
    /// implementation cũ (test fake) không phải sửa.
    /// </summary>
    void LogVerbose(string message) => Log(message);
}

/// <summary>
/// Kênh diagnostics Gap Stability chi tiết. Implementation có thể ghi ra file riêng
/// mà không publish realtime lên UI. Tách interface để không ảnh hưởng các logger cũ.
/// </summary>
public interface IGapStabilityRawLogger
{
    void LogGapStabilityRaw(string message);
}
