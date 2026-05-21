using Microsoft.Extensions.DependencyInjection;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IDashboardService, DashboardService>();
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<IMachineIdentityService, MachineIdentityService>();
        services.AddSingleton<IGapCalculator, GapCalculator>();
        services.AddSingleton<IDashboardMetricsMapper, DashboardMetricsMapper>();
        services.AddSingleton<IGapSignalConfirmationEngine, GapSignalConfirmationEngine>();
        services.AddSingleton<IOpenSignalEngine>(sp => (GapSignalConfirmationEngine)sp.GetRequiredService<IGapSignalConfirmationEngine>());

        // Phase 0: ICloseSignalEngine registration kept for legacy [Obsolete] TradingFlowEngine reference.
        // Phase 0+: each PositionSlot gets its own CloseSignalEngine via ICloseSignalEngineFactory.
        services.AddSingleton<ICloseSignalEngine, CloseSignalEngine>();
        services.AddSingleton<ICloseSignalEngineFactory, CloseSignalEngineFactory>();

        services.AddSingleton<ITradeInstructionFactory, TradeInstructionFactory>();
        services.AddSingleton<ITradeSignalLogBuilder, TradeSignalLogBuilder>();

        // Phase 0: ITradingFlowEngine binding switched from TradingFlowEngine to PortfolioCoordinatorAdapter.
        // Legacy TradingFlowEngine still instantiable for unit tests (TradingFlowEngineTests).
        services.AddSingleton<IPortfolioCoordinator, PortfolioCoordinator>();
        services.AddSingleton<ITradingFlowEngine, PortfolioCoordinatorAdapter>();
        return services;
    }
}