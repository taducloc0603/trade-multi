using Microsoft.Extensions.DependencyInjection;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Services;

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
        services.AddSingleton<ICloseSignalEngine, CloseSignalEngine>();
        services.AddSingleton<ITradeInstructionFactory, TradeInstructionFactory>();
        services.AddSingleton<ITradeSignalLogBuilder, TradeSignalLogBuilder>();
        services.AddSingleton<ITradingFlowEngine, TradingFlowEngine>();
        return services;
    }
}