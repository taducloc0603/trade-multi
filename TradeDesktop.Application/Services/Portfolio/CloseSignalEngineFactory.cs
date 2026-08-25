using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.Application.Services.Portfolio;

public sealed class CloseSignalEngineFactory : ICloseSignalEngineFactory
{
    private readonly ISlotLogger? _logger;

    public CloseSignalEngineFactory(ISlotLogger? logger = null)
    {
        _logger = logger;
    }

    public ICloseSignalEngine Create() => new CloseSignalEngine(_logger);
}
