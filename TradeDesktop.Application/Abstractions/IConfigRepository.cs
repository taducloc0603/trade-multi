using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Abstractions;

public interface IConfigRepository
{
    Task<ConfigRecord?> GetByHostNameAsync(string hostName, CancellationToken cancellationToken = default);
    Task<bool> UpdateSansAndHostNameByHostNameAsync(
        string hostName,
        string sansJson,
        string platformA,
        string platformB,
        CancellationToken cancellationToken = default);
    Task<bool> UpdateCurrentTicksAsync(
        string hostName,
        string currentTickA,
        string currentTickB,
        CancellationToken cancellationToken = default);
}
