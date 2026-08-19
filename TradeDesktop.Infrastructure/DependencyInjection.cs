using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Infrastructure.MarketData;
using TradeDesktop.Infrastructure.Mt5Bridge;
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
        services.AddSingleton<ISharedMemoryReader, SharedMemoryMarketDataReader>();
        services.AddSingleton<IExchangePairReader>(sp => sp.GetRequiredService<ISharedMemoryReader>());
        services.AddSingleton<ITradesSharedMemoryReader, TradesSharedMemoryReader>();
        services.AddSingleton<IHistorySharedMemoryReader, HistorySharedMemoryReader>();
        services.AddSingleton<MockSharedMemoryMarketDataReader>();
        services.AddSingleton<ISignalEngine, SimpleSignalEngine>();
        var bridgeOptions = BuildMt5BridgeOptions(configuration);
        services.AddSingleton(bridgeOptions);
        services.AddSingleton<IMt5BridgeTransportProvider, Mt5BridgeTransportProvider>();
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

    private static Mt5BridgeOptions BuildMt5BridgeOptions(IConfiguration configuration)
    {
        return new Mt5BridgeOptions
        {
            ExchangeA = new Mt5BridgeEndpointOptions
            {
                RoomId = ReadString(configuration, "MT5_BRIDGE_ROOM_ID_A", "Mt5Bridge:A:RoomId", "OCTBridge_A"),
                Account = ReadLong(configuration, "MT5_BRIDGE_ACCOUNT_A", "Mt5Bridge:A:Account"),
                Symbol = ReadString(configuration, "MT5_BRIDGE_SYMBOL_A", "Mt5Bridge:A:Symbol", string.Empty),
                Volume = ReadDouble(configuration, "MT5_BRIDGE_VOLUME_A", "Mt5Bridge:A:Volume")
            },
            ExchangeB = new Mt5BridgeEndpointOptions
            {
                RoomId = ReadString(configuration, "MT5_BRIDGE_ROOM_ID_B", "Mt5Bridge:B:RoomId", "OCTBridge_B"),
                Account = ReadLong(configuration, "MT5_BRIDGE_ACCOUNT_B", "Mt5Bridge:B:Account"),
                Symbol = ReadString(configuration, "MT5_BRIDGE_SYMBOL_B", "Mt5Bridge:B:Symbol", string.Empty),
                Volume = ReadDouble(configuration, "MT5_BRIDGE_VOLUME_B", "Mt5Bridge:B:Volume")
            },
            AckTimeout = TimeSpan.FromMilliseconds(
                Math.Max(100, ReadInt(configuration, "MT5_BRIDGE_ACK_TIMEOUT_MS", "Mt5Bridge:AckTimeoutMs", 1000))),
            ExecutionTimeout = TimeSpan.FromMilliseconds(
                Math.Max(1000, ReadInt(configuration, "MT5_BRIDGE_EXECUTION_TIMEOUT_MS", "Mt5Bridge:ExecutionTimeoutMs", 15000))),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(
                Math.Max(500, ReadInt(configuration, "MT5_BRIDGE_HEARTBEAT_TIMEOUT_MS", "Mt5Bridge:HeartbeatTimeoutMs", 2000)))
        };
    }

    private static string ReadString(IConfiguration configuration, string envKey, string sectionKey, string fallback)
        => configuration[envKey] ?? configuration[sectionKey] ?? fallback;

    private static long ReadLong(IConfiguration configuration, string envKey, string sectionKey)
        => long.TryParse(configuration[envKey] ?? configuration[sectionKey], out var value) ? value : 0;

    private static double ReadDouble(IConfiguration configuration, string envKey, string sectionKey)
        => double.TryParse(
            configuration[envKey] ?? configuration[sectionKey],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value) ? value : 0;

    private static int ReadInt(IConfiguration configuration, string envKey, string sectionKey, int fallback)
        => int.TryParse(configuration[envKey] ?? configuration[sectionKey], out var value) ? value : fallback;
}
