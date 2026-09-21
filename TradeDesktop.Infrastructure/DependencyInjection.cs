using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Infrastructure.CTrader;
using TradeDesktop.Infrastructure.MarketData;
using TradeDesktop.Infrastructure.Signals;
using TradeDesktop.Infrastructure.SharedMemory;
using TradeDesktop.Infrastructure.Supabase;

namespace TradeDesktop.Infrastructure;

public static class DependencyInjection
{
    private const string DefaultSupabaseUrl = "https://avtwclunxeivgdwsqjmp.supabase.co";
    private const string DefaultSupabaseAnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6ImF2dHdjbHVueGVpdmdkd3Nxam1wIiwicm9sZSI6ImFub24iLCJpYXQiOjE3Nzg0ODkyMTEsImV4cCI6MjA5NDA2NTIxMX0.gQouNvbtnH299xIzM7GhcmDSYiHPYMC4ALx-jA0DfZQ";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Phase 4: đăng ký không điều kiện; session chỉ mở socket khi platform_b = ctrader (G4). Container dispose
        // lúc thoát app → logout.
        services.AddSingleton<ICTraderQuoteSession>(_ => CTraderQuoteSession.CreateDefault());
        services.AddSingleton<ISharedMemoryReader, SharedMemoryMarketDataReader>();
        services.AddSingleton<IExchangePairReader>(sp => sp.GetRequiredService<ISharedMemoryReader>());
        services.AddSingleton<ICTraderTradeSession>(_ => CTraderTradeSession.CreateDefault());
        // Phase 5 ROLLBACK = thay dòng dưới bằng `services.AddSingleton<ITradesSharedMemoryReader, TradesSharedMemoryReader>();`
        // — quay về MMF và TRADE session không bao giờ được start (vòng đời do decorator điều khiển).
        services.AddSingleton<ITradesSharedMemoryReader>(sp => new CTraderAwareTradesReader(new TradesSharedMemoryReader(), sp.GetRequiredService<IRuntimeConfigProvider>(), sp.GetRequiredService<ICTraderTradeSession>(), sp.GetRequiredService<ICTraderQuoteSession>()));
        // Phase 6 ROLLBACK = thay dòng dưới bằng `services.AddSingleton<IHistorySharedMemoryReader, HistorySharedMemoryReader>();`
        services.AddSingleton<IHistorySharedMemoryReader>(sp => new CTraderAwareHistoryReader(new HistorySharedMemoryReader(), sp.GetRequiredService<IRuntimeConfigProvider>(), sp.GetRequiredService<ICTraderTradeSession>(), sp.GetRequiredService<ICTraderQuoteSession>()));
        services.AddSingleton<MockSharedMemoryMarketDataReader>();
        services.AddSingleton<ISignalEngine, SimpleSignalEngine>();
        services.AddHttpClient();
        services.AddSingleton<IConfigRepository>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient();

            var url =
                configuration["SUPABASE_URL"] ??
                configuration["NEXT_PUBLIC_SUPABASE_URL"] ??
                configuration["Supabase:Url"] ??
                configuration["Supabase__Url"] ??
                DefaultSupabaseUrl;

            var key =
                configuration["SUPABASE_KEY"] ??
                configuration["SUPABASE_ANON_KEY"] ??
                configuration["NEXT_PUBLIC_SUPABASE_ANON_KEY"] ??
                configuration["SUPABASE_SERVICE_ROLE_KEY"] ??
                configuration["Supabase:Key"] ??
                configuration["Supabase__Key"] ??
                DefaultSupabaseAnonKey;

            return new SupabaseConfigRepository(httpClient, url, key);
        });

        return services;
    }
}