using System.Windows;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TradeDesktop.App.Services;
using TradeDesktop.App.ViewModels;
using TradeDesktop.App.State;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application;
using TradeDesktop.Infrastructure;

namespace TradeDesktop.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private static readonly object LogLock = new();
    private static bool _fatalDialogShown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        RegisterGlobalExceptionHandlers();
        WriteStartupLog($"OnStartup begin. BaseDirectory={AppContext.BaseDirectory}");

        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices(services =>
                {
                    var configuration = new ConfigurationBuilder()
                        .AddInMemoryCollection(LoadDotEnv())
                        .AddEnvironmentVariables()
                        .Build();

                    services
                        .AddApplication()
                        .AddInfrastructure(configuration);

                    services.AddSingleton<ITradeSessionFileLogger, TradeSessionFileLogger>();
                    services.AddHttpClient<ITelegramNotifier, TelegramNotifier>();
                    services.AddSingleton<RuntimeConfigState>();
                    services.AddSingleton<IMt5ManualTradeService, Mt5ManualTradeService>();
                    services.AddSingleton<ITradePlatformExecutor, Mt5TradeExecutor>();
                    services.AddSingleton<ITradePlatformExecutor, Mt4TradeExecutor>();
                    services.AddSingleton<ITradeExecutionRouter, TradeExecutionRouter>();
                    services.AddSingleton<IRuntimeConfigProvider>(sp => sp.GetRequiredService<RuntimeConfigState>());
                    services.AddSingleton<IRuntimeConfigStateUpdater>(sp => sp.GetRequiredService<RuntimeConfigState>());
                    services.AddSingleton<DashboardViewModel>();
                    services.AddTransient<ConfigViewModel>();
                    services.AddTransient<ConfigWindow>();
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            await _host.StartAsync();
            WriteStartupLog("Host started successfully.");

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();
            WriteStartupLog("MainWindow shown.");
        }
        catch (Exception ex)
        {
            HandleFatalStartupException("Lỗi khởi động ứng dụng", ex);
            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.Services.GetService<ITradeSessionFileLogger>()?.StopSession(DateTimeOffset.Now);
        }
        catch
        {
            // ignore logger shutdown errors
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private static IDictionary<string, string?> LoadDotEnv()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var envPath = FindDotEnvPath();

        if (envPath is null)
        {
            return values;
        }

        foreach (var rawLine in File.ReadLines(envPath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line[7..].TrimStart();
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = line[(separatorIndex + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value.StartsWith('"') && value.EndsWith('"')) ||
                 (value.StartsWith('\'') && value.EndsWith('\''))))
            {
                value = value[1..^1];
            }

            values[key] = value;
        }

        return values;
    }

    private static string? FindDotEnvPath()
    {
        static string? FindInCurrentAndParents(string startDirectory)
        {
            var current = new DirectoryInfo(startDirectory);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, ".env");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }

            return null;
        }

        var fromBaseDirectory = FindInCurrentAndParents(AppContext.BaseDirectory);
        if (fromBaseDirectory is not null)
        {
            return fromBaseDirectory;
        }

        return FindInCurrentAndParents(Directory.GetCurrentDirectory());
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HandleFatalStartupException("Lỗi không xử lý (UI thread)", e.Exception);
        e.Handled = true;
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown domain exception");
        HandleFatalStartupException("Lỗi không xử lý (AppDomain)", ex);
    }

    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        HandleFatalStartupException("Lỗi task không được observe", e.Exception);
        e.SetObserved();
    }

    private static string GetStartupLogPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logDirectory = Path.Combine(localAppData, "TradeDesktop", "logs");
        Directory.CreateDirectory(logDirectory);
        return Path.Combine(logDirectory, "startup.log");
    }

    private static void WriteStartupLog(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";

        lock (LogLock)
        {
            File.AppendAllText(GetStartupLogPath(), line + Environment.NewLine, Encoding.UTF8);
        }
    }

    private static void HandleFatalStartupException(string title, Exception ex)
    {
        WriteStartupLog($"{title}: {ex}");

        if (_fatalDialogShown)
        {
            return;
        }

        _fatalDialogShown = true;

        var logPath = GetStartupLogPath();
        var message =
            $"{title}.\n\n" +
            $"Chi tiết: {ex.Message}\n\n" +
            $"Vui lòng gửi file log:\n{logPath}";

        MessageBox.Show(
            message,
            "TradeDesktop Startup Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}