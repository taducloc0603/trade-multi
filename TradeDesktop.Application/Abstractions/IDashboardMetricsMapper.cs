using TradeDesktop.Application.Models;
using TradeDesktop.Domain.Models;

namespace TradeDesktop.Application.Abstractions;

public interface IDashboardMetricsMapper
{
    DashboardMetrics Map(SharedMemorySnapshot snapshot);
}
