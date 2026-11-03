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
    Task<bool> UpdateCurrentSlotsAsync(
        string hostName,
        string currentSlotsJson,
        CancellationToken cancellationToken = default);
    // Ghi RIÊNG cột `fast_leg` (sàn C). null ⇒ cột NULL. Không đụng sans/platform hay cột nào khác.
    Task<bool> UpdateFastLegAsync(
        string hostName,
        string? fastLegJson,
        CancellationToken cancellationToken = default);
}
