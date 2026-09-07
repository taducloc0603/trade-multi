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

    /// <summary>
    /// Co ghi cac dong tien do lap moi tick (<c>[*_CYCLE][PROGRESS]</c>, <c>[TP_CYCLE][PROGRESS]</c>)
    /// hay khong. Caller dung de thoat TRUOC khi noi suy chuoi, thay vi build chuoi roi de tang
    /// duoi loc bo - chi phi do nhan theo so slot va tra tren UI thread.
    ///
    /// KHAC VOI <see cref="LogVerbose"/>: LogVerbose thuan la DINH TUYEN (ghi file, khong publish
    /// realtime) va KHONG bi property nay chan. Dong RESET dung LogVerbose nhung van luon duoc ghi.
    ///
    /// Mac dinh <c>true</c> nen cac implementation cu (test fake) giu nguyen hanh vi.
    /// Implementation BAT BUOC khong duoc lay khoa: property nay nam tren duong chay moi tick.
    /// </summary>
    bool IsCycleProgressEnabled => true;
}

/// <summary>
/// Kênh diagnostics Gap Stability chi tiết. Implementation có thể ghi ra file riêng
/// mà không publish realtime lên UI. Tách interface để không ảnh hưởng các logger cũ.
/// </summary>
public interface IGapStabilityRawLogger
{
    void LogGapStabilityRaw(string message);
}
