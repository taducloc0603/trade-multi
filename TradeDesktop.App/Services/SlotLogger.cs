using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.App.Services;

/// <summary>
/// Forward Application-layer ISlotLogger calls to App-layer ITradeSessionFileLogger.
/// </summary>
public sealed class SlotLogger : ISlotLogger
{
    private readonly ITradeSessionFileLogger _sessionLogger;

    public SlotLogger(ITradeSessionFileLogger sessionLogger)
    {
        _sessionLogger = sessionLogger;
    }

    public void Log(string message) => _sessionLogger.Log(message);
}
