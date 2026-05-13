using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.Application.Services;

public sealed class MachineIdentityService : IMachineIdentityService
{
    public string GetRawHostName() => Environment.MachineName;

    public string GetHostName()
    {
        return Environment.MachineName.Trim().ToLower();
    }
}
