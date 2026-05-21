namespace TradeDesktop.Application.Abstractions;

/// <summary>
/// Factory để tạo CloseSignalEngine instance riêng cho mỗi PositionSlot.
/// Mỗi slot cần engine riêng vì window state (BuyGaps, SellGaps, window start time)
/// không được share để Rule D (priority close) hoạt động đúng (Phase 2 §2.2.D).
/// </summary>
public interface ICloseSignalEngineFactory
{
    ICloseSignalEngine Create();
}
