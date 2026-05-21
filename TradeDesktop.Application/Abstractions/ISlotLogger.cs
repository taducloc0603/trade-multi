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
