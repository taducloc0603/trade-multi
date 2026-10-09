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
using TradeDesktop.Application.Helpers;
using TradeDesktop.Application;
using TradeDesktop.Application.Services;
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
                    // Một instance SlotLogger duy nhất phục vụ cả 3 kênh log (main, gap raw,
                    // signal outcome) để không nhân bản forwarder cho cùng session logger.
                    services.AddSingleton<SlotLogger>();
                    services.AddSingleton<ISlotLogger>(sp => sp.GetRequiredService<SlotLogger>());
                    services.AddSingleton<ISignalOutcomeRawLogger>(sp => sp.GetRequiredService<SlotLogger>());
                    // Factory tường minh: ctor còn các tham số int có default, không phụ thuộc
                    // vào việc container có điền default value hay không (lỗi ở đây = crash startup).
                    services.AddSingleton(sp =>
                        new SignalGapOutcomeTracker(sp.GetRequiredService<ISignalOutcomeRawLogger>()));
                    services.AddHttpClient<ITelegramNotifier, TelegramNotifier>();
                    services.AddSingleton<RuntimeConfigState>();
                    services.AddSingleton<IMt5ManualTradeService, Mt5ManualTradeService>();
                    services.AddSingleton<ITradePlatformExecutor, Mt5TradeExecutor>();
                    services.AddSingleton<ITradePlatformExecutor, Mt4TradeExecutor>();
                    // Phase 7 Bước C (2026-09-24): executor THẬT — app có thể đặt lệnh trên sàn B qua FIX.
                    // Quay lại NullCTraderTradeExecutor là cách tắt nhanh nhất nếu cần dừng khẩn cấp.
                    services.AddSingleton<ITradePlatformExecutor, CTraderTradeExecutor>();
                    // PrimeXBT (docs/plans/primexbt Phase 7): executor THẬT. Kill switch = thay dòng dưới bằng
                    // `services.AddSingleton<ITradePlatformExecutor, NullPrimeXbtTradeExecutor>();` ⇒ mọi lệnh sàn B fail an toàn.
                    services.AddSingleton<ITradePlatformExecutor, PrimeXbtTradeExecutor>();
                    services.AddSingleton<IPrimeXbtLoginDialog, PrimeXbtLoginDialog>();
                    services.AddSingleton<CTraderSessionMonitor>();
                    services.AddSingleton<PrimeXbtSessionMonitor>();
                    services.AddSingleton<ITradeExecutionRouter, TradeExecutionRouter>();
                    services.AddSingleton<IWindowProbe, NativeWindowProbe>();
                    services.AddSingleton<IHwndHealthChecker, HwndHealthChecker>();
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

            // Phase 4: gắn monitor TRƯỚC khi MainWindow (→ DashboardViewModel → reader) chạy, để không lỡ sự kiện
            // logon đầu tiên của QUOTE session.
            _host.Services.GetRequiredService<CTraderSessionMonitor>();
            _host.Services.GetRequiredService<PrimeXbtSessionMonitor>();

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

    // Chạy trên FINALIZER THREAD: không được chặn ở đây (MessageBox đồng bộ sẽ treo việc finalize của cả process).
    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (TransportAbortExceptionClassifier.IsBenignTransportAbort(e.Exception))
        {
            WriteStartupLog($"[WARN] Unobserved transport abort (socket FIX đóng khi đang đọc) — bỏ qua: {e.Exception}");
            e.SetObserved();
            return;
        }

        const string title = "Lỗi task không được observe";
        var exception = e.Exception;
        WriteStartupLog($"{title}: {exception}");
        e.SetObserved();

        var dispatcher = Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.BeginInvoke(() => ShowFatalDialog(title, exception));
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
        ShowFatalDialog(title, ex);
    }

    private static void ShowFatalDialog(string title, Exception ex)
    {
        if (_fatalDialogShown)
        {
            return;
        }

        _fatalDialogShown = true;

        var logPath = GetStartupLogPath();
        var message =
            $"{title}.\n\n" +
            $"Chi tiết: {ex.Message}\n\n" +
            $"{DescribeForDialog(ex)}\n\n" +
            $"Vui lòng gửi file log (hoặc chụp màn hình hộp thoại này):\n{logPath}";

        MessageBox.Show(
            message,
            "TradeDesktop Startup Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    // Chẩn đoán phụ không được làm hỏng chính hộp thoại lỗi.
    private static string DescribeForDialog(Exception ex)
    {
        try
        {
            return ExceptionDiagnosticFormatter.Describe(ex);
        }
        catch
        {
            return ex.GetType().Name;
        }
    }
}