using TradeDesktop.Domain.Models;

namespace TradeDesktop.Application.Abstractions;

public interface IRuntimeConfigStateUpdater
{
    void UpdateDashboardMetrics(DashboardMetrics snapshot);
}
