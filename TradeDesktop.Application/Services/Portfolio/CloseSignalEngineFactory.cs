using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.Application.Services.Portfolio;

public sealed class CloseSignalEngineFactory : ICloseSignalEngineFactory
{
    public ICloseSignalEngine Create() => new CloseSignalEngine();
}
