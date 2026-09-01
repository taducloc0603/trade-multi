namespace TradeDesktop.Application.Abstractions;

/// <summary>
/// Kênh log riêng cho việc đánh giá hậu nghiệm chất lượng signal: mỗi signal ghi
/// gap tại thời điểm trigger kèm dãy gap của N tick kế tiếp. Implementation ghi ra
/// file riêng và KHÔNG publish realtime lên UI. Tách interface để không ảnh hưởng
/// các logger cũ (<see cref="ISlotLogger"/>, <see cref="IGapStabilityRawLogger"/>).
/// </summary>
public interface ISignalOutcomeRawLogger
{
    void LogSignalOutcomeRaw(string message);
}
