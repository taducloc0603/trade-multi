namespace TradeDesktop.Application.Abstractions;

public interface IMachineIdentityService
{
    string GetRawHostName();
    string GetHostName();
}
