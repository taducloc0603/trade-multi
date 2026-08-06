using System.Globalization;
using System.IO;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using TradeDesktop.App.Commands;
using TradeDesktop.App.Helpers;
using TradeDesktop.App.Services;
using TradeDesktop.App.State;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;
using TradeDesktop.Domain.Models;

namespace TradeDesktop.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    private static readonly string AssemblyVersion = GetAssemblyVersion();

    private readonly IServiceProvider _serviceProvider;
    private readonly RuntimeConfigState _runtimeConfigState;
    private readonly IConfigService _configService;
    private readonly IDashboardMetricsMapper _dashboardMetricsMapper;
    private readonly ITradingFlowEngine _tradingFlowEngine;
    private readonly IPortfolioCoordinator _portfolioCoordinator;
    private readonly ITradeInstructionFactory _tradeInstructionFactory;
    private readonly ITradeSignalLogBuilder _tradeSignalLogBuilder;
    private readonly IMachineIdentityService _machineIdentityService;
    private readonly ITradesSharedMemoryReader _tradesSharedMemoryReader;
    private readonly IHistorySharedMemoryReader _historySharedMemoryReader;
    private readonly ITradeExecutionRouter _tradeExecutionRouter;
    private readonly IMt5ManualTradeService _mt5ManualTradeService;
    private readonly ITradeSessionFileLogger _tradeSessionFileLogger;
    private readonly ITelegramNotifier _telegramNotifier;
    private readonly IHwndHealthChecker _hwndHealthChecker;
    private readonly string _normalizedHostName;
    private readonly CancellationTokenSource _orderInfoPollingCts = new();
    private readonly Dictionary<string, ulong> _lastTradeTimestampByMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _lastHistoryTimestampByMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<ulong>> _knownTradeTicketsByMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<ulong>> _knownHistoryTicketsByMap = new(StringComparer.Ordinal);
    private readonly HashSet<string> _initialTradeTicketScanDoneMaps = new(StringComparer.Ordinal);
    private readonly HashSet<string> _initialHistoryTicketScanDoneMaps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<ulong>> _loggedTradeTicketsByMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<ulong>> _loggedHistoryTicketsByMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool?> _lastMmfAvailability = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool?> _lastMmfParseSuccess = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PendingOpenRequest>> _pendingOpenRequestsByMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PendingCloseRequest>> _pendingCloseRequestsByMap = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, PendingOpenRequest> _openRequestByTicket = [];
    private readonly Dictionary<ulong, PendingCloseRequest> _closeRequestByTicket = [];
    private readonly Dictionary<ulong, string> _pairIdByTicket = [];
    private readonly Dictionary<int, OpenConfirmCycleState> _openConfirmBySlot = [];
    private readonly Dictionary<int, CloseConfirmCycleState> _closeConfirmBySlot = [];
    private readonly Dictionary<ulong, double> _openSlippageByTicket = [];
    private readonly Dictionary<ulong, double> _profitSnapshotByTicket = [];
    private readonly Dictionary<ulong, long> _openExecutionMsByTicket = [];
    private readonly Dictionary<ulong, long> _closeExecutionMsByTicket = [];
    private readonly Dictionary<string, int> _sttByPairId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingOpenPairState> _pendingOpenPairById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingClosePairState> _pendingClosePairById = new(StringComparer.Ordinal);
    private int _nextStt = 1;
    private int _manualSlot;
    private int _autoSlot;
    private int _autoOpenInFlight;
    private int _autoOpenInFlightBuy;
    private int _autoOpenInFlightSell;
    // Shared physical close-dispatch mutex. Auto/manual ownership is tracked per slot.
    private int _closeDispatchInFlight;
    private DateTimeOffset? _lastAutoOpenClickAtLocal;
    // Phase 3: per-side debounce — Buy không cản Sell và ngược lại.
    private DateTimeOffset? _lastAutoOpenBuyAtLocal;
    private DateTimeOffset? _lastAutoOpenSellAtLocal;
    private const int AutoOpenDebounceMs = 1500;
    private const int ClosePendingTimeJitterMs = 200;
    private int _closeBothFlatPollStreak;
    private int _externalPartialCloseStreak = 0;
    private bool _hadBothOpenRecently = false;
    private string _lastExternalCloseGuardBlockReason = string.Empty;
    private string _lastOppositeOpenGuardBlockSignature = string.Empty;
    private DateTime _lastOppositeOpenGuardBlockLogAtUtc;
    private const int ExternalPartialCloseStreakRequired = 4;
    private static readonly TimeSpan OppositeOpenGuardBlockLogInterval = TimeSpan.FromSeconds(30);
    private bool _externalPartialCloseInFlight = false;
    private double? _lastLoggedLatencyA;
    private double? _lastLoggedLatencyB;
    private DateTime _lastLatencyLogAtUtc = DateTime.MinValue;
    private const double LatencySpikeThresholdMs = 500.0;
    private const int LatencyLogMinIntervalSeconds = 5;
    private string? _lastTickTokenA;
    private string? _lastTickTokenB;
    private DateTime _lastTickObservedAtA = DateTime.UtcNow;
    private DateTime _lastTickObservedAtB = DateTime.UtcNow;
    private bool _isStaleA;
    private bool _isStaleB;
    private const int StaleTickThresholdSeconds = 10;
    private DateTime _lastPerfSummaryAtUtc = DateTime.MinValue;
    private const int PerfSummaryIntervalSeconds = 60;
    // Perf summary throttle: chỉ log khi signature thô (tps/latency band) đổi; heartbeat tối đa mỗi 5 phút.
    private string _lastPerfSummarySignature = string.Empty;
    private const int PerfSummaryHeartbeatSeconds = 300;
    private const double AlertSlippageThresholdPt = 40.0;
    private const long AlertExecutionThresholdMs = 1000;
    private bool _isAutoOpenPausedByInvariant;
    private int _invariantClearStreak;
    private const int InvariantClearPollsRequired = 10;
    // Self-heal watchdog: chống churn — tối thiểu cách nhau ngần này giây giữa 2 lần resync tự động.
    private DateTime _lastWatchdogSelfHealAtUtc = DateTime.MinValue;
    private const int WatchdogSelfHealMinIntervalSeconds = 30;
    private TradingFlowPhase _lastLoggedPhase = TradingFlowPhase.WaitingOpen;
    private TradingOpenMode _lastLoggedOpenMode = TradingOpenMode.None;
    private TradingPositionSide _lastLoggedPositionSide = TradingPositionSide.None;
    private ActiveAutoCycleState? _activeAutoCycle;
    private ActiveAutoCycleState? _activeAutoCloseRecoveryCycle;
    private readonly Queue<SignalEntryGuard.PriceHistoryEntry> _priceHistory = new();
    private SharedMapReadResult<TradeSharedRecord>? _latestTradeLeftResult;
    private SharedMapReadResult<TradeSharedRecord>? _latestTradeRightResult;

    private static readonly TimeSpan OrderInfoPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan OpenPartialRecheckDelay = TimeSpan.FromSeconds(1);
    private const int StableBothFlatPollsRequired = 4;

    // UI render throttle: snapshot event fires every 50ms (logic path),
    // but heavy UI rebuild (BindDashboardMetrics + RefreshTradeRowsFromSnapshot)
    // only runs at this minimum interval to keep CPU low when a position is open.
    private const long SnapshotUiRenderMinIntervalMs = 200;
    private long _lastSnapshotUiRenderTickMs;

    private sealed record PendingOpenRequest(
        string PairId,
        string TradeMapName,
        string? Symbol,
        int TradeType,
        double? Volume,
        double? ExpectedPrice,
        int HoldingSeconds,
        DateTimeOffset AppOpenRequestTimeLocal,
        long AppOpenRequestUnixMs,
        long AppOpenRequestRawMs,
        bool IsAutoFlow,
        int SlotNumber = 0,
        string ExchangeLabel = "");

    private sealed record PendingCloseRequest(
        string PairId,
        string TradeMapName,
        ulong? Ticket,
        string? Symbol,
        int TradeType,
        double? Volume,
        double? ExpectedPrice,
        DateTimeOffset AppCloseRequestTimeLocal,
        long AppCloseRequestUnixMs,
        long AppCloseRequestRawMs,
        bool IsAutoFlow,
        int SlotNumber = 0,
        string ExchangeLabel = "");

    private sealed class OpenConfirmCycleState
    {
        public bool HasA { get; set; }
        public bool HasB { get; set; }
        public int HoldingSeconds { get; set; }
        public bool HoldingLogged { get; set; }
    }

    private sealed class CloseConfirmCycleState
    {
        public bool HasA { get; set; }
        public bool HasB { get; set; }
        public bool WaitingStarted { get; set; }
    }

    private sealed class ActiveAutoCycleState
    {
        public int Slot { get; init; }
        public DateTimeOffset OpenedAtLocal { get; set; }
        public string? PairIdA { get; set; }
        public string? PairIdB { get; set; }
        public ulong? TicketA { get; set; }
        public ulong? TicketB { get; set; }
    }

    private enum LivePairTradeState
    {
        BothFlat,
        OnlyAOpen,
        OnlyBOpen,
        BothOpen,
        MapUnavailableOrParseError
    }

    private sealed class PendingClosePairState
    {
        public string PairId { get; init; } = string.Empty;
        public bool IsAutoFlow { get; init; }
        public int SlotNumber { get; init; }
        public DateTimeOffset CreatedAtLocal { get; set; }
        public DateTimeOffset LastCheckedAtLocal { get; set; }
        public int ClosePendingTimeoutMs { get; set; }
        public int RetryChecks { get; set; }
        public bool ExhaustedLogged { get; set; }
        public bool IsResolved { get; set; }

        public PendingCloseOrigin Origin { get; set; }

        public bool CloseConfirmedA { get; set; }
        public bool CloseConfirmedB { get; set; }

        public string? TradeMapNameA { get; set; }
        public string? TradeMapNameB { get; set; }
        public TradeLegPlatform? PlatformA { get; set; }
        public TradeLegPlatform? PlatformB { get; set; }
        public string? TradeHwndA { get; set; }
        public string? TradeHwndB { get; set; }
        public ulong? TicketA { get; set; }
        public ulong? TicketB { get; set; }
        public int? TradeTypeA { get; set; }
        public int? TradeTypeB { get; set; }
        public string? SymbolA { get; set; }
        public string? SymbolB { get; set; }
        public double? VolumeA { get; set; }
        public double? VolumeB { get; set; }
    }

    private enum PendingCloseOrigin
    {
        LegacyManual = 0,
        Auto = 1,
        ManualPair = 2,
        Recovery = 3
    }

    private sealed class PendingOpenPairState
    {
        public string PairId { get; init; } = string.Empty;
        public bool IsAutoFlow { get; init; }
        public int SlotNumber { get; init; }
        public DateTimeOffset CreatedAtLocal { get; init; }
        public int OpenPendingTimeoutMs { get; set; }

        public bool OpenConfirmedA { get; set; }
        public bool OpenConfirmedB { get; set; }
        public ulong? OpenedTicketA { get; set; }
        public ulong? OpenedTicketB { get; set; }

        public string? TradeMapNameA { get; set; }
        public string? TradeMapNameB { get; set; }
        public int? TradeTypeA { get; set; }
        public int? TradeTypeB { get; set; }
        public string? SymbolA { get; set; }
        public string? SymbolB { get; set; }
        public double? VolumeA { get; set; }
        public double? VolumeB { get; set; }

        public bool TimeoutCloseTriggered { get; set; }
        public bool TimeoutRecheckPending { get; set; }
        public DateTimeOffset? TimeoutRecheckRequestedAtLocal { get; set; }
        public bool IsResolved { get; set; }
    }

    private sealed record PendingOpenTimeoutAction(
        string PairId,
        bool IsAutoFlow,
        int SlotNumber,
        string OpenedExchange,
        string MissingExchange,
        ulong Ticket,
        string TradeMapName,
        int? TradeType,
        string? Symbol,
        double? Volume);

    private sealed record PendingCloseRetryAction(
        string PairId,
        bool IsAutoFlow,
        int SlotNumber,
        string Exchange,
        string TradeMapName,
        TradeLegPlatform Platform,
        string TradeHwnd,
        ulong Ticket,
        int? TradeType,
        string? Symbol,
        double? Volume);

    private sealed record ResyncedOpenSlot(
        int SlotId,
        string PairId,
        TradingPositionSide Side,
        TradingOpenMode OpenMode,
        TradeSharedRecord RecordA,
        TradeSharedRecord RecordB);

    private string _runtimeSummary = string.Empty;
    private string _dbInlineData = string.Empty;
    private bool _isDbInlineDataVisible;
    private string _configErrorMessage = string.Empty;
    private bool _isConfigErrorVisible;
    private string _exchangeAHeader = "Sàn A";
    private string _exchangeBHeader = "Sàn B";
    private string _gapBuy = "-";
    private string _gapSell = "-";

    private string _exchangeASymbol = "-";
    private string _exchangeABid = "-";
    private string _exchangeAAsk = "-";
    private string _exchangeASpread = "-";
    private string _exchangeALatencyMs = "-";
    private string _exchangeATps = "-";
    private string _exchangeATime = "-";
    private string _exchangeAMaxLatMs = "-";
    private string _exchangeAAvgLatMs = "-";

    private string _exchangeBSymbol = "-";
    private string _exchangeBBid = "-";
    private string _exchangeBAsk = "-";
    private string _exchangeBSpread = "-";
    private string _exchangeBLatencyMs = "-";
    private string _exchangeBTps = "-";
    private string _exchangeBTime = "-";
    private string _exchangeBMaxLatMs = "-";
    private string _exchangeBAvgLatMs = "-";
    private bool _isTradingLogicEnabled;
    private bool _isOpenGapBuyEnabled = true;
    private bool _isOpenGapSellEnabled = true;
    private string _lastSignalText = "-";
    private bool _isLoading = true;
    private string _loadingMessage = "Đang chờ dữ liệu shared memory...";
    private string _machineHostName = string.Empty;
    private bool _isShowConfigVisible;
    private bool _hasManualTradeHwndConfig;
    private bool _isManualOpenInFlight;
    private LivePairTradeState _manualOpenGatePairState = LivePairTradeState.MapUnavailableOrParseError;
    private OrderTabType _selectedOrderTab = OrderTabType.Trade;
    private int _selectedOrderTabIndex;
    private string _historyRealtimeProfitSummary = "0.00 | 0.00 $";

    // HWND health-check / SKIP state (xem plan: phát hiện MT4/MT5 out → bỏ qua mở/đóng).
    private volatile bool _isHwndInvalid;
    private bool _hwndPopupShownForCurrentEpisode;
    private DateTime _lastHwndCheckUtc = DateTime.MinValue;
    private DateTime _lastHwndSkipLogUtc = DateTime.MinValue;

    // Connection kill-switch state (TERMINAL_CONNECTED từ trades-header → tự off switch).
    private readonly ConnectionStatusEvaluator _connectionEvaluator = new();
    private volatile bool _isConnectionDown;
    private bool _connPopupShownForCurrentEpisode;
    private DateTime _lastConnSkipLogUtc = DateTime.MinValue;

    public DashboardViewModel(
        IServiceProvider serviceProvider,
        RuntimeConfigState runtimeConfigState,
        IConfigService configService,
        IExchangePairReader exchangePairReader,
        IDashboardMetricsMapper dashboardMetricsMapper,
        ITradingFlowEngine tradingFlowEngine,
        IPortfolioCoordinator portfolioCoordinator,
        ITradeInstructionFactory tradeInstructionFactory,
        ITradeSignalLogBuilder tradeSignalLogBuilder,
        IMachineIdentityService machineIdentityService,
        ITradesSharedMemoryReader tradesSharedMemoryReader,
        IHistorySharedMemoryReader historySharedMemoryReader,
        ITradeExecutionRouter tradeExecutionRouter,
        IMt5ManualTradeService mt5ManualTradeService,
        ITradeSessionFileLogger tradeSessionFileLogger,
        ITelegramNotifier telegramNotifier,
        IHwndHealthChecker hwndHealthChecker)
    {
        _serviceProvider = serviceProvider;
        _runtimeConfigState = runtimeConfigState;
        _configService = configService;
        _dashboardMetricsMapper = dashboardMetricsMapper;
        _tradingFlowEngine = tradingFlowEngine;
        _portfolioCoordinator = portfolioCoordinator;
        SyncPortfolioCoordinatorConfig();
        _tradeInstructionFactory = tradeInstructionFactory;
        _tradeSignalLogBuilder = tradeSignalLogBuilder;
        _machineIdentityService = machineIdentityService;
        _tradesSharedMemoryReader = tradesSharedMemoryReader;
        _historySharedMemoryReader = historySharedMemoryReader;
        _tradeExecutionRouter = tradeExecutionRouter;
        _mt5ManualTradeService = mt5ManualTradeService;
        _tradeSessionFileLogger = tradeSessionFileLogger;
        _telegramNotifier = telegramNotifier;
        _hwndHealthChecker = hwndHealthChecker;

        var normalizedHostName = _machineIdentityService.GetHostName();
        _normalizedHostName = normalizedHostName;
        MachineHostName = normalizedHostName;

        OpenConfigCommand = new AsyncRelayCommand(OpenConfigAsync);
        ReconnectConfigCommand = new AsyncRelayCommand(ReconnectConfigAsync);
        CopyHostNameCommand = new AsyncRelayCommand(CopyHostNameAsync);
        OpenLogFolderCommand = new AsyncRelayCommand(OpenLogFolderAsync);
        OpenCurrentLogCommand = new AsyncRelayCommand(OpenCurrentLogAsync);
        StartTradingLogicCommand = new AsyncRelayCommand(StartTradingLogicAsync, CanStartTradingLogic);
        StopTradingLogicCommand = new AsyncRelayCommand(StopTradingLogicAsync, CanStopTradingLogic);
        BuyCommand = new AsyncRelayCommand(BuyAsync, CanManualOpen);
        SellCommand = new AsyncRelayCommand(SellAsync, CanManualOpen);
        CloseOrderCommand = new AsyncRelayCommand(CloseOrderAsync, CanManualClose);
        ClosePairManuallyCommand = new AsyncRelayCommand<TradePairRealtimeProfitRowViewModel?>(
            row => ManualClosePairBySlotAsync(row?.PairId ?? string.Empty),
            CanManualPairClose);

        TradeTab = new OrderInfoTabViewModel(
            OrderTabType.Trade,
            "Trade",
            new OrderPanelStatusViewModel("Sàn A", OrderRecordLayoutMode.TradeTable),
            new OrderPanelStatusViewModel("Sàn B", OrderRecordLayoutMode.TradeTable));

        HistoryTab = new OrderInfoTabViewModel(
            OrderTabType.History,
            "History",
            new OrderPanelStatusViewModel("Sàn A", OrderRecordLayoutMode.HistoryTable),
            new OrderPanelStatusViewModel("Sàn B", OrderRecordLayoutMode.HistoryTable));

        OrderTabs = [TradeTab, HistoryTab];
        SignalLogItems.CollectionChanged += OnSignalLogItemsCollectionChanged;

        _runtimeConfigState.StateChanged += (_, _) => ApplyRuntimeConfig();
        _runtimeConfigState.QualifyingConfigChanged += OnQualifyingConfigChanged;
        _runtimeConfigState.ManualHwndChanged += (_, _) => RunHwndHealthCheck("config-save");
        ApplyRuntimeConfig();
        _ = InitializeRuntimeConfigAsync();

        exchangePairReader.SnapshotReceived += OnSnapshotReceived;
        _ = StartExchangeReaderSafeAsync(exchangePairReader);
        _ = RunOrderInfoPollingAsync(_orderInfoPollingCts.Token);
    }

    public string RuntimeSummary
    {
        get => _runtimeSummary;
        private set => SetProperty(ref _runtimeSummary, value);
    }

    public string BuildVersion => $"Version {AssemblyVersion}";

    public string DbInlineData
    {
        get => _dbInlineData;
        private set => SetProperty(ref _dbInlineData, value);
    }

    public bool IsDbInlineDataVisible
    {
        get => _isDbInlineDataVisible;
        private set => SetProperty(ref _isDbInlineDataVisible, value);
    }

    public string ConfigErrorMessage
    {
        get => _configErrorMessage;
        private set => SetProperty(ref _configErrorMessage, value);
    }

    public bool IsConfigErrorVisible
    {
        get => _isConfigErrorVisible;
        private set => SetProperty(ref _isConfigErrorVisible, value);
    }

    public string ExchangeAHeader
    {
        get => _exchangeAHeader;
        private set => SetProperty(ref _exchangeAHeader, value);
    }

    public string ExchangeBHeader
    {
        get => _exchangeBHeader;
        private set => SetProperty(ref _exchangeBHeader, value);
    }

    public string GapBuy
    {
        get => _gapBuy;
        private set => SetProperty(ref _gapBuy, value);
    }

    public string GapSell
    {
        get => _gapSell;
        private set => SetProperty(ref _gapSell, value);
    }

    public string ExchangeASymbol { get => _exchangeASymbol; private set => SetProperty(ref _exchangeASymbol, value); }
    public string ExchangeABid { get => _exchangeABid; private set => SetProperty(ref _exchangeABid, value); }
    public string ExchangeAAsk { get => _exchangeAAsk; private set => SetProperty(ref _exchangeAAsk, value); }
    public string ExchangeASpread { get => _exchangeASpread; private set => SetProperty(ref _exchangeASpread, value); }
    public string ExchangeALatencyMs { get => _exchangeALatencyMs; private set => SetProperty(ref _exchangeALatencyMs, value); }
    public string ExchangeATps { get => _exchangeATps; private set => SetProperty(ref _exchangeATps, value); }
    public string ExchangeATime { get => _exchangeATime; private set => SetProperty(ref _exchangeATime, value); }
    public string ExchangeAMaxLatMs { get => _exchangeAMaxLatMs; private set => SetProperty(ref _exchangeAMaxLatMs, value); }
    public string ExchangeAAvgLatMs { get => _exchangeAAvgLatMs; private set => SetProperty(ref _exchangeAAvgLatMs, value); }

    public string ExchangeBSymbol { get => _exchangeBSymbol; private set => SetProperty(ref _exchangeBSymbol, value); }
    public string ExchangeBBid { get => _exchangeBBid; private set => SetProperty(ref _exchangeBBid, value); }
    public string ExchangeBAsk { get => _exchangeBAsk; private set => SetProperty(ref _exchangeBAsk, value); }
    public string ExchangeBSpread { get => _exchangeBSpread; private set => SetProperty(ref _exchangeBSpread, value); }
    public string ExchangeBLatencyMs { get => _exchangeBLatencyMs; private set => SetProperty(ref _exchangeBLatencyMs, value); }
    public string ExchangeBTps { get => _exchangeBTps; private set => SetProperty(ref _exchangeBTps, value); }
    public string ExchangeBTime { get => _exchangeBTime; private set => SetProperty(ref _exchangeBTime, value); }
    public string ExchangeBMaxLatMs { get => _exchangeBMaxLatMs; private set => SetProperty(ref _exchangeBMaxLatMs, value); }
    public string ExchangeBAvgLatMs { get => _exchangeBAvgLatMs; private set => SetProperty(ref _exchangeBAvgLatMs, value); }
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public string LoadingMessage { get => _loadingMessage; private set => SetProperty(ref _loadingMessage, value); }
    public string MachineHostName { get => _machineHostName; private set => SetProperty(ref _machineHostName, value); }

    public bool IsShowConfigVisible
    {
        get => _isShowConfigVisible;
        private set => SetProperty(ref _isShowConfigVisible, value);
    }

    public bool HasManualTradeHwndConfig
    {
        get => _hasManualTradeHwndConfig;
        private set
        {
            if (!SetProperty(ref _hasManualTradeHwndConfig, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsManualTradeWarningVisible));
            RaiseManualOpenCanExecuteChanged();
        }
    }

    public bool IsManualTradeWarningVisible => !HasManualTradeHwndConfig;

    public string ManualTradeWarningMessage =>
        "Vui lòng nhập đầy đủ CHART/TRADE HWND cho sàn A và sàn B trong Config";

    public string LastSignalText
    {
        get => _lastSignalText;
        private set => SetProperty(ref _lastSignalText, value);
    }

    private const int MaxSignalLogItems = 500;
    public ObservableCollection<string> SignalLogItems { get; } = new CappedObservableCollection<string>(MaxSignalLogItems);
    public ObservableCollection<TradePairRealtimeProfitRowViewModel> TradeRealtimeProfitRows { get; } = [];
    public ObservableCollection<HistoryPairProfitRowViewModel> HistoryRealtimeProfitRows { get; } = [];
    public string HistoryRealtimeProfitSummary
    {
        get => _historyRealtimeProfitSummary;
        private set => SetProperty(ref _historyRealtimeProfitSummary, value);
    }

    public IReadOnlyList<OrderInfoTabViewModel> OrderTabs { get; }
    public OrderInfoTabViewModel TradeTab { get; }
    public OrderInfoTabViewModel HistoryTab { get; }

    public OrderTabType SelectedOrderTab
    {
        get => _selectedOrderTab;
        set
        {
            if (!SetProperty(ref _selectedOrderTab, value))
            {
                return;
            }

            var tabIndex = value == OrderTabType.History ? 1 : 0;
            if (_selectedOrderTabIndex != tabIndex)
            {
                _selectedOrderTabIndex = tabIndex;
                OnPropertyChanged(nameof(SelectedOrderTabIndex));
            }
        }
    }

    public int SelectedOrderTabIndex
    {
        get => _selectedOrderTabIndex;
        set
        {
            if (!SetProperty(ref _selectedOrderTabIndex, value))
            {
                return;
            }

            SelectedOrderTab = value == 1 ? OrderTabType.History : OrderTabType.Trade;
        }
    }

    public bool IsTradingLogicEnabled
    {
        get => _isTradingLogicEnabled;
        private set
        {
            if (!SetProperty(ref _isTradingLogicEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(TradingLogicStatusText));
            OnPropertyChanged(nameof(TradingLogicStatusBrush));
            StartTradingLogicCommand.RaiseCanExecuteChanged();
            StopTradingLogicCommand.RaiseCanExecuteChanged();
        }
    }

    public string TradingLogicStatusText => IsTradingLogicEnabled ? "Running" : "Stopped";
    public Brush TradingLogicStatusBrush => IsTradingLogicEnabled ? Brushes.ForestGreen : Brushes.Gray;

    // Switch cho phép thao tác lệnh (open/close). Tự OFF khi monitor phát hiện mất kết nối;
    // user tự bật lại. OFF → mọi open/close bị skip (xem SkipIfTradeOpsDisabled).
    private bool _allowTradeOperations = true;
    public bool AllowTradeOperations
    {
        get => _allowTradeOperations;
        set
        {
            if (!SetProperty(ref _allowTradeOperations, value))
            {
                return;
            }

            OnPropertyChanged(nameof(TradeOperationsStatusText));
            SafeVmLog($"[CONN][INFO] AllowTradeOperations={value}");
        }
    }

    public string TradeOperationsStatusText => AllowTradeOperations ? "Cho phép lệnh" : "Đã chặn lệnh";
    public string CurrentPositionTextA => ResolveCurrentPositionText(isExchangeA: true);
    public string CurrentPositionTextB => ResolveCurrentPositionText(isExchangeA: false);
    public string CurrentPhaseText => ResolveCurrentPhaseText();
    public bool IsOpenGapBuyEnabled
    {
        get => _isOpenGapBuyEnabled;
        set
        {
            if (!SetProperty(ref _isOpenGapBuyEnabled, value))
            {
                return;
            }

            SafeVmLog($"[UI][INFO] Toggle changed: IsOpenGapBuyEnabled={value}");
        }
    }

    public bool IsOpenGapSellEnabled
    {
        get => _isOpenGapSellEnabled;
        set
        {
            if (!SetProperty(ref _isOpenGapSellEnabled, value))
            {
                return;
            }

            SafeVmLog($"[UI][INFO] Toggle changed: IsOpenGapSellEnabled={value}");
        }
    }

    public AsyncRelayCommand OpenConfigCommand { get; }
    public AsyncRelayCommand ReconnectConfigCommand { get; }
    public AsyncRelayCommand CopyHostNameCommand { get; }
    public AsyncRelayCommand OpenLogFolderCommand { get; }
    public AsyncRelayCommand OpenCurrentLogCommand { get; }
    public AsyncRelayCommand StartTradingLogicCommand { get; }
    public AsyncRelayCommand StopTradingLogicCommand { get; }
    public IAsyncRelayCommand BuyCommand { get; }
    public IAsyncRelayCommand SellCommand { get; }
    public IAsyncRelayCommand CloseOrderCommand { get; }
    public IAsyncRelayCommand ClosePairManuallyCommand { get; }

    private bool CanStartTradingLogic() => !IsTradingLogicEnabled;
    private bool CanStopTradingLogic() => IsTradingLogicEnabled;
    private bool CanManualOpen()
        => IsTradingLogicEnabled
           && HasManualTradeHwndConfig
           && !_isManualOpenInFlight
           && _manualOpenGatePairState == LivePairTradeState.BothFlat;

    private bool CanManualClose()
        => IsTradingLogicEnabled
           && HasManualTradeHwndConfig;

    private bool CanManualPairClose(TradePairRealtimeProfitRowViewModel? row)
        => row is not null
           && !string.IsNullOrWhiteSpace(row.PairId)
           && !_portfolioCoordinator.HasNonAutoCloseInFlight;

    private void RaiseClosePairCanExecuteChanged()
        => ClosePairManuallyCommand.RaiseCanExecuteChanged();

    private void RaiseNonAutoCloseUiStateChanged()
    {
        RaiseClosePairCanExecuteChanged();
        RaiseCurrentPositionTextChanged();
        OnPropertyChanged(nameof(CurrentPhaseText));
    }

    private void RaiseManualOpenCanExecuteChanged()
    {
        BuyCommand.RaiseCanExecuteChanged();
        SellCommand.RaiseCanExecuteChanged();
        CloseOrderCommand.RaiseCanExecuteChanged();
    }

    private void RefreshManualOpenAvailability(LivePairTradeState pairState)
    {
        if (_manualOpenGatePairState == pairState)
        {
            return;
        }

        _manualOpenGatePairState = pairState;
        RaiseManualOpenCanExecuteChanged();
    }

    // ===== HWND health-check + SKIP guard =====

    /// <summary>
    /// Kiểm tra toàn bộ HWND trong config. Nếu có giá trị không hợp lệ → bật cờ SKIP,
    /// log, và popup 1 lần/đợt lỗi. Nếu trở lại hợp lệ → gỡ SKIP + log resume.
    /// Gọi tại: Start, định kỳ 60s (polling), và sau khi lưu config (ManualHwndChanged).
    /// </summary>
    private void RunHwndHealthCheck(string source)
    {
        IReadOnlyList<HwndIssue> issues;
        try
        {
            issues = _hwndHealthChecker.Check(_runtimeConfigState.CurrentManualHwndColumns);
        }
        catch (Exception ex)
        {
            SafeVmLog($"[HWND][ERROR] Health-check ({source}) threw: {ex.Message}");
            return;
        }

        if (issues.Count > 0)
        {
            var wasValid = !_isHwndInvalid;
            _isHwndInvalid = true;
            SafeVmLog($"[HWND][WARN] SKIP bật ({source}): " +
                      string.Join("; ", issues.Select(i => $"{i.Label}={Quote(i.Value)}|{i.Kind}")));

            if (wasValid)
            {
                _hwndPopupShownForCurrentEpisode = false; // đợt lỗi mới
            }

            if (!_hwndPopupShownForCurrentEpisode)
            {
                _hwndPopupShownForCurrentEpisode = true;
                ShowHwndInvalidPopup(issues);
            }

            // TODO(telegram): sau này gửi noti khi gặp HWND invalid.
            // _ = Task.Run(() => _telegramNotifier.NotifyAsync("HWND_INVALID", "CRITICAL",
            //     string.Join("; ", issues.Select(i => $"{i.Label}={i.Value}|{i.Kind}"))));
        }
        else if (_isHwndInvalid)
        {
            _isHwndInvalid = false;
            _hwndPopupShownForCurrentEpisode = false;
            SafeVmLog($"[HWND][INFO] HWND hợp lệ trở lại ({source}) — KHÔNG skip nữa, tiếp tục giao dịch.");
        }
    }

    /// <summary>
    /// Trả true nếu đang ở trạng thái HWND invalid → caller phải bỏ qua thao tác lệnh.
    /// Log throttle 5s để tránh spam (snapshot ~50ms).
    /// </summary>
    private bool SkipIfHwndInvalid(string op)
    {
        if (!_isHwndInvalid)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastHwndSkipLogUtc).TotalSeconds >= 5)
        {
            _lastHwndSkipLogUtc = now;
            SafeVmLog($"[HWND][SKIP] Bỏ qua {op}: HWND không hợp lệ — chờ sửa config & lưu.");
        }

        return true;
    }

    private void ShowHwndInvalidPopup(IReadOnlyList<HwndIssue> issues)
    {
        var lines = string.Join(Environment.NewLine,
            issues.Select(i => $"• {i.Label}: {Quote(i.Value)} — {ReasonText(i.Kind)}"));
        var message =
            "Phát hiện HWND không hợp lệ. Đã TẠM DỪNG mọi thao tác mở/đóng lệnh." + Environment.NewLine +
            Environment.NewLine +
            lines + Environment.NewLine +
            Environment.NewLine +
            "Vui lòng mở Config, cập nhật lại HWND và bấm Lưu để tiếp tục giao dịch.";

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                System.Windows.MessageBox.Show(
                    message,
                    "HWND không hợp lệ",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
            catch
            {
                // ignored by design
            }
        }));
    }

    private static string ReasonText(HwndIssueKind kind) => kind switch
    {
        HwndIssueKind.Empty => "để trống",
        HwndIssueKind.BadFormat => "sai định dạng",
        HwndIssueKind.WindowMissing => "cửa sổ không tồn tại",
        _ => "không hợp lệ"
    };

    private static string Quote(string value) => string.IsNullOrEmpty(value) ? "(trống)" : value;

    // ===== Connection kill-switch =====

    private static ChannelConnectionInput ToChannelConnectionInput<T>(SharedMapReadResult<T> result)
        => new(result.IsMapAvailable, result.IsParseSuccess, result.Timestamp, result.Connected);

    /// <summary>
    /// Áp dụng trạng thái kết nối (gọi trên UI thread). Down → tự OFF switch + popup 1 lần/đợt + log.
    /// Reconnect → log nhưng KHÔNG tự bật lại (user tự bật). Idempotent qua các poll.
    /// </summary>
    private void ApplyConnectionStatus(ConnectionStatus status)
    {
        if (status.IsDown)
        {
            var wasUp = !_isConnectionDown;
            _isConnectionDown = true;

            if (wasUp)
            {
                _connPopupShownForCurrentEpisode = false; // đợt mất kết nối mới
                SafeVmLog($"[CONN][WARN] Mất kết nối — chặn thao tác lệnh: {string.Join("; ", status.Reasons)}");
            }

            if (AllowTradeOperations)
            {
                AllowTradeOperations = false; // tự tắt switch → skip mọi open/close
            }

            if (!_connPopupShownForCurrentEpisode)
            {
                _connPopupShownForCurrentEpisode = true;
                ShowConnectionLostPopup(status.Reasons);
            }
        }
        else if (_isConnectionDown)
        {
            _isConnectionDown = false;
            _connPopupShownForCurrentEpisode = false;
            SafeVmLog("[CONN][INFO] Kết nối trở lại — switch VẪN off, bật tay để giao dịch lại.");
        }
    }

    /// <summary>
    /// Trả true nếu switch đang OFF → caller phải bỏ qua thao tác lệnh. Log throttle 5s (poll ~500ms).
    /// </summary>
    private bool SkipIfTradeOpsDisabled(string op)
    {
        if (AllowTradeOperations)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastConnSkipLogUtc).TotalSeconds >= 5)
        {
            _lastConnSkipLogUtc = now;
            SafeVmLog($"[CONN][SKIP] Bỏ qua {op}: switch OFF (mất kết nối hoặc user tắt) — bật lại để tiếp tục.");
        }

        return true;
    }

    /// <summary>
    /// Gate chung cho MỌI thao tác mở/đóng: bỏ qua nếu HWND invalid HOẶC switch cho phép lệnh OFF.
    /// Short-circuit nên chỉ một guard log mỗi lần.
    /// </summary>
    private bool ShouldSkipTradeOp(string op) => SkipIfHwndInvalid(op) || SkipIfTradeOpsDisabled(op);

    private void ShowConnectionLostPopup(IReadOnlyList<string> reasons)
    {
        var lines = string.Join(Environment.NewLine, reasons.Select(r => $"• {r}"));
        var message =
            "Phát hiện MẤT KẾT NỐI. Đã TẠM DỪNG mọi thao tác mở/đóng lệnh (switch OFF)." + Environment.NewLine +
            Environment.NewLine +
            lines + Environment.NewLine +
            Environment.NewLine +
            "Kiểm tra lại kết nối MT4/MT5. Khi ổn định, bật lại switch để tiếp tục giao dịch.";

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                System.Windows.MessageBox.Show(
                    message,
                    "Mất kết nối MT4/MT5",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
            catch
            {
                // ignored by design
            }
        }));
    }

    private async Task StartTradingLogicAsync()
    {
        if (IsTradingLogicEnabled)
        {
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            "Bạn có chắc muốn bắt đầu chạy Auto Trading?",
            "Xác nhận chạy Auto",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        _tradeSessionFileLogger.StartSession(DateTimeOffset.Now, _normalizedHostName);
        _tradeSessionFileLogger.Log("Trading logic start confirmed by user");

        ResetTradingLogicState();
        // Wipe display state on Start only — Stop giữ nguyên để xem P&L cuối session
        _sttByPairId.Clear();
        _nextStt = 1;
        _profitSnapshotByTicket.Clear();
        TradeRealtimeProfitRows.Clear();
        HistoryRealtimeProfitRows.Clear();
        HistoryRealtimeProfitSummary = "0.00 | 0.00 $";
        HistoryTab.LeftPanel.SetEmpty();
        HistoryTab.RightPanel.SetEmpty();
        LastSignalText = "-";
        SignalLogItems.Clear();

        await ResyncOpenTradesAsync();

        // Kiểm tra HWND ngay khi Start: nếu MT4/MT5 không còn cửa sổ hợp lệ thì vẫn
        // vào running nhưng bật cờ SKIP (không mở/đóng) + popup cảnh báo.
        RunHwndHealthCheck("start");

        // Connection kill-switch: bật cho phép lệnh + reset monitor. Từ đây poll order-info
        // sẽ giám sát kết nối liên tục và tự OFF switch nếu 1 trong 2 sàn rớt.
        _connectionEvaluator.Reset();
        _isConnectionDown = false;
        _connPopupShownForCurrentEpisode = false;
        AllowTradeOperations = true;

        IsTradingLogicEnabled = true;
        SyncTradingFlowWithLivePairState(GetLivePairTradeStateStrict());
        _lastLoggedPhase = _tradingFlowEngine.CurrentPhase;
        _lastLoggedOpenMode = _tradingFlowEngine.CurrentOpenMode;
        _lastLoggedPositionSide = _tradingFlowEngine.CurrentPositionSide;
        SafeVmLog($"[FLOW][INFO] Session start: phase={_lastLoggedPhase} openMode={_lastLoggedOpenMode} side={_lastLoggedPositionSide}");
    }

    private Task StopTradingLogicAsync()
    {
        if (!IsTradingLogicEnabled)
        {
            return Task.CompletedTask;
        }

        _tradeSessionFileLogger.Log("Trading logic stop requested");
        SafeVmLog($"[FLOW][INFO] Session stop: phase={_tradingFlowEngine.CurrentPhase} openMode={_tradingFlowEngine.CurrentOpenMode} side={_tradingFlowEngine.CurrentPositionSide}");

        IsTradingLogicEnabled = false;
        ResetTradingLogicState();
        LastSignalText = "-";
        _tradeSessionFileLogger.StopSession(DateTimeOffset.Now);
        return Task.CompletedTask;
    }

    private void OnSignalLogItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not NotifyCollectionChangedAction.Add || e.NewItems is null || e.NewItems.Count == 0)
        {
            return;
        }

        try
        {
            if (e.NewStartingIndex == 0 && e.NewItems.Count > 1)
            {
                for (var i = e.NewItems.Count - 1; i >= 0; i--)
                {
                    if (e.NewItems[i] is string line && !string.IsNullOrWhiteSpace(line))
                    {
                        _tradeSessionFileLogger.Log(line);
                    }
                }

                return;
            }

            foreach (var item in e.NewItems)
            {
                if (item is string line && !string.IsNullOrWhiteSpace(line))
                {
                    _tradeSessionFileLogger.Log(line);
                }
            }
        }
        catch (Exception ex)
        {
            // Phase A requirement: never break runtime flow because of file logging.
            SafeVmLog($"[VM][WARN] Suppressed exception at OnSignalLogItemsCollectionChanged: {ex.Message}");
        }
    }

    private async Task BuyAsync()
    {
        if (ShouldSkipTradeOp("manual-buy"))
        {
            return;
        }

        _isManualOpenInFlight = true;
        RaiseManualOpenCanExecuteChanged();

        try
        {
            var (_, hwndColumn) = _runtimeConfigState.GetRandomManualHwndColumn();
            var snapshot = _runtimeConfigState.CurrentDashboardMetrics;
            var appOpenRequestTimeLocal = DateTimeOffset.Now;
            var appOpenRequestRawMs = Environment.TickCount64;
            var slot = _manualSlot;

            // Capture pending request BEFORE executing click to avoid race with shared-memory polling.
            CapturePendingOpenRequest(TradeTab.LeftPanel.TargetMapName, snapshot, isExchangeA: true, tradeType: 0, appOpenRequestTimeLocal, appOpenRequestRawMs, slot);
            CapturePendingOpenRequest(TradeTab.RightPanel.TargetMapName, snapshot, isExchangeA: false, tradeType: 1, appOpenRequestTimeLocal, appOpenRequestRawMs, slot);

            var result = await _tradeExecutionRouter.OpenPairAsync(
                new TradeOpenPairRequest(
                    new TradeOpenLegRequest(
                        Exchange: "A",
                        Platform: ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformA),
                        ChartHwnd: hwndColumn.ChartHwndA,
                        Action: TradeLegAction.Buy),
                    new TradeOpenLegRequest(
                        Exchange: "B",
                        Platform: ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformB),
                        ChartHwnd: hwndColumn.ChartHwndB,
                        Action: TradeLegAction.Sell),
                    Context: CreateExecutionContext(
                        TradeExecutionReason.ManualOpen,
                        "manual-buy")));

            NotifyOpenCloseFailures("OPEN", result, BuildPairId(slot, appOpenRequestRawMs, isAutoFlow: false));
            if (result.IsDispatchBlocked)
            {
                RemovePendingOpenRequests(appOpenRequestRawMs);
                SafeVmLog($"[TRADE_GATE][BLOCKED] manual OPEN remainingMs={result.GateRemainingMilliseconds}");
                ShowManualTradeFeedback("BUY", result);
                return;
            }

            // Phase 1 Manual Open log: A buy, B sell
            var now = DateTime.Now;
            var gap = _runtimeConfigState.CurrentDashboardMetrics?.GapBuy;
            var symbolA = snapshot?.ExchangeA.Symbol ?? "-";
            var symbolB = snapshot?.ExchangeB.Symbol ?? "-";
            var priceA = SignalLogFormatter.ResolveOpenPrice(snapshot?.ExchangeA.Bid, snapshot?.ExchangeA.Ask, isBuy: true);
            var priceB = SignalLogFormatter.ResolveOpenPrice(snapshot?.ExchangeB.Bid, snapshot?.ExchangeB.Ask, isBuy: false);
            var spreadText = BuildSpreadPtsText(snapshot);
            var displayStt = ResolveDisplayStt(BuildPairId(slot, appOpenRequestRawMs, isAutoFlow: false), slot);
            SignalLogItems.Insert(0, SignalLogFormatter.FormatManualOpen(now, displayStt, "B", "SELL", symbolB, priceB, gap, spreadText));
            SignalLogItems.Insert(0, SignalLogFormatter.FormatManualOpen(now, displayStt, "A", "BUY", symbolA, priceA, gap, spreadText));

            _manualSlot++;
            ShowManualTradeFeedback("BUY", result);

            if (result.Success || result.Legs.Any(x => x.Success))
            {
                _tradingFlowEngine.ForceWaitingClose(TradingPositionSide.Buy);
                LogFlowTransitionIfChanged("force-waiting-close-side=buy");
                RaiseCurrentPositionTextChanged();
                OnPropertyChanged(nameof(CurrentPhaseText));
            }
        }
        finally
        {
            _isManualOpenInFlight = false;
            SyncTradingFlowWithLivePairState(GetLivePairTradeStateStrict());
            RefreshManualOpenAvailability(ComputeToolAwarePairStateForOpenGate(GetLivePairTradeStateStrict()));
            RaiseManualOpenCanExecuteChanged();
        }
    }

    private async Task SellAsync()
    {
        if (ShouldSkipTradeOp("manual-sell"))
        {
            return;
        }

        _isManualOpenInFlight = true;
        RaiseManualOpenCanExecuteChanged();

        try
        {
            var (_, hwndColumn) = _runtimeConfigState.GetRandomManualHwndColumn();
            var snapshot = _runtimeConfigState.CurrentDashboardMetrics;
            var appOpenRequestTimeLocal = DateTimeOffset.Now;
            var appOpenRequestRawMs = Environment.TickCount64;
            var slot = _manualSlot;

            // Capture pending request BEFORE executing click to avoid race with shared-memory polling.
            CapturePendingOpenRequest(TradeTab.LeftPanel.TargetMapName, snapshot, isExchangeA: true, tradeType: 1, appOpenRequestTimeLocal, appOpenRequestRawMs, slot);
            CapturePendingOpenRequest(TradeTab.RightPanel.TargetMapName, snapshot, isExchangeA: false, tradeType: 0, appOpenRequestTimeLocal, appOpenRequestRawMs, slot);

            var result = await _tradeExecutionRouter.OpenPairAsync(
                new TradeOpenPairRequest(
                    new TradeOpenLegRequest(
                        Exchange: "A",
                        Platform: ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformA),
                        ChartHwnd: hwndColumn.ChartHwndA,
                        Action: TradeLegAction.Sell),
                    new TradeOpenLegRequest(
                        Exchange: "B",
                        Platform: ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformB),
                        ChartHwnd: hwndColumn.ChartHwndB,
                        Action: TradeLegAction.Buy),
                    Context: CreateExecutionContext(
                        TradeExecutionReason.ManualOpen,
                        "manual-sell")));

            if (result.IsDispatchBlocked)
            {
                RemovePendingOpenRequests(appOpenRequestRawMs);
                SafeVmLog($"[TRADE_GATE][BLOCKED] manual OPEN remainingMs={result.GateRemainingMilliseconds}");
                ShowManualTradeFeedback("SELL", result);
                return;
            }

            // Phase 1 Manual Open log: A sell, B buy
            var now = DateTime.Now;
            var gap = _runtimeConfigState.CurrentDashboardMetrics?.GapSell;
            var symbolA = snapshot?.ExchangeA.Symbol ?? "-";
            var symbolB = snapshot?.ExchangeB.Symbol ?? "-";
            var priceA = SignalLogFormatter.ResolveOpenPrice(snapshot?.ExchangeA.Bid, snapshot?.ExchangeA.Ask, isBuy: false);
            var priceB = SignalLogFormatter.ResolveOpenPrice(snapshot?.ExchangeB.Bid, snapshot?.ExchangeB.Ask, isBuy: true);
            var spreadText = BuildSpreadPtsText(snapshot);
            var displayStt = ResolveDisplayStt(BuildPairId(slot, appOpenRequestRawMs, isAutoFlow: false), slot);
            SignalLogItems.Insert(0, SignalLogFormatter.FormatManualOpen(now, displayStt, "B", "BUY", symbolB, priceB, gap, spreadText));
            SignalLogItems.Insert(0, SignalLogFormatter.FormatManualOpen(now, displayStt, "A", "SELL", symbolA, priceA, gap, spreadText));

            _manualSlot++;
            ShowManualTradeFeedback("SELL", result);

            if (result.Success || result.Legs.Any(x => x.Success))
            {
                _tradingFlowEngine.ForceWaitingClose(TradingPositionSide.Sell);
                LogFlowTransitionIfChanged("force-waiting-close-side=sell");
                RaiseCurrentPositionTextChanged();
                OnPropertyChanged(nameof(CurrentPhaseText));
            }
        }
        finally
        {
            _isManualOpenInFlight = false;
            SyncTradingFlowWithLivePairState(GetLivePairTradeStateStrict());
            RefreshManualOpenAvailability(ComputeToolAwarePairStateForOpenGate(GetLivePairTradeStateStrict()));
            RaiseManualOpenCanExecuteChanged();
        }
    }

    private async Task CloseOrderAsync()
    {
        if (ShouldSkipTradeOp("manual-close-legacy"))
        {
            return;
        }

        var snapshot = _runtimeConfigState.CurrentDashboardMetrics;
        var slot = Math.Max(0, _manualSlot - 1);
        var selectA = SelectCloseCandidateForExchange(
            exchangeLabel: "A",
            tradeMapName: TradeTab.LeftPanel.TargetMapName,
            tradeHwnd: _runtimeConfigState.CurrentTradeHwndA);

        var selectB = SelectCloseCandidateForExchange(
            exchangeLabel: "B",
            tradeMapName: TradeTab.RightPanel.TargetMapName,
            tradeHwnd: _runtimeConfigState.CurrentTradeHwndB);

        var appCloseRequestTimeLocal = DateTimeOffset.Now;
        var appCloseRequestRawMs = Environment.TickCount64;

        // Capture pending request BEFORE executing close to avoid race with shared-memory polling.
        CapturePendingCloseRequest(selectA, snapshot, isExchangeA: true, appCloseRequestTimeLocal, appCloseRequestRawMs, slot);
        CapturePendingCloseRequest(selectB, snapshot, isExchangeA: false, appCloseRequestTimeLocal, appCloseRequestRawMs, slot);

        var result = await _tradeExecutionRouter.ClosePairAsync(
            new TradeClosePairRequest(
                LegA: selectA.Request is null
                    ? null
                    : new TradeCloseLegRequest(
                        Exchange: selectA.Request.Exchange,
                        Platform: ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformA),
                        TradeHwnd: selectA.Request.TradeHwnd,
                        Ticket: selectA.Request.Ticket,
                        Action: TradeLegAction.Close,
                        RowIndex: selectA.Request.RowIndex,
                        TradeMapName: selectA.TradeMapName),
                LegB: selectB.Request is null
                    ? null
                    : new TradeCloseLegRequest(
                        Exchange: selectB.Request.Exchange,
                        Platform: ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformB),
                        TradeHwnd: selectB.Request.TradeHwnd,
                        Ticket: selectB.Request.Ticket,
                        Action: TradeLegAction.Close,
                        RowIndex: selectB.Request.RowIndex,
                        TradeMapName: selectB.TradeMapName),
                Context: CreateExecutionContext(
                    TradeExecutionReason.ManualClose,
                    "manual-close-legacy")));

        if (result.IsDispatchBlocked)
        {
            RemovePendingCloseRequests(appCloseRequestRawMs);
            SafeVmLog($"[TRADE_GATE][BLOCKED] manual CLOSE remainingMs={result.GateRemainingMilliseconds}");
            ShowManualTradeFeedback("CLOSE", result);
            return;
        }

        NotifyOpenCloseFailures("CLOSE", result, ResolvePairIdForClose(selectA.Request?.Ticket ?? selectB.Request?.Ticket, slot, appCloseRequestRawMs, isAutoFlow: false));

        // Phase 1 Manual Close log
        var now = DateTime.Now;
        var displayStt = ResolveDisplayStt(
            ResolvePairIdForClose(selectA.Request?.Ticket ?? selectB.Request?.Ticket, slot, appCloseRequestRawMs, isAutoFlow: false),
            slot);
        if (selectA.TradeType.HasValue)
        {
            var isBuyA = selectA.TradeType.Value == 0;
            var typeA = SignalLogFormatter.TradeTypeString(selectA.TradeType.Value);
            var symbolA = selectA.Symbol ?? "-";
            var closePriceA = SignalLogFormatter.ResolveClosePrice(snapshot?.ExchangeA.Bid, snapshot?.ExchangeA.Ask, isBuyA);
            SignalLogItems.Insert(0, SignalLogFormatter.FormatManualClose(now, displayStt, "A", typeA, symbolA, closePriceA));
        }

        if (selectB.TradeType.HasValue)
        {
            var isBuyB = selectB.TradeType.Value == 0;
            var typeB = SignalLogFormatter.TradeTypeString(selectB.TradeType.Value);
            var symbolB = selectB.Symbol ?? "-";
            var closePriceB = SignalLogFormatter.ResolveClosePrice(snapshot?.ExchangeB.Bid, snapshot?.ExchangeB.Ask, isBuyB);
            SignalLogItems.Insert(0, SignalLogFormatter.FormatManualClose(now, displayStt, "B", typeB, symbolB, closePriceB));
        }

        AppendCloseSelectionDiagnostics(selectA, selectB);
        ShowManualTradeFeedback("CLOSE", result);
        SyncTradingFlowWithLivePairState(GetLivePairTradeStateStrict());
    }

    // Đóng tay 1 cặp đang chạy theo pairId — đường RIÊNG, coordinator-safe, ĐỘC LẬP với auto flow.
    // KHÔNG đụng _activeAutoCycle / _activeAutoCloseRecoveryCycle / _autoSlot. Chỉ chạm coordinator
    // qua MarkSlotCloseTriggered (dispatch) + CloseSlotManually (finalize, qua polling).
    private async Task ManualClosePairBySlotAsync(string pairId)
    {
        SafeVmLog($"[CYCLE][INFO] Manual close requested: pairId={pairId}");
        var closeClaimed = false;
        var dispatchStarted = false;
        var appCloseRequestRawMs = 0L;

        if (string.IsNullOrWhiteSpace(pairId))
        {
            SafeVmLog("[VM][WARN] Manual close skipped: empty pairId (row chưa resolve pairId)");
            return;
        }

        if (ShouldSkipTradeOp($"manual-close pairId={pairId}"))
        {
            return;
        }

        var slot = _portfolioCoordinator.GetSlotByPairId(pairId);
        if (slot is null || slot.TicketA is not { } ticketA || slot.TicketB is not { } ticketB)
        {
            SafeVmLog($"[VM][WARN] Manual close skipped: no live pair for pairId={pairId}");
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Đóng tay bỏ qua: không tìm thấy cặp đang chạy ({pairId})"));
            return;
        }

        if (slot.Status == PositionSlotStatus.PendingClose)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Đóng tay bỏ qua: cặp {pairId} đang đóng"));
            return;
        }

        if (slot.Status != PositionSlotStatus.Live)
        {
            SafeVmLog($"[VM][WARN] Manual close skipped: slot status={slot.Status} pairId={pairId}");
            return;
        }

        // Chặn nếu một close khác (auto hoặc manual) đang in-flight — dùng chung lock với auto close.
        if (Interlocked.CompareExchange(ref _closeDispatchInFlight, 1, 0) != 0)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Đóng tay bị chặn: đang có close khác in-flight"));
            return;
        }

        try
        {
            var selectA = SelectCloseCandidateForTicket(
                targetTicket: ticketA,
                exchangeLabel: "A",
                tradeMapName: TradeTab.LeftPanel.TargetMapName,
                tradeHwnd: _runtimeConfigState.CurrentTradeHwndA);
            var selectB = SelectCloseCandidateForTicket(
                targetTicket: ticketB,
                exchangeLabel: "B",
                tradeMapName: TradeTab.RightPanel.TargetMapName,
                tradeHwnd: _runtimeConfigState.CurrentTradeHwndB);

            if (selectA.Request is null || selectB.Request is null)
            {
                SafeVmLog(
                    $"[VM][WARN] Manual close aborted: leg unresolved pairId={pairId} " +
                    $"A={selectA.Status} B={selectB.Status}");
                AppendCloseSelectionDiagnostics(selectA, selectB);
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    SignalLogItems.Insert(0,
                        $"    - [{DateTime.Now:HH:mm:ss.fff}] Đóng tay bỏ qua: không resolve được leg ({pairId})"));
                return;
            }

            var snapshot = _runtimeConfigState.CurrentDashboardMetrics;
            var appCloseRequestTimeLocal = DateTimeOffset.Now;
            appCloseRequestRawMs = Environment.TickCount64;

            closeClaimed = _portfolioCoordinator.TryClaimSlotClose(
                slot.PairId,
                CloseExecutionOwner.Manual,
                DateTime.UtcNow);
            if (!closeClaimed)
            {
                SafeVmLog($"[VM][WARN] Manual close skipped: pair already claimed pairId={pairId}");
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    SignalLogItems.Insert(0,
                        $"    - [{DateTime.Now:HH:mm:ss.fff}] Đóng tay bị chặn: cặp {pairId} đã được luồng khác nhận xử lý"));
                return;
            }
            RaiseNonAutoCloseUiStateChanged();

            // Capture pending BEFORE dispatch (chống race với polling). isAutoFlow=false.
            CapturePendingCloseRequest(
                selectA,
                snapshot,
                isExchangeA: true,
                appCloseRequestTimeLocal,
                appCloseRequestRawMs,
                slot.SlotId,
                pairIdOverride: slot.PairId);
            CapturePendingCloseRequest(
                selectB,
                snapshot,
                isExchangeA: false,
                appCloseRequestTimeLocal,
                appCloseRequestRawMs,
                slot.SlotId,
                pairIdOverride: slot.PairId);

            // Manual per-pair có lifecycle riêng; retry giữ nguyên origin này.
            if (_pendingClosePairById.TryGetValue(slot.PairId, out var pendingState))
            {
                pendingState.Origin = PendingCloseOrigin.ManualPair;
            }

            dispatchStarted = true;
            var result = await _tradeExecutionRouter.ClosePairAsync(
                new TradeClosePairRequest(
                    LegA: new TradeCloseLegRequest(
                        Exchange: selectA.Request.Exchange,
                        Platform: ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformA),
                        TradeHwnd: selectA.Request.TradeHwnd,
                        Ticket: selectA.Request.Ticket,
                        Action: TradeLegAction.Close,
                        RowIndex: selectA.Request.RowIndex,
                        TradeMapName: selectA.TradeMapName),
                    LegB: new TradeCloseLegRequest(
                        Exchange: selectB.Request.Exchange,
                        Platform: ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformB),
                        TradeHwnd: selectB.Request.TradeHwnd,
                        Ticket: selectB.Request.Ticket,
                        Action: TradeLegAction.Close,
                        RowIndex: selectB.Request.RowIndex,
                        TradeMapName: selectB.TradeMapName),
                    Context: CreateExecutionContext(
                        TradeExecutionReason.ManualPairClose,
                        "manual-close-slot",
                        slot.PairId,
                        slot.SlotId)));

            NotifyOpenCloseFailures("CLOSE", result, slot.PairId);
            if (result.IsDispatchBlocked)
            {
                _portfolioCoordinator.AbortPendingClose(slot.PairId);
                RaiseNonAutoCloseUiStateChanged();
                closeClaimed = false;
                RemovePendingCloseRequests(appCloseRequestRawMs);
                if (_pendingClosePairById.TryGetValue(slot.PairId, out var blockedPending))
                {
                    blockedPending.IsResolved = true;
                }
                SafeVmLog($"[TRADE_GATE][BLOCKED] manual CLOSE pairId={slot.PairId} remainingMs={result.GateRemainingMilliseconds}");
                return;
            }
            AppendCloseSelectionDiagnostics(selectA, selectB);
            ShowManualTradeFeedback("CLOSE", result);
            SafeVmLog($"[CYCLE][INFO] Manual close dispatched: pairId={slot.PairId} slot={slot.SlotId} success={result.Success}");
        }
        catch (Exception ex)
        {
            if (closeClaimed && !dispatchStarted)
            {
                _portfolioCoordinator.AbortPendingClose(pairId);
                RaiseNonAutoCloseUiStateChanged();
                if (appCloseRequestRawMs != 0)
                {
                    RemovePendingCloseRequests(appCloseRequestRawMs);
                }
            }
            SafeVmLog($"[VM][ERROR] Manual close error pairId={pairId}: {ex}");
        }
        finally
        {
            Interlocked.Exchange(ref _closeDispatchInFlight, 0);
        }
    }

    private async Task DispatchSignalTradeAsync(GapSignalTriggerResult trigger)
    {
        try
        {
            if (trigger.Action == GapSignalAction.Open)
            {
                if (trigger.PrimarySide == GapSignalSide.Buy)
                {
                    await AutoBuyAsync(trigger);
                }
                else
                {
                    await AutoSellAsync(trigger);
                }
            }
            else if (trigger.Action == GapSignalAction.Close)
            {
                await AutoCloseOrderAsync(trigger);
            }
        }
        catch (Exception ex)
        {
            SafeVmLog($"[VM][ERROR] Auto trade error: {ex}");
            if (trigger.Action == GapSignalAction.Close)
            {
                _tradingFlowEngine.AbortPendingCloseExecution();
                LogFlowTransitionIfChanged("close-aborted-by-exception");
            }
            else if (trigger.Action == GapSignalAction.Open)
            {
                // Open execution failed before confirmation -> rollback transient WaitingClose state.
                _tradingFlowEngine.AbortPendingOpenExecution();
                LogFlowTransitionIfChanged("open-aborted-by-exception");
            }

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                RaiseCurrentPositionTextChanged();
                OnPropertyChanged(nameof(CurrentPhaseText));
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Auto trade error: {ex.Message}");
            });
        }
    }

    /// <summary>
    /// Phase 1 entry point cho open dispatch. Tách khỏi DispatchSignalTradeAsync để
    /// có chỗ xử lý slot allocation. AutoBuyAsync/AutoSellAsync giữ signature cũ;
    /// pairId vẫn build từ _autoSlot bên trong các method đó cho backward compat.
    /// Coordinator slot allocation xảy ra inside AutoBuyAsync/AutoSellAsync sau khi
    /// build pairId — đảm bảo pairId của ViewModel và coordinator slot match.
    /// </summary>
    private Task DispatchOpenTriggerAsync(GapSignalTriggerResult trigger)
    {
        if (ShouldSkipTradeOp("auto-open"))
        {
            return Task.CompletedTask;
        }

        return DispatchSignalTradeAsync(trigger);
    }

    /// <summary>
    /// Phase 4: close dispatch uses slot.TicketA/B for ticket-precise RowIndex lookup.
    /// Slot already marked PendingClose by coordinator.ProcessSnapshot.
    /// </summary>
    private async Task DispatchCloseTriggerAsync(PositionSlot targetSlot, GapSignalTriggerResult trigger)
    {
        if (ShouldSkipTradeOp("auto-close"))
        {
            return;
        }

        try
        {
            await AutoCloseOrderAsync(trigger, targetSlot);
        }
        catch (Exception ex)
        {
            SafeVmLog($"[VM][ERROR] Auto close error: {ex}");
            _tradingFlowEngine.AbortPendingCloseExecution();
            LogFlowTransitionIfChanged("close-aborted-by-exception");
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                RaiseCurrentPositionTextChanged();
                OnPropertyChanged(nameof(CurrentPhaseText));
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Auto close error: {ex.Message}");
            });
        }
    }

    private async Task AutoBuyAsync(GapSignalTriggerResult trigger)
    {
        if (_isAutoOpenPausedByInvariant)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Open blocked: invariant watchdog is active (auto-open paused)");
            });
            return;
        }

        if (!TryAllowOppositeOpenPriceGuard(requestedTradeType: 0, stage: "CHECK", pairId: null))
        {
            return;
        }

        // Phase 3: per-side in-flight lock — Buy không cản Sell và ngược lại.
        // Old global lock _autoOpenInFlight still kept for backward compat (e.g., Reset paths).
        if (Interlocked.CompareExchange(ref _autoOpenInFlightBuy, 1, 0) != 0
            || Interlocked.CompareExchange(ref _autoOpenInFlight, 1, 0) != 0)
        {
            // Release Buy-side flag if global failed to avoid stuck state.
            Interlocked.Exchange(ref _autoOpenInFlightBuy, 0);
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Open blocked: another auto-open is in flight");
            });
            return;
        }

        var slot = _autoSlot;
        var appOpenRequestRawMs = Environment.TickCount64;
        var pairId = BuildPairId(slot, appOpenRequestRawMs, isAutoFlow: true);

        try
        {
            var (_, hwndColumn) = _runtimeConfigState.GetRandomManualHwndColumn();
            var appOpenRequestTimeLocal = DateTimeOffset.Now;

            if (HasUnresolvedAutoPendingOpenCycle(out var blockingPairId))
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    SignalLogItems.Insert(0,
                        $"    - [{DateTime.Now:HH:mm:ss.fff}] Open blocked: pending auto cycle " +
                        $"{blockingPairId} not yet fully confirmed (waiting MMF for both legs)");
                });
                return;
            }

            // Phase 3: per-side debounce.
            if (_lastAutoOpenBuyAtLocal.HasValue
                && (DateTimeOffset.Now - _lastAutoOpenBuyAtLocal.Value).TotalMilliseconds < AutoOpenDebounceMs
                && !HasAnyToolOpenedTicketInLatestResults())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    SignalLogItems.Insert(0,
                        $"    - [{DateTime.Now:HH:mm:ss.fff}] Open Buy blocked: debounce window " +
                        $"({AutoOpenDebounceMs}ms) since last Buy click at {_lastAutoOpenBuyAtLocal.Value:HH:mm:ss.fff}");
                });
                return;
            }

            BeginActiveAutoCycle(slot, appOpenRequestTimeLocal, appOpenRequestRawMs);

            _openConfirmBySlot.Remove(slot);

            // Capture pending request BEFORE executing click to avoid race with shared-memory polling.
            // Keep this before debounce timestamp assignment as defensive ordering.
            CapturePendingOpenRequestFromTrigger(TradeTab.LeftPanel.TargetMapName, trigger, isExchangeA: true, tradeType: 0, appOpenRequestTimeLocal, appOpenRequestRawMs, slot);
            CapturePendingOpenRequestFromTrigger(TradeTab.RightPanel.TargetMapName, trigger, isExchangeA: false, tradeType: 1, appOpenRequestTimeLocal, appOpenRequestRawMs, slot);

            // Phase 1: allocate coordinator slot AFTER pending captured. If quota full, abort.
            var coordinatorSlot = _portfolioCoordinator.AllocatePendingOpenSlot(pairId, trigger);
            if (coordinatorSlot is null)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    SignalLogItems.Insert(0,
                        $"    - [{DateTime.Now:HH:mm:ss.fff}] Open blocked: portfolio quota full");
                });
                return;
            }
            SafeVmLog($"[SLOT][INFO] Slot {coordinatorSlot.SlotId} allocated: pairId={pairId} side=Buy mode=GapBuy");

            _lastAutoOpenClickAtLocal = DateTimeOffset.Now;
            _lastAutoOpenBuyAtLocal = DateTimeOffset.Now;

            var delayOpenAMs = Math.Max(0, _runtimeConfigState.CurrentDelayOpenAMs);
            var delayOpenBMs = Math.Max(0, _runtimeConfigState.CurrentDelayOpenBMs);
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Time delay open A {delayOpenAMs} ms khi open ở sàn A");
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Time delay open B {delayOpenBMs} ms khi open ở sàn B");
            });

            var openResult = await _tradeExecutionRouter.OpenPairAsync(
                new TradeOpenPairRequest(
                    new TradeOpenLegRequest(
                        Exchange: "A",
                        Platform: ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformA),
                        ChartHwnd: hwndColumn.ChartHwndA,
                        Action: TradeLegAction.Buy,
                        DelayMs: delayOpenAMs),
                    new TradeOpenLegRequest(
                        Exchange: "B",
                        Platform: ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformB),
                        ChartHwnd: hwndColumn.ChartHwndB,
                        Action: TradeLegAction.Sell,
                        DelayMs: delayOpenBMs),
                    Context: CreateStrategicContext(
                        TradeExecutionReason.StrategicOpen,
                        "auto-gap-buy",
                        trigger,
                        pairId,
                        coordinatorSlot.SlotId)));

            NotifyOpenCloseFailures("OPEN", openResult, pairId);

            if (!openResult.Success && openResult.Legs.All(x => !x.Success))
            {
                _lastAutoOpenClickAtLocal = null;
                if (_pendingOpenPairById.TryGetValue(pairId, out var state))
                {
                    state.IsResolved = true;
                }
                _portfolioCoordinator.AbortPendingOpen(pairId);
            }
            if (openResult.IsDispatchBlocked)
            {
                RemovePendingOpenRequests(appOpenRequestRawMs);
                if (_pendingOpenPairById.TryGetValue(pairId, out var blockedState))
                {
                    blockedState.IsResolved = true;
                }
                _portfolioCoordinator.AbortPendingOpen(pairId);
                SafeVmLog($"[TRADE_GATE][BLOCKED] OPEN pairId={pairId} remainingMs={openResult.GateRemainingMilliseconds}");
                return;
            }

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // Phase 1 Auto Open log: A buy, B sell — trigger is OpenByGapBuy
                var now = DateTime.Now;
                var symbolA = _runtimeConfigState.CurrentDashboardMetrics?.ExchangeA.Symbol ?? "-";
                var symbolB = _runtimeConfigState.CurrentDashboardMetrics?.ExchangeB.Symbol ?? "-";
                var priceA = trigger.LastAAsk;
                var priceB = trigger.LastBBid;
                var triggerGapLabel = "Gap BUY";
                var triggerLastGap = trigger.LastBuyGap;
                var triggerAllGaps = trigger.BuyGaps;
                var spreadText = BuildSpreadPtsText(_runtimeConfigState.CurrentDashboardMetrics);
                var displayStt = ResolveDisplayStt(pairId, slot);
                SignalLogItems.Insert(0, SignalLogFormatter.FormatAutoOpen(now, displayStt, "B", "SELL", symbolB, priceB, triggerGapLabel, triggerLastGap, triggerAllGaps, spreadText));
                SignalLogItems.Insert(0, SignalLogFormatter.FormatAutoOpen(now, displayStt, "A", "BUY", symbolA, priceA, triggerGapLabel, triggerLastGap, triggerAllGaps, spreadText));
                _autoSlot++;
            });
        }
        catch (Exception ex)
        {
            SafeVmLog($"[VM][ERROR] AutoBuyAsync cleanup after exception: {ex}");
            // Cleanup when router throws to avoid stale pending cycle blocking next opens.
            _lastAutoOpenClickAtLocal = null;
            if (_pendingOpenPairById.TryGetValue(pairId, out var state))
            {
                state.IsResolved = true;
            }
            _portfolioCoordinator.AbortPendingOpen(pairId);

            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _autoOpenInFlight, 0);
            Interlocked.Exchange(ref _autoOpenInFlightBuy, 0);
        }
    }

    private async Task AutoSellAsync(GapSignalTriggerResult trigger)
    {
        if (_isAutoOpenPausedByInvariant)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Open blocked: invariant watchdog is active (auto-open paused)");
            });
            return;
        }

        if (!TryAllowOppositeOpenPriceGuard(requestedTradeType: 1, stage: "CHECK", pairId: null))
        {
            return;
        }

        // Phase 3: per-side in-flight lock — Sell không cản Buy và ngược lại.
        if (Interlocked.CompareExchange(ref _autoOpenInFlightSell, 1, 0) != 0
            || Interlocked.CompareExchange(ref _autoOpenInFlight, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _autoOpenInFlightSell, 0);
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Open blocked: another auto-open is in flight");
            });
            return;
        }

        var slot = _autoSlot;
        var appOpenRequestRawMs = Environment.TickCount64;
        var pairId = BuildPairId(slot, appOpenRequestRawMs, isAutoFlow: true);

        try
        {
            var (_, hwndColumn) = _runtimeConfigState.GetRandomManualHwndColumn();
            var appOpenRequestTimeLocal = DateTimeOffset.Now;

            if (HasUnresolvedAutoPendingOpenCycle(out var blockingPairId))
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    SignalLogItems.Insert(0,
                        $"    - [{DateTime.Now:HH:mm:ss.fff}] Open blocked: pending auto cycle " +
                        $"{blockingPairId} not yet fully confirmed (waiting MMF for both legs)");
                });
                return;
            }

            // Phase 3: per-side debounce.
            if (_lastAutoOpenSellAtLocal.HasValue
                && (DateTimeOffset.Now - _lastAutoOpenSellAtLocal.Value).TotalMilliseconds < AutoOpenDebounceMs
                && !HasAnyToolOpenedTicketInLatestResults())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    SignalLogItems.Insert(0,
                        $"    - [{DateTime.Now:HH:mm:ss.fff}] Open Sell blocked: debounce window " +
                        $"({AutoOpenDebounceMs}ms) since last Sell click at {_lastAutoOpenSellAtLocal.Value:HH:mm:ss.fff}");
                });
                return;
            }

            BeginActiveAutoCycle(slot, appOpenRequestTimeLocal, appOpenRequestRawMs);

            _openConfirmBySlot.Remove(slot);

            // Capture pending request BEFORE executing click to avoid race with shared-memory polling.
            // Keep this before debounce timestamp assignment as defensive ordering.
            CapturePendingOpenRequestFromTrigger(TradeTab.LeftPanel.TargetMapName, trigger, isExchangeA: true, tradeType: 1, appOpenRequestTimeLocal, appOpenRequestRawMs, slot);
            CapturePendingOpenRequestFromTrigger(TradeTab.RightPanel.TargetMapName, trigger, isExchangeA: false, tradeType: 0, appOpenRequestTimeLocal, appOpenRequestRawMs, slot);

            // Phase 1: allocate coordinator slot AFTER pending captured. If quota full, abort.
            var coordinatorSlot = _portfolioCoordinator.AllocatePendingOpenSlot(pairId, trigger);
            if (coordinatorSlot is null)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    SignalLogItems.Insert(0,
                        $"    - [{DateTime.Now:HH:mm:ss.fff}] Open blocked: portfolio quota full");
                });
                return;
            }
            SafeVmLog($"[SLOT][INFO] Slot {coordinatorSlot.SlotId} allocated: pairId={pairId} side=Sell mode=GapSell");

            _lastAutoOpenClickAtLocal = DateTimeOffset.Now;
            _lastAutoOpenSellAtLocal = DateTimeOffset.Now;

            var delayOpenAMs = Math.Max(0, _runtimeConfigState.CurrentDelayOpenAMs);
            var delayOpenBMs = Math.Max(0, _runtimeConfigState.CurrentDelayOpenBMs);
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Time delay open A {delayOpenAMs} ms khi open ở sàn A");
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Time delay open B {delayOpenBMs} ms khi open ở sàn B");
            });

            var openResult = await _tradeExecutionRouter.OpenPairAsync(
                new TradeOpenPairRequest(
                    new TradeOpenLegRequest(
                        Exchange: "A",
                        Platform: ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformA),
                        ChartHwnd: hwndColumn.ChartHwndA,
                        Action: TradeLegAction.Sell,
                        DelayMs: delayOpenAMs),
                    new TradeOpenLegRequest(
                        Exchange: "B",
                        Platform: ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformB),
                        ChartHwnd: hwndColumn.ChartHwndB,
                        Action: TradeLegAction.Buy,
                        DelayMs: delayOpenBMs),
                    Context: CreateStrategicContext(
                        TradeExecutionReason.StrategicOpen,
                        "auto-gap-sell",
                        trigger,
                        pairId,
                        coordinatorSlot.SlotId)));

            NotifyOpenCloseFailures("OPEN", openResult, pairId);

            if (!openResult.Success && openResult.Legs.All(x => !x.Success))
            {
                _lastAutoOpenClickAtLocal = null;
                if (_pendingOpenPairById.TryGetValue(pairId, out var state))
                {
                    state.IsResolved = true;
                }
                _portfolioCoordinator.AbortPendingOpen(pairId);
            }
            if (openResult.IsDispatchBlocked)
            {
                RemovePendingOpenRequests(appOpenRequestRawMs);
                if (_pendingOpenPairById.TryGetValue(pairId, out var blockedState))
                {
                    blockedState.IsResolved = true;
                }
                _portfolioCoordinator.AbortPendingOpen(pairId);
                SafeVmLog($"[TRADE_GATE][BLOCKED] OPEN pairId={pairId} remainingMs={openResult.GateRemainingMilliseconds}");
                return;
            }

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // Phase 1 Auto Open log: A sell, B buy — trigger is OpenByGapSell
                var now = DateTime.Now;
                var symbolA = _runtimeConfigState.CurrentDashboardMetrics?.ExchangeA.Symbol ?? "-";
                var symbolB = _runtimeConfigState.CurrentDashboardMetrics?.ExchangeB.Symbol ?? "-";
                var priceA = trigger.LastABid;
                var priceB = trigger.LastBAsk;
                var triggerGapLabel = "Gap SELL";
                var triggerLastGap = trigger.LastSellGap;
                var triggerAllGaps = trigger.SellGaps;
                var spreadText = BuildSpreadPtsText(_runtimeConfigState.CurrentDashboardMetrics);
                var displayStt = ResolveDisplayStt(pairId, slot);
                SignalLogItems.Insert(0, SignalLogFormatter.FormatAutoOpen(now, displayStt, "B", "BUY", symbolB, priceB, triggerGapLabel, triggerLastGap, triggerAllGaps, spreadText));
                SignalLogItems.Insert(0, SignalLogFormatter.FormatAutoOpen(now, displayStt, "A", "SELL", symbolA, priceA, triggerGapLabel, triggerLastGap, triggerAllGaps, spreadText));
                _autoSlot++;
            });
        }
        catch (Exception ex)
        {
            SafeVmLog($"[VM][ERROR] AutoSellAsync cleanup after exception: {ex}");
            // Cleanup when router throws to avoid stale pending cycle blocking next opens.
            _lastAutoOpenClickAtLocal = null;
            if (_pendingOpenPairById.TryGetValue(pairId, out var state))
            {
                state.IsResolved = true;
            }
            _portfolioCoordinator.AbortPendingOpen(pairId);

            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _autoOpenInFlight, 0);
            Interlocked.Exchange(ref _autoOpenInFlightSell, 0);
        }
    }

    /// <summary>
    /// Phase 4: AutoCloseOrderAsync now accepts an optional targetSlot. When provided,
    /// uses ticket-precise selection via SelectCloseCandidateForTicket. Otherwise falls
    /// back to legacy first-tool-opened (used by manual close + recovery).
    /// </summary>
    private async Task AutoCloseOrderAsync(GapSignalTriggerResult trigger, PositionSlot? targetSlot = null)
    {
        if (Interlocked.CompareExchange(ref _closeDispatchInFlight, 1, 0) != 0)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Close blocked: another auto-close is in flight");
            });
            return;
        }

        try
        {
            var slot = Math.Max(0, _autoSlot - 1);
            _closeConfirmBySlot.Remove(slot);

            // Phase 4: if coordinator provided a specific slot, lookup by ticket.
            // Otherwise legacy first-tool-opened picker.
            CloseSelectionResult selectA;
            CloseSelectionResult selectB;
            if (targetSlot?.TicketA is { } ticketA && targetSlot?.TicketB is { } ticketB)
            {
                selectA = SelectCloseCandidateForTicket(
                    targetTicket: ticketA,
                    exchangeLabel: "A",
                    tradeMapName: TradeTab.LeftPanel.TargetMapName,
                    tradeHwnd: _runtimeConfigState.CurrentTradeHwndA);
                selectB = SelectCloseCandidateForTicket(
                    targetTicket: ticketB,
                    exchangeLabel: "B",
                    tradeMapName: TradeTab.RightPanel.TargetMapName,
                    tradeHwnd: _runtimeConfigState.CurrentTradeHwndB);
                SafeVmLog(
                    $"[CLOSE_SELECT][INFO] Slot {targetSlot!.SlotId} resolved: " +
                    $"A.row={selectA.Request?.RowIndex} ticket={ticketA}, " +
                    $"B.row={selectB.Request?.RowIndex} ticket={ticketB}");
            }
            else
            {
                selectA = SelectCloseCandidateForExchange(
                    exchangeLabel: "A",
                    tradeMapName: TradeTab.LeftPanel.TargetMapName,
                    tradeHwnd: _runtimeConfigState.CurrentTradeHwndA);
                selectB = SelectCloseCandidateForExchange(
                    exchangeLabel: "B",
                    tradeMapName: TradeTab.RightPanel.TargetMapName,
                    tradeHwnd: _runtimeConfigState.CurrentTradeHwndB);
            }

            var appCloseRequestTimeLocal = DateTimeOffset.Now;
            var appCloseRequestRawMs = Environment.TickCount64;
            _activeAutoCloseRecoveryCycle = BuildActiveCloseRecoveryCycle(slot, selectA, selectB);
            var delayCloseAMs = Math.Max(0, _runtimeConfigState.CurrentDelayCloseAMs);
            var delayCloseBMs = Math.Max(0, _runtimeConfigState.CurrentDelayCloseBMs);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Time delay close A {delayCloseAMs} ms khi close ở sàn A");
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Time delay close B {delayCloseBMs} ms khi close ở sàn B");
            });

            // Capture pending request BEFORE executing close to avoid race with shared-memory polling.
            CapturePendingCloseRequestFromTrigger(selectA, trigger, isExchangeA: true, appCloseRequestTimeLocal, appCloseRequestRawMs, slot);
            CapturePendingCloseRequestFromTrigger(selectB, trigger, isExchangeA: false, appCloseRequestTimeLocal, appCloseRequestRawMs, slot);

            if (targetSlot is not null)
            {
                _portfolioCoordinator.MarkSlotCloseTriggered(targetSlot.PairId, DateTime.UtcNow);
            }

            var closeResult = await _tradeExecutionRouter.ClosePairAsync(
                new TradeClosePairRequest(
                    LegA: selectA.Request is null
                        ? null
                        : new TradeCloseLegRequest(
                            Exchange: selectA.Request.Exchange,
                            Platform: ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformA),
                            TradeHwnd: selectA.Request.TradeHwnd,
                            Ticket: selectA.Request.Ticket,
                            Action: TradeLegAction.Close,
                            DelayMs: delayCloseAMs,
                            RowIndex: selectA.Request.RowIndex,
                            TradeMapName: selectA.TradeMapName),
                    LegB: selectB.Request is null
                        ? null
                        : new TradeCloseLegRequest(
                            Exchange: selectB.Request.Exchange,
                            Platform: ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformB),
                            TradeHwnd: selectB.Request.TradeHwnd,
                            Ticket: selectB.Request.Ticket,
                            Action: TradeLegAction.Close,
                            DelayMs: delayCloseBMs,
                            RowIndex: selectB.Request.RowIndex,
                            TradeMapName: selectB.TradeMapName),
                    Context: CreateStrategicContext(
                        TradeExecutionReason.StrategicClose,
                        "auto-close",
                        trigger,
                        targetSlot?.PairId,
                        targetSlot?.SlotId)));

            if (closeResult.IsDispatchBlocked)
            {
                RemovePendingCloseRequests(appCloseRequestRawMs);
                if (targetSlot is not null)
                {
                    _portfolioCoordinator.AbortPendingClose(targetSlot.PairId);
                    if (_pendingClosePairById.TryGetValue(targetSlot.PairId, out var pending))
                    {
                        pending.IsResolved = true;
                    }
                }
                _activeAutoCloseRecoveryCycle = null;
                SafeVmLog($"[TRADE_GATE][BLOCKED] CLOSE remainingMs={closeResult.GateRemainingMilliseconds}");
                return;
            }

            NotifyOpenCloseFailures("CLOSE", closeResult, ResolvePairIdFromCloseSelection(selectA) ?? ResolvePairIdFromCloseSelection(selectB) ?? $"AUTO-{slot:D4}-{appCloseRequestRawMs}");

            var hadCloseCandidateA = selectA.Request is not null;
            var hadCloseCandidateB = selectB.Request is not null;
            var hadCloseCandidateBoth = hadCloseCandidateA && hadCloseCandidateB;

            var successByExchange = closeResult.Legs
                .GroupBy(x => x.Exchange, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Any(x => x.Success), StringComparer.OrdinalIgnoreCase);

            var closeSuccessA = !hadCloseCandidateA
                || (successByExchange.TryGetValue("A", out var successA) && successA);
            var closeSuccessB = !hadCloseCandidateB
                || (successByExchange.TryGetValue("B", out var successB) && successB);
            var hasCloseSuccessBoth = hadCloseCandidateBoth && closeSuccessA && closeSuccessB;

            var reconcileTradeLeft = ReadTradesWithMmfLog(TradeTab.LeftPanel.TargetMapName);
            var reconcileTradeRight = ReadTradesWithMmfLog(TradeTab.RightPanel.TargetMapName);
            var pairState = GetLivePairTradeState(reconcileTradeLeft, reconcileTradeRight);
            var immediateFlatObserved = false;

            if (pairState == LivePairTradeState.BothFlat
                && IsActiveCloseRecoveryCycleClosed(reconcileTradeLeft, reconcileTradeRight, out _))
            {
                // Hardening: do NOT finalize close immediately from a single MMF read.
                // Prime streak and wait polling to confirm stable BothFlat.
                _closeBothFlatPollStreak = Math.Max(_closeBothFlatPollStreak, 1);
                immediateFlatObserved = true;
            }
            else
            {
                _closeBothFlatPollStreak = 0;
            }

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // Phase 1 Auto Close log
                var now = DateTime.Now;
                var isCloseBuy = trigger.TriggerType is GapSignalTriggerType.CloseByGapBuy;
                var triggerGapLabel = isCloseBuy ? "Gap BUY" : "Gap SELL";
                var triggerLastGap = isCloseBuy ? trigger.LastBuyGap : trigger.LastSellGap;
                var triggerAllGaps = isCloseBuy ? trigger.BuyGaps : trigger.SellGaps;

                if (selectA.TradeType.HasValue
                    && closeSuccessA)
                {
                    var isBuyA = selectA.TradeType.Value == 0;
                    var typeA = SignalLogFormatter.TradeTypeString(selectA.TradeType.Value);
                    var symbolA = selectA.Symbol ?? "-";
                    var closePriceA = SignalLogFormatter.ResolveClosePrice(
                        _runtimeConfigState.CurrentDashboardMetrics?.ExchangeA.Bid,
                        _runtimeConfigState.CurrentDashboardMetrics?.ExchangeA.Ask,
                        isBuyA);
                    SignalLogItems.Insert(0, SignalLogFormatter.FormatAutoClose(
                        now,
                        ResolveDisplayStt(targetSlot?.PairId, slot),
                        "A",
                        typeA,
                        symbolA,
                        closePriceA,
                        triggerGapLabel,
                        triggerLastGap,
                        triggerAllGaps,
                        trigger.CloseReason,
                        trigger.CloseTpProfit,
                        trigger.CloseTpTarget,
                        trigger.CloseTpProfits));
                }

                if (selectB.TradeType.HasValue
                    && closeSuccessB)
                {
                    var isBuyB = selectB.TradeType.Value == 0;
                    var typeB = SignalLogFormatter.TradeTypeString(selectB.TradeType.Value);
                    var symbolB = selectB.Symbol ?? "-";
                    var closePriceB = SignalLogFormatter.ResolveClosePrice(
                        _runtimeConfigState.CurrentDashboardMetrics?.ExchangeB.Bid,
                        _runtimeConfigState.CurrentDashboardMetrics?.ExchangeB.Ask,
                        isBuyB);
                    SignalLogItems.Insert(0, SignalLogFormatter.FormatAutoClose(
                        now,
                        ResolveDisplayStt(targetSlot?.PairId, slot),
                        "B",
                        typeB,
                        symbolB,
                        closePriceB,
                        triggerGapLabel,
                        triggerLastGap,
                        triggerAllGaps,
                        trigger.CloseReason,
                        trigger.CloseTpProfit,
                        trigger.CloseTpTarget,
                        trigger.CloseTpProfits));
                }

                AppendCloseSelectionDiagnostics(selectA, selectB);

                LogFlowRecoveryState(
                    pairState,
                    context: "Auto close reconcile",
                    hadCloseCandidateA,
                    hadCloseCandidateB,
                    closeSuccessA,
                    closeSuccessB,
                    hasCloseSuccessBoth,
                    immediateFlatObserved);

                RaiseCurrentPositionTextChanged();
                OnPropertyChanged(nameof(CurrentPhaseText));
            });
        }
        finally
        {
            Interlocked.Exchange(ref _closeDispatchInFlight, 0);
        }
    }

    private void CapturePendingOpenRequestFromTrigger(
        string tradeMapName,
        GapSignalTriggerResult trigger,
        bool isExchangeA,
        int tradeType,
        DateTimeOffset appOpenRequestTimeLocal,
        long appOpenRequestRawMs,
        int slotNumber = 0)
    {
        var expectedPrice = ResolveExpectedPriceFromTrigger(trigger, isExchangeA, tradeType);
        RegisterPendingOpenRequest(
            tradeMapName: tradeMapName,
            symbol: null,
            volume: null,
            tradeType: tradeType,
            expectedPrice: expectedPrice,
            holdingSeconds: _tradingFlowEngine.CurrentHoldingSeconds,
            appOpenRequestTimeLocal: appOpenRequestTimeLocal,
            appOpenRequestRawMs: appOpenRequestRawMs,
            isAutoFlow: true,
            slotNumber: slotNumber,
            exchangeLabel: isExchangeA ? "A" : "B");
    }

    private void CapturePendingCloseRequestFromTrigger(
        CloseSelectionResult selection,
        GapSignalTriggerResult trigger,
        bool isExchangeA,
        DateTimeOffset appCloseRequestTimeLocal,
        long appCloseRequestRawMs,
        int slotNumber = 0)
    {
        if (selection.Request is null || !selection.TradeType.HasValue)
        {
            return;
        }

        var originalTradeType = selection.TradeType.Value;
        var closeTradeType = originalTradeType == 0 ? 1 : 0;
        var expectedPrice = ResolveExpectedPriceFromTrigger(trigger, isExchangeA, closeTradeType);
        RegisterPendingCloseRequest(
            tradeMapName: ResolveTradeMapNameFromCloseSelection(selection),
            ticket: selection.Request.Ticket,
            tradeType: originalTradeType,
            expectedPrice: expectedPrice,
            appCloseRequestTimeLocal: appCloseRequestTimeLocal,
            appCloseRequestRawMs: appCloseRequestRawMs,
            symbol: selection.Symbol,
            volume: selection.Volume,
            isAutoFlow: true,
            slotNumber: slotNumber,
            exchangeLabel: isExchangeA ? "A" : "B");
    }

    private static double? ResolveExpectedPriceFromTrigger(GapSignalTriggerResult trigger, bool isExchangeA, int tradeType)
    {
        var isBuy = tradeType == 0;

        // Standard forex: Buy at Ask, Sell at Bid
        if (isExchangeA)
        {
            if (isBuy)
            {
                return trigger.LastAAsk.HasValue ? (double)trigger.LastAAsk.Value : null;
            }

            return trigger.LastABid.HasValue ? (double)trigger.LastABid.Value : null;
        }

        if (isBuy)
        {
            return trigger.LastBAsk.HasValue ? (double)trigger.LastBAsk.Value : null;
        }

        return trigger.LastBBid.HasValue ? (double)trigger.LastBBid.Value : null;
    }

    /// <summary>
    /// Đọc SharedMemory và tìm row index của ticket cụ thể.
    /// Trả null nếu map không khả dụng hoặc ticket không tồn tại.
    /// Caller phải skip close khi nhận null — tuyệt đối không fallback về 0.
    /// </summary>
    private int? FindRowIndexForTicket(string tradeMapName, ulong ticket)
    {
        try
        {
            var result = ReadTradesWithMmfLog(tradeMapName);
            if (!result.IsMapAvailable || !result.IsParseSuccess)
            {
                return null;
            }

            for (var i = 0; i < result.Records.Count; i++)
            {
                if (result.Records[i].Ticket == ticket)
                {
                    return i;
                }
            }
        }
        catch (Exception ex)
        {
            // ignore and fallback to null
            SafeVmLog($"[VM][WARN] Suppressed exception at FindRowIndexForTicket: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Phase 4: Resolve current RowIndex of a SPECIFIC ticket in MMF.
    /// Multi-slot close routing must lookup by slot.TicketA/B, NOT pick "first tool-opened"
    /// which closes wrong slot when 2+ slots cùng side.
    /// </summary>
    private CloseSelectionResult SelectCloseCandidateForTicket(
        ulong targetTicket,
        string exchangeLabel,
        string tradeMapName,
        string tradeHwnd)
    {
        var result = ReadTradesWithMmfLog(tradeMapName);

        if (!result.IsMapAvailable)
        {
            return new CloseSelectionResult(
                Request: null, Status: CloseSelectionStatus.MapNotFound,
                TradeType: null, TradeMapName: tradeMapName, Symbol: null, Volume: null,
                DiagnosticMessage: $"Close {exchangeLabel} skipped: map not found ({tradeMapName})");
        }

        if (!result.IsParseSuccess)
        {
            return new CloseSelectionResult(
                Request: null, Status: CloseSelectionStatus.ParseError,
                TradeType: null, TradeMapName: tradeMapName, Symbol: null, Volume: null,
                DiagnosticMessage: $"Close {exchangeLabel} skipped: parse error ({result.ErrorMessage ?? "unknown"})");
        }

        // Find SPECIFIC ticket — do NOT fallback to first-tool-opened.
        for (var i = 0; i < result.Records.Count; i++)
        {
            if (result.Records[i].Ticket != targetTicket) continue;

            // Defense check: ticket must be tool-opened.
            if (!_pairIdByTicket.ContainsKey(result.Records[i].Ticket))
            {
                SafeVmLog(
                    $"[CLOSE_SELECT][WARN] Ticket {targetTicket} found at row {i} but not tool-opened — skipping");
                return new CloseSelectionResult(
                    Request: null, Status: CloseSelectionStatus.NoOpenTrade,
                    TradeType: null, TradeMapName: tradeMapName, Symbol: null, Volume: null,
                    DiagnosticMessage: $"Close {exchangeLabel} skipped: ticket {targetTicket} found but not tool-opened");
            }

            var trade = result.Records[i];
            return new CloseSelectionResult(
                Request: new ManualCloseRequest(exchangeLabel, tradeHwnd, trade.Ticket, i),
                Status: CloseSelectionStatus.Candidate,
                TradeType: trade.TradeType, TradeMapName: tradeMapName,
                Symbol: trade.Symbol, Volume: trade.Lot,
                DiagnosticMessage: null);
        }

        SafeVmLog(
            $"[CLOSE_SELECT][ERROR] Ticket {targetTicket} NOT found in {exchangeLabel} MMF " +
            $"(records={result.Records.Count}). Close will skip; signal may retry next tick.");
        return new CloseSelectionResult(
            Request: null, Status: CloseSelectionStatus.NoOpenTrade,
            TradeType: null, TradeMapName: tradeMapName, Symbol: null, Volume: null,
            DiagnosticMessage: $"Close {exchangeLabel} skipped: ticket {targetTicket} not found in MMF");
    }

    [Obsolete("Phase 4: prefer SelectCloseCandidateForTicket for multi-slot routing. Kept for manual close + recovery fallback.")]
    private CloseSelectionResult SelectCloseCandidateForExchange(string exchangeLabel, string tradeMapName, string tradeHwnd)
    {
        var result = ReadTradesWithMmfLog(tradeMapName);

        if (!result.IsMapAvailable)
        {
            return new CloseSelectionResult(
                Request: null,
                Status: CloseSelectionStatus.MapNotFound,
                TradeType: null,
                TradeMapName: tradeMapName,
                Symbol: null,
                Volume: null,
                DiagnosticMessage: $"Close {exchangeLabel} skipped: map not found ({tradeMapName})");
        }

        if (!result.IsParseSuccess)
        {
            return new CloseSelectionResult(
                Request: null,
                Status: CloseSelectionStatus.ParseError,
                TradeType: null,
                TradeMapName: tradeMapName,
                Symbol: null,
                Volume: null,
                DiagnosticMessage: $"Close {exchangeLabel} skipped: parse error ({result.ErrorMessage ?? "unknown"})");
        }

        if (result.Count <= 0 || result.Records.Count == 0)
        {
            return new CloseSelectionResult(
                Request: null,
                Status: CloseSelectionStatus.NoOpenTrade,
                TradeType: null,
                TradeMapName: tradeMapName,
                Symbol: null,
                Volume: null,
                DiagnosticMessage: null);
        }

        var firstTradeIndex = -1;
        for (var i = 0; i < result.Records.Count; i++)
        {
            if (_pairIdByTicket.ContainsKey(result.Records[i].Ticket))
            {
                firstTradeIndex = i;
                break;
            }
        }

        if (firstTradeIndex < 0)
        {
            return new CloseSelectionResult(
                Request: null,
                Status: CloseSelectionStatus.NoOpenTrade,
                TradeType: null,
                TradeMapName: tradeMapName,
                Symbol: null,
                Volume: null,
                DiagnosticMessage: $"Close {exchangeLabel} skipped: no tool-opened trade found " +
                                   $"({result.Records.Count} external trade(s) present, not opened via tool)");
        }

        var firstTrade = result.Records[firstTradeIndex];

        return new CloseSelectionResult(
            Request: new ManualCloseRequest(exchangeLabel, tradeHwnd, firstTrade.Ticket, firstTradeIndex),
            Status: CloseSelectionStatus.Candidate,
            TradeType: firstTrade.TradeType,
            TradeMapName: tradeMapName,
            Symbol: firstTrade.Symbol,
            Volume: firstTrade.Lot,
            DiagnosticMessage: null);
    }

    private void AppendCloseSelectionDiagnostics(CloseSelectionResult selectionA, CloseSelectionResult selectionB)
    {
        var now = DateTime.Now;

        if (!string.IsNullOrWhiteSpace(selectionA.DiagnosticMessage))
        {
            SignalLogItems.Insert(0, $"    - [{now:HH:mm:ss.fff}] {selectionA.DiagnosticMessage}");
        }

        if (!string.IsNullOrWhiteSpace(selectionB.DiagnosticMessage))
        {
            SignalLogItems.Insert(0, $"    - [{now:HH:mm:ss.fff}] {selectionB.DiagnosticMessage}");
        }
    }

    private enum CloseSelectionStatus
    {
        Candidate,
        NoOpenTrade,
        MapNotFound,
        ParseError
    }

    private sealed record CloseSelectionResult(
        ManualCloseRequest? Request,
        CloseSelectionStatus Status,
        int? TradeType,
        string? TradeMapName,
        string? Symbol,
        double? Volume,
        string? DiagnosticMessage);

    private void CapturePendingOpenRequest(
        string tradeMapName,
        DashboardMetrics? snapshot,
        bool isExchangeA,
        int tradeType,
        DateTimeOffset appOpenRequestTimeLocal,
        long appOpenRequestRawMs,
        int slotNumber = 0)
    {
        var expectedPrice = ResolveExpectedOpenPrice(snapshot, isExchangeA, tradeType);
        RegisterPendingOpenRequest(
            tradeMapName: tradeMapName,
            symbol: ResolveExpectedOpenSymbol(snapshot, isExchangeA),
            volume: null,
            tradeType: tradeType,
            expectedPrice: expectedPrice,
            appOpenRequestTimeLocal: appOpenRequestTimeLocal,
            appOpenRequestRawMs: appOpenRequestRawMs,
            holdingSeconds: 0,
            isAutoFlow: false,
            slotNumber: slotNumber,
            exchangeLabel: isExchangeA ? "A" : "B");
    }

    private void RemovePendingOpenRequests(long appOpenRequestRawMs)
    {
        foreach (var key in _pendingOpenRequestsByMap.Keys.ToList())
        {
            var list = _pendingOpenRequestsByMap[key];
            list.RemoveAll(x => x.AppOpenRequestRawMs == appOpenRequestRawMs);
            if (list.Count == 0)
            {
                _pendingOpenRequestsByMap.Remove(key);
            }
        }
    }

    private void RemovePendingCloseRequests(long appCloseRequestRawMs)
    {
        foreach (var key in _pendingCloseRequestsByMap.Keys.ToList())
        {
            var list = _pendingCloseRequestsByMap[key];
            list.RemoveAll(x => x.AppCloseRequestRawMs == appCloseRequestRawMs);
            if (list.Count == 0)
            {
                _pendingCloseRequestsByMap.Remove(key);
            }
        }
    }

    private void CapturePendingCloseRequest(
        CloseSelectionResult selection,
        DashboardMetrics? snapshot,
        bool isExchangeA,
        DateTimeOffset appCloseRequestTimeLocal,
        long appCloseRequestRawMs,
        int slotNumber = 0,
        string? pairIdOverride = null)
    {
        if (selection.Request is null || !selection.TradeType.HasValue)
        {
            return;
        }

        var expectedPrice = ResolveExpectedClosePrice(snapshot, isExchangeA, selection.TradeType.Value);

        RegisterPendingCloseRequest(
            tradeMapName: ResolveTradeMapNameFromCloseSelection(selection),
            ticket: selection.Request.Ticket,
            tradeType: selection.TradeType.Value,
            expectedPrice: expectedPrice,
            appCloseRequestTimeLocal: appCloseRequestTimeLocal,
            appCloseRequestRawMs: appCloseRequestRawMs,
            symbol: selection.Symbol,
            volume: selection.Volume,
            isAutoFlow: false,
            slotNumber: slotNumber,
            exchangeLabel: isExchangeA ? "A" : "B",
            pairIdOverride: pairIdOverride);
    }

    private void RegisterPendingOpenRequest(
        string tradeMapName,
        string? symbol,
        double? volume,
        int tradeType,
        double? expectedPrice,
        DateTimeOffset appOpenRequestTimeLocal,
        long appOpenRequestRawMs,
        int holdingSeconds,
        bool isAutoFlow,
        int slotNumber = 0,
        string exchangeLabel = "")
    {
        var key = NormalizeMapName(tradeMapName);
        if (!_pendingOpenRequestsByMap.TryGetValue(key, out var pendingList))
        {
            pendingList = [];
            _pendingOpenRequestsByMap[key] = pendingList;
        }

        var pending = new PendingOpenRequest(
            PairId: BuildPairId(slotNumber, appOpenRequestRawMs, isAutoFlow),
            TradeMapName: key,
            Symbol: symbol,
            TradeType: tradeType,
            Volume: volume,
            ExpectedPrice: expectedPrice,
            HoldingSeconds: holdingSeconds,
            AppOpenRequestTimeLocal: appOpenRequestTimeLocal,
            AppOpenRequestUnixMs: appOpenRequestTimeLocal.ToUnixTimeMilliseconds(),
            AppOpenRequestRawMs: appOpenRequestRawMs,
            IsAutoFlow: isAutoFlow,
            SlotNumber: slotNumber,
            ExchangeLabel: exchangeLabel);

        pendingList.Add(pending);
        RegisterOrUpdatePendingOpenPairState(pending);
        SafeVmLog(
            $"[CYCLE][INFO] Pending open captured: pairId={pending.PairId} slot={pending.SlotNumber} " +
            $"exchange={pending.ExchangeLabel} tradeType={pending.TradeType} map={pending.TradeMapName} " +
            $"requestTime={pending.AppOpenRequestTimeLocal:HH:mm:ss.fff}");
        Debug.WriteLine($"[ExecOpen][Capture] map={key}, type={tradeType}, slot={slotNumber}, label={exchangeLabel}, app_open_request_time={pending.AppOpenRequestTimeLocal:O}, app_open_request_raw_ms={pending.AppOpenRequestRawMs}");
    }

    private void RegisterOrUpdatePendingOpenPairState(PendingOpenRequest pending)
    {
        if (!_pendingOpenPairById.TryGetValue(pending.PairId, out var state))
        {
            state = new PendingOpenPairState
            {
                PairId = pending.PairId,
                IsAutoFlow = pending.IsAutoFlow,
                SlotNumber = pending.SlotNumber,
                CreatedAtLocal = pending.AppOpenRequestTimeLocal,
                OpenPendingTimeoutMs = Math.Max(0, _runtimeConfigState.CurrentOpenPendingTimeMs)
            };
            _pendingOpenPairById[pending.PairId] = state;
        }

        state.OpenPendingTimeoutMs = Math.Max(0, _runtimeConfigState.CurrentOpenPendingTimeMs);

        if (string.Equals(pending.ExchangeLabel, "A", StringComparison.OrdinalIgnoreCase))
        {
            state.TradeMapNameA = pending.TradeMapName;
            state.TradeTypeA = pending.TradeType;
            state.SymbolA = pending.Symbol;
            state.VolumeA = pending.Volume;
        }
        else if (string.Equals(pending.ExchangeLabel, "B", StringComparison.OrdinalIgnoreCase))
        {
            state.TradeMapNameB = pending.TradeMapName;
            state.TradeTypeB = pending.TradeType;
            state.SymbolB = pending.Symbol;
            state.VolumeB = pending.Volume;
        }
    }

    private void RegisterPendingCloseRequest(
        string tradeMapName,
        ulong? ticket,
        int tradeType,
        double? expectedPrice,
        DateTimeOffset appCloseRequestTimeLocal,
        long appCloseRequestRawMs,
        string? symbol,
        double? volume,
        bool isAutoFlow,
        int slotNumber = 0,
        string exchangeLabel = "",
        string? pairIdOverride = null)
    {
        var key = NormalizeMapName(tradeMapName);
        if (!_pendingCloseRequestsByMap.TryGetValue(key, out var pendingList))
        {
            pendingList = [];
            _pendingCloseRequestsByMap[key] = pendingList;
        }

        var pending = new PendingCloseRequest(
            PairId: string.IsNullOrWhiteSpace(pairIdOverride)
                ? ResolvePairIdForClose(ticket, slotNumber, appCloseRequestRawMs, isAutoFlow)
                : pairIdOverride,
            TradeMapName: key,
            Ticket: ticket,
            Symbol: symbol,
            TradeType: tradeType,
            Volume: volume,
            ExpectedPrice: expectedPrice,
            AppCloseRequestTimeLocal: appCloseRequestTimeLocal,
            AppCloseRequestUnixMs: appCloseRequestTimeLocal.ToUnixTimeMilliseconds(),
            AppCloseRequestRawMs: appCloseRequestRawMs,
            IsAutoFlow: isAutoFlow,
            SlotNumber: slotNumber,
            ExchangeLabel: exchangeLabel);

        pendingList.Add(pending);
        RegisterOrUpdatePendingClosePairState(pending);
        Debug.WriteLine($"[ExecClose][Capture] map={key}, ticket={(ticket.HasValue ? ticket.Value.ToString(CultureInfo.InvariantCulture) : "-")}, type={tradeType}, slot={slotNumber}, label={exchangeLabel}, app_close_request_time={pending.AppCloseRequestTimeLocal:O}, app_close_request_raw_ms={pending.AppCloseRequestRawMs}");
    }

    // Close pending timeout dùng làm khoảng chờ giữa các lần retry confirm close.
    // Random hóa trong [base, base+jitter] để khoảng retry không đều nhau; giữ semantics
    // disable khi base<=0 (timeout<=0 → skip retry). Sinh mới cho MỖI chu kỳ chờ.
    private static int NextClosePendingTimeoutMs(int baseMs)
    {
        var b = Math.Max(0, baseMs);
        if (b <= 0) return 0;
        return Random.Shared.Next(b, b + ClosePendingTimeJitterMs + 1); // [b, b+200]
    }

    private void RegisterOrUpdatePendingClosePairState(PendingCloseRequest pending)
    {
        if (!_pendingClosePairById.TryGetValue(pending.PairId, out var state))
        {
            state = new PendingClosePairState
            {
                PairId = pending.PairId,
                IsAutoFlow = pending.IsAutoFlow,
                SlotNumber = pending.SlotNumber,
                CreatedAtLocal = pending.AppCloseRequestTimeLocal,
                LastCheckedAtLocal = pending.AppCloseRequestTimeLocal,
                ClosePendingTimeoutMs = NextClosePendingTimeoutMs(_runtimeConfigState.CurrentClosePendingTimeMs),
                Origin = pending.IsAutoFlow ? PendingCloseOrigin.Auto : PendingCloseOrigin.LegacyManual
            };
            _pendingClosePairById[pending.PairId] = state;
        }

        state.ClosePendingTimeoutMs = NextClosePendingTimeoutMs(_runtimeConfigState.CurrentClosePendingTimeMs);

        if (string.Equals(pending.ExchangeLabel, "A", StringComparison.OrdinalIgnoreCase))
        {
            state.TradeMapNameA = pending.TradeMapName;
            state.PlatformA = ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformA);
            state.TradeHwndA = _runtimeConfigState.CurrentTradeHwndA;
            state.TicketA = pending.Ticket;
            state.TradeTypeA = pending.TradeType;
            state.SymbolA = pending.Symbol;
            state.VolumeA = pending.Volume;
            state.CloseConfirmedA = false;
        }
        else if (string.Equals(pending.ExchangeLabel, "B", StringComparison.OrdinalIgnoreCase))
        {
            state.TradeMapNameB = pending.TradeMapName;
            state.PlatformB = ResolveTradeLegPlatform(_runtimeConfigState.CurrentPlatformB);
            state.TradeHwndB = _runtimeConfigState.CurrentTradeHwndB;
            state.TicketB = pending.Ticket;
            state.TradeTypeB = pending.TradeType;
            state.SymbolB = pending.Symbol;
            state.VolumeB = pending.Volume;
            state.CloseConfirmedB = false;
        }
    }

    private static string ResolveTradeMapNameFromCloseSelection(CloseSelectionResult selection)
        => NormalizeMapName(selection.TradeMapName);

    private static string BuildPairId(int slotNumber, long requestRawMs, bool isAutoFlow)
        => $"{(isAutoFlow ? "AUTO" : "MANUAL")}-{slotNumber:D4}-{requestRawMs}";

    private string ResolvePairIdForClose(ulong? ticket, int slotNumber, long requestRawMs, bool isAutoFlow)
    {
        if (ticket.HasValue && _pairIdByTicket.TryGetValue(ticket.Value, out var pairId) && !string.IsNullOrWhiteSpace(pairId))
        {
            return pairId;
        }

        return BuildPairId(slotNumber, requestRawMs, isAutoFlow);
    }

    private static string ResolveTradeMapNameFromHistoryMap(string historyMapName)
    {
        var normalized = NormalizeMapName(historyMapName);
        return normalized.EndsWith("_History", StringComparison.OrdinalIgnoreCase)
            ? string.Concat(normalized.AsSpan(0, normalized.Length - "_History".Length), "_Trades")
            : normalized;
    }

    private static string NormalizeMapName(string? mapName)
        => string.IsNullOrWhiteSpace(mapName) ? string.Empty : mapName.Trim();

    private static double? ResolveExpectedOpenPrice(DashboardMetrics? snapshot, bool isExchangeA, int tradeType)
    {
        if (snapshot is null)
        {
            return null;
        }

        var exchange = isExchangeA ? snapshot.ExchangeA : snapshot.ExchangeB;
        var isBuy = tradeType == 0;

        // Standard forex: Buy at Ask, Sell at Bid
        if (isBuy)
        {
            return exchange.Ask.HasValue ? (double)exchange.Ask.Value : null;
        }

        return exchange.Bid.HasValue ? (double)exchange.Bid.Value : null;
    }

    private static string? ResolveExpectedOpenSymbol(DashboardMetrics? snapshot, bool isExchangeA)
    {
        if (snapshot is null)
        {
            return null;
        }

        return isExchangeA ? snapshot.ExchangeA.Symbol : snapshot.ExchangeB.Symbol;
    }

    private static TradeLegPlatform ResolveTradeLegPlatform(string? platformRaw)
    {
        var normalized = (platformRaw ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "mt4" => TradeLegPlatform.Mt4,
            "mt5" => TradeLegPlatform.Mt5,
            _ => throw new InvalidOperationException($"Unsupported platform: '{platformRaw}'")
        };
    }

    private static double? ResolveExpectedClosePrice(DashboardMetrics? snapshot, bool isExchangeA, int originalTradeType)
    {
        if (snapshot is null)
        {
            return null;
        }

        var exchange = isExchangeA ? snapshot.ExchangeA : snapshot.ExchangeB;
        var isBuyPosition = originalTradeType == 0;

        if (isBuyPosition)
        {
            return exchange.Bid.HasValue ? (double)exchange.Bid.Value : null;
        }

        return exchange.Ask.HasValue ? (double)exchange.Ask.Value : null;
    }

    private void BeginActiveAutoCycle(int slot, DateTimeOffset openedAtLocal, long appOpenRequestRawMs)
    {
        var pairId = BuildPairId(slot, appOpenRequestRawMs, isAutoFlow: true);
        _activeAutoCycle = new ActiveAutoCycleState
        {
            Slot = slot,
            OpenedAtLocal = openedAtLocal,
            PairIdA = pairId,
            PairIdB = pairId,
            TicketA = null,
            TicketB = null
        };

        _activeAutoCloseRecoveryCycle = null;
        _closeBothFlatPollStreak = 0;
    }

    private ActiveAutoCycleState? BuildActiveCloseRecoveryCycle(
        int slot,
        CloseSelectionResult selectA,
        CloseSelectionResult selectB)
    {
        var active = _activeAutoCycle;
        if (active is null || active.Slot != slot)
        {
            return null;
        }

        var pairIdA = ResolvePairIdFromCloseSelection(selectA) ?? active.PairIdA;
        var pairIdB = ResolvePairIdFromCloseSelection(selectB) ?? active.PairIdB;

        return new ActiveAutoCycleState
        {
            Slot = active.Slot,
            OpenedAtLocal = active.OpenedAtLocal,
            PairIdA = pairIdA,
            PairIdB = pairIdB,
            TicketA = selectA.Request?.Ticket ?? active.TicketA,
            TicketB = selectB.Request?.Ticket ?? active.TicketB
        };
    }

    private string? ResolvePairIdFromCloseSelection(CloseSelectionResult selection)
    {
        if (selection.Request is null)
        {
            return null;
        }

        return _pairIdByTicket.TryGetValue(selection.Request.Ticket, out var pairId)
            ? pairId
            : null;
    }

    private bool IsActiveCloseRecoveryCycleClosed(
        SharedMapReadResult<TradeSharedRecord> tradeLeftResult,
        SharedMapReadResult<TradeSharedRecord> tradeRightResult,
        out string reason)
    {
        reason = string.Empty;
        var cycle = _activeAutoCloseRecoveryCycle;
        if (cycle is null)
        {
            reason = "no active close recovery cycle";
            return false;
        }

        if (!tradeLeftResult.IsMapAvailable || !tradeLeftResult.IsParseSuccess
            || !tradeRightResult.IsMapAvailable || !tradeRightResult.IsParseSuccess)
        {
            reason = "map unavailable/parse";
            return false;
        }

        var hasTrackedA = cycle.TicketA.HasValue || !string.IsNullOrWhiteSpace(cycle.PairIdA);
        var hasTrackedB = cycle.TicketB.HasValue || !string.IsNullOrWhiteSpace(cycle.PairIdB);
        if (!hasTrackedA && !hasTrackedB)
        {
            reason = "active cycle has no tracked legs";
            return false;
        }

        var aStillOpen = hasTrackedA && IsTrackedLegStillOpen(tradeLeftResult.Records, cycle.TicketA, cycle.PairIdA);
        var bStillOpen = hasTrackedB && IsTrackedLegStillOpen(tradeRightResult.Records, cycle.TicketB, cycle.PairIdB);

        if (aStillOpen || bStillOpen)
        {
            reason = aStillOpen && bStillOpen
                ? "both active legs still open"
                : aStillOpen
                    ? "active leg A still open"
                    : "active leg B still open";
            return false;
        }

        return true;
    }

    private bool IsTrackedLegStillOpen(
        IReadOnlyList<TradeSharedRecord> records,
        ulong? trackedTicket,
        string? trackedPairId)
    {
        if (trackedTicket.HasValue && records.Any(x => x.Ticket == trackedTicket.Value))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(trackedPairId))
        {
            foreach (var record in records)
            {
                if (_pairIdByTicket.TryGetValue(record.Ticket, out var pairId)
                    && string.Equals(pairId, trackedPairId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void ClearActiveAutoCycleOnCloseFinalize()
    {
        _activeAutoCloseRecoveryCycle = null;
        _activeAutoCycle = null;

        // Clear persisted tickets from Supabase on close finalize
        _ = Task.Run(async () =>
        {
            try
            {
                await _configService.SaveCurrentTicksAsync("", "");
                await _configService.SaveCurrentSlotsAsync("[]");
                SafeVmLog("[RECOVERY][INFO] Cleared current ticks/current_slots from DB after close finalize");
            }
            catch (Exception ex)
            {
                SafeVmLog($"[RECOVERY][WARN] Failed to clear current recovery state: {ex.Message}");
            }
        });
    }

    private bool IsPendingCloseStateForActiveCycle(PendingClosePairState state)
    {
        var active = _activeAutoCycle;
        if (active is null)
        {
            return false;
        }

        if (state.SlotNumber != active.Slot)
        {
            return false;
        }

        return string.Equals(state.PairId, active.PairIdA, StringComparison.Ordinal)
            || string.Equals(state.PairId, active.PairIdB, StringComparison.Ordinal);
    }

    private void ShowManualTradeFeedback(string actionName, ManualTradeResult result)
    {
        var detail = BuildManualTradeFeedbackText(actionName, result);
        var hasFailedLeg = result.Legs.Any(x => !x.Success);
        var isError = !result.Success || hasFailedLeg || !string.IsNullOrWhiteSpace(result.ErrorMessage);

        if (isError)
        {
            TryCopyToClipboard(detail);
            SafeVmLog($"[MANUAL][ERROR] {actionName} FAILED: {detail}");
        }
    }

    private static string BuildManualTradeFeedbackText(string actionName, ManualTradeResult result)
    {
        var lines = new List<string>
        {
            $"Action: {actionName}",
            $"Label: {result.Label}",
            $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}",
            $"Result: {(result.Success ? "SUCCESS" : "FAILED")}",
            "Details:"
        };

        if (result.Legs.Count == 0)
        {
            lines.Add("- (no leg details)");
        }
        else
        {
            foreach (var leg in result.Legs)
            {
                lines.Add($"- {leg.Exchange} {leg.Action}: {(leg.Success ? "OK" : "FAILED")} | {leg.Detail}");
            }
        }

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            lines.Add($"Error: {result.ErrorMessage}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void TryCopyToClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            // Ignore clipboard failures.
            SafeVmLog($"[VM][WARN] Suppressed exception at TryCopyToClipboard: {ex.Message}");
        }
    }

    private void ResetTradingLogicState()
    {
        _tradingFlowEngine.Reset();
        LogFlowTransitionIfChanged("engine-reset");
        _manualSlot = 0;
        _autoSlot = 0;
        _isManualOpenInFlight = false;
        _manualOpenGatePairState = LivePairTradeState.MapUnavailableOrParseError;
        _lastAutoOpenClickAtLocal = null;
        _closeDispatchInFlight = 0;
        _isAutoOpenPausedByInvariant = false;
        _invariantClearStreak = 0;
        _activeAutoCycle = null;
        _activeAutoCloseRecoveryCycle = null;
        _closeBothFlatPollStreak = 0;
        _externalPartialCloseStreak = 0;
        _externalPartialCloseInFlight = false;
        _lastExternalCloseGuardBlockReason = string.Empty;
        _hadBothOpenRecently = false;
        _lastMmfAvailability.Clear();
        _lastMmfParseSuccess.Clear();
        _initialTradeTicketScanDoneMaps.Clear();
        _initialHistoryTicketScanDoneMaps.Clear();
        _loggedTradeTicketsByMap.Clear();
        _loggedHistoryTicketsByMap.Clear();
        _lastLoggedLatencyA = null;
        _lastLoggedLatencyB = null;
        _lastLatencyLogAtUtc = DateTime.MinValue;
        _lastTickTokenA = null;
        _lastTickTokenB = null;
        _lastTickObservedAtA = DateTime.UtcNow;
        _lastTickObservedAtB = DateTime.UtcNow;
        _isStaleA = false;
        _isStaleB = false;
        _lastPerfSummaryAtUtc = DateTime.MinValue;
        RaiseCurrentPositionTextChanged();
        OnPropertyChanged(nameof(CurrentPhaseText));
        RaiseManualOpenCanExecuteChanged();
    }

    private Task CopyHostNameAsync()
    {
        try
        {
            var hostNameToCopy = string.IsNullOrWhiteSpace(_normalizedHostName)
                ? MachineHostName
                : _normalizedHostName;

            if (string.IsNullOrWhiteSpace(hostNameToCopy))
            {
                return Task.CompletedTask;
            }

            System.Windows.Clipboard.SetText(hostNameToCopy);
        }
        catch (Exception ex)
        {
            // Ignore clipboard failures to keep UI responsive.
            SafeVmLog($"[VM][WARN] Suppressed exception at CopyHostNameAsync: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private Task OpenLogFolderAsync()
    {
        try
        {
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var logDirectory = Path.Combine(desktopPath, "trade-log");
            if (!Directory.Exists(logDirectory))
            {
                System.Windows.MessageBox.Show(
                    $"Folder chưa tồn tại: {logDirectory}\nBấm Start để tạo file log đầu tiên.",
                    "Log Folder",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return Task.CompletedTask;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", logDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SafeVmLog($"[UI][WARN] Open log folder failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private Task OpenCurrentLogAsync()
    {
        try
        {
            var currentLogPath = _tradeSessionFileLogger.CurrentLogFilePath;
            if (string.IsNullOrWhiteSpace(currentLogPath) || !File.Exists(currentLogPath))
            {
                System.Windows.MessageBox.Show(
                    "Chưa có session log nào đang mở. Bấm Start trước.",
                    "Current Log",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return Task.CompletedTask;
            }

            Process.Start(new ProcessStartInfo(currentLogPath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SafeVmLog($"[UI][WARN] Open current log failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private async Task ResyncOpenTradesAsync()
    {
        try
        {
            var tradeMapNameA = TradeTab.LeftPanel.TargetMapName;
            var tradeMapNameB = TradeTab.RightPanel.TargetMapName;

            if (string.IsNullOrWhiteSpace(tradeMapNameA) || string.IsNullOrWhiteSpace(tradeMapNameB))
            {
                AddSignalLog($"    - [{DateTime.Now:HH:mm:ss.fff}] Resync failed: trade map names are not available");
                return;
            }

            var tradeResultA = _tradesSharedMemoryReader.ReadTrades(tradeMapNameA);
            var tradeResultB = _tradesSharedMemoryReader.ReadTrades(tradeMapNameB);
            if (!AreTradeMapsHealthy(tradeResultA, tradeResultB))
            {
                AddSignalLog($"    - [{DateTime.Now:HH:mm:ss.fff}] Resync failed: trade maps unavailable or parse failed");
                return;
            }

            var restoredCount = TryRebuildCoordinatorFromMmf(
                tradeResultA.Records, tradeResultB.Records, tradeMapNameA, tradeMapNameB);
            if (restoredCount == 0)
            {
                AddSignalLog($"    - [{DateTime.Now:HH:mm:ss.fff}] Resync skipped: no pairable open trades found");
                return;
            }

            await PersistCurrentSlotsSnapshotAsync("manual-resync");

            AddSignalLog(
                $"    - [{DateTime.Now:HH:mm:ss.fff}] Resync complete: restored {restoredCount} slot(s), " +
                $"auto-open resumed");
        }
        catch (Exception ex)
        {
            SafeVmLog($"[RECOVERY][ERROR] Resync open trades failed: {ex}");
            AddSignalLog($"    - [{DateTime.Now:HH:mm:ss.fff}] Resync failed: {ex.Message}");
        }
    }

    private Task OpenConfigAsync()
    {
        try
        {
            ClearConfigError();
            var configWindow = _serviceProvider.GetRequiredService<ConfigWindow>();
            var mainWindow = System.Windows.Application.Current?.MainWindow;
            if (mainWindow is not null && !ReferenceEquals(mainWindow, configWindow))
            {
                configWindow.Owner = mainWindow;
            }

            configWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            var error = $"[Config] Không mở được cửa sổ Config: {ex.Message}";
            ShowConfigError(error);

            var owner = System.Windows.Application.Current?.MainWindow;
            System.Windows.MessageBox.Show(
                owner,
                error,
                "Config Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }

        return Task.CompletedTask;
    }

    private void ShowConfigError(string message)
    {
        ConfigErrorMessage = message;
        IsConfigErrorVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void ClearConfigError()
    {
        ConfigErrorMessage = string.Empty;
        IsConfigErrorVisible = false;
    }

    private async Task ReconnectConfigAsync()
    {
        await InitializeRuntimeConfigAsync();
    }

    private void ApplyRuntimeConfig()
    {
        ExchangeAHeader = string.IsNullOrWhiteSpace(_runtimeConfigState.MapName1)
            ? "Sàn A"
            : $"Sàn A ({_runtimeConfigState.MapName1})";

        ExchangeBHeader = string.IsNullOrWhiteSpace(_runtimeConfigState.MapName2)
            ? "Sàn B"
            : $"Sàn B ({_runtimeConfigState.MapName2})";

        RuntimeSummary =
            $"Host Name: {_runtimeConfigState.CurrentMachineHostName}  |  Point: {_runtimeConfigState.CurrentPoint}  |  OpenPts: {_runtimeConfigState.CurrentOpenPts}  |  ConfirmGapPts: {_runtimeConfigState.CurrentConfirmGapPts}  |  ClosePts: {_runtimeConfigState.CurrentClosePts}  |  CloseConfirmGapPts: {_runtimeConfigState.CurrentCloseConfirmGapPts}  |  SOS Profit: {_runtimeConfigState.CurrentSosTriggerProfitPts}  |  SOS Time: {_runtimeConfigState.CurrentSosTriggerAfterSeconds}s  |  SOS ConfirmGap: {_runtimeConfigState.CurrentSosCloseConfirmGapPts}  |  SOS CloseGap: {_runtimeConfigState.CurrentSosCloseGapPts}  |  OppositeOpenDistance: {_runtimeConfigState.CurrentOppositeOpenMinDistancePts}pts  |  StartTimeHold: {_runtimeConfigState.CurrentStartTimeHold}  |  EndTimeHold: {_runtimeConfigState.CurrentEndTimeHold}  |  ConfirmLatencyMs: {_runtimeConfigState.CurrentConfirmLatencyMs}  |  MaxGap: {_runtimeConfigState.CurrentMaxGap}  |  LimitMaxGap: {_runtimeConfigState.CurrentLimitMaxGap}  |  LimitMaxTp: {_runtimeConfigState.CurrentLimitMaxTp}  |  MaxSpread: {_runtimeConfigState.CurrentMaxSpread}  |  Map 1: {_runtimeConfigState.CurrentMapName1}  |  Map 2: {_runtimeConfigState.CurrentMapName2}";

        HasManualTradeHwndConfig = _runtimeConfigState.CurrentManualHwndColumns.Any(x => x.IsComplete);
        RefreshManualOpenAvailability(ComputeToolAwarePairStateForOpenGate(GetLivePairTradeStateStrict()));

        SyncPortfolioCoordinatorConfig();
        RefreshOrderInfoTabs();
    }

    /// <summary>
    /// Phase 1: push quota + cooldown config từ RuntimeConfigState xuống coordinator.
    /// Gọi từ constructor và sau mỗi runtime config update (DB load, user change).
    /// Phase 1 cap=1 default; Phase 5 sẽ load real values từ DB.
    /// </summary>
    private void SyncPortfolioCoordinatorConfig()
    {
        if (_portfolioCoordinator is null) return;

        _portfolioCoordinator.UpdateQuotaConfig(
            maxTotal: _runtimeConfigState.CurrentMaxTotalOpens,
            maxBuy: _runtimeConfigState.CurrentMaxBuyOpens,
            maxSell: _runtimeConfigState.CurrentMaxSellOpens);

        // Legacy random wait columns were removed. Global range remains zero; transition
        // spacing is owned by the Auto transition gate and post-close config.
        _portfolioCoordinator.UpdateCooldownConfig(minSec: 0, maxSec: 0);

        _portfolioCoordinator.UpdateMaxLifeTimeConfig(
            _runtimeConfigState.CurrentMaxLifeTimeBySecond);

        // Rule C — 2 lock độc lập từ DB, thay hardcode 300 cũ:
        // opposite_side_lock_seconds → lock sau OPEN (chỉ chặn chiều ngược).
        // Random post-open/post-close ranges are sampled once per corresponding transition.
        _portfolioCoordinator.UpdateOppositeSideLockConfig(
            _runtimeConfigState.CurrentOppositeSideLockSeconds);
        _portfolioCoordinator.UpdatePostCloseLockConfig(
            _runtimeConfigState.CurrentRdStartPostCloseLockSeconds,
            _runtimeConfigState.CurrentRdEndPostCloseLockSeconds);
        _portfolioCoordinator.UpdatePostOpenLockConfig(
            _runtimeConfigState.CurrentRdStartPostOpenLockSeconds,
            _runtimeConfigState.CurrentRdEndPostOpenLockSeconds);
        _portfolioCoordinator.UpdateSameActionLockConfig(
            _runtimeConfigState.CurrentRdStartSameActionLockSeconds,
            _runtimeConfigState.CurrentRdEndSameActionLockSeconds);
        _portfolioCoordinator.UpdateScheduleSleepingConfig(
            _runtimeConfigState.CurrentScheduleSleepingJson);
    }

    private void RefreshOrderInfoTabs()
    {
        var tickMapA = _runtimeConfigState.MapName1;
        var tickMapB = _runtimeConfigState.MapName2;

        BindTabMapNames(
            TradeTab,
            tickMapA,
            tickMapB,
            OrderMapNameResolver.BuildTradeMapName);

        BindTabMapNames(
            HistoryTab,
            tickMapA,
            tickMapB,
            OrderMapNameResolver.BuildHistoryMapName);
    }

    private static void BindTabMapNames(
        OrderInfoTabViewModel tab,
        string leftTickMap,
        string rightTickMap,
        Func<string, string> mapNameResolver)
    {
        BindPanelMapName(tab.LeftPanel, leftTickMap, mapNameResolver);
        BindPanelMapName(tab.RightPanel, rightTickMap, mapNameResolver);
    }

    private static void BindPanelMapName(
        OrderPanelStatusViewModel panel,
        string sourceTickMapName,
        Func<string, string> mapNameResolver)
    {
        var targetMapName = mapNameResolver(sourceTickMapName);

        if (string.Equals(panel.TargetMapName, targetMapName, StringComparison.Ordinal))
        {
            return;
        }

        panel.ApplyMapBinding(sourceTickMapName, targetMapName);
        panel.SetLoading();
    }

    private async Task RunOrderInfoPollingAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(OrderInfoPollInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            List<PendingOpenTimeoutAction> timeoutActions = [];
            List<PendingCloseRetryAction> closeRetryActions = [];
            var tradeLeftResult = ReadTradesWithMmfLog(TradeTab.LeftPanel.TargetMapName);
            var tradeRightResult = ReadTradesWithMmfLog(TradeTab.RightPanel.TargetMapName);
            var historyLeftResult = ReadHistoryWithMmfLog(HistoryTab.LeftPanel.TargetMapName);
            var historyRightResult = ReadHistoryWithMmfLog(HistoryTab.RightPanel.TargetMapName);
            LogTradeTicketChangeIfNeeded(TradeTab.LeftPanel.TargetMapName, tradeLeftResult);
            LogTradeTicketChangeIfNeeded(TradeTab.RightPanel.TargetMapName, tradeRightResult);
            LogHistoryTicketChangeIfNeeded(HistoryTab.LeftPanel.TargetMapName, historyLeftResult);
            LogHistoryTicketChangeIfNeeded(HistoryTab.RightPanel.TargetMapName, historyRightResult);
            var shouldApplyTradeLeft = ShouldApplyTradeResult(TradeTab.LeftPanel.TargetMapName, tradeLeftResult);
            var shouldApplyTradeRight = ShouldApplyTradeResult(TradeTab.RightPanel.TargetMapName, tradeRightResult);
            var shouldApplyHistoryLeft = ShouldApplyHistoryResult(HistoryTab.LeftPanel.TargetMapName, historyLeftResult);
            var shouldApplyHistoryRight = ShouldApplyHistoryResult(HistoryTab.RightPanel.TargetMapName, historyRightResult);
            var snapshot = _runtimeConfigState.CurrentDashboardMetrics;
            var point = _runtimeConfigState.CurrentPoint;
            var livePairStateFromPoll = GetLivePairTradeState(tradeLeftResult, tradeRightResult);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // Connection kill-switch: chỉ giám sát khi đang chạy Auto. Evaluate + Reset (lúc Start)
                // cùng chạy trên UI thread ở đây → tránh data race trên state của evaluator.
                // --- Tạm vô hiệu hoá kill-switch mất kết nối (comment feature f3ab5ee) ---
                // if (IsTradingLogicEnabled)
                // {
                //     ApplyConnectionStatus(_connectionEvaluator.Evaluate(
                //         ToChannelConnectionInput(tradeLeftResult),
                //         ToChannelConnectionInput(tradeRightResult)));
                // }

                if (shouldApplyTradeLeft)
                {
                    _latestTradeLeftResult = tradeLeftResult;
                    ApplyTradeResult(TradeTab.LeftPanel, tradeLeftResult, snapshot, isExchangeA: true, point);
                    OnPropertyChanged(nameof(CurrentPositionTextA));
                }

                if (shouldApplyTradeRight)
                {
                    _latestTradeRightResult = tradeRightResult;
                    ApplyTradeResult(TradeTab.RightPanel, tradeRightResult, snapshot, isExchangeA: false, point);
                    OnPropertyChanged(nameof(CurrentPositionTextB));
                }

                if (shouldApplyHistoryLeft)
                {
                    ApplyHistoryResult(HistoryTab.LeftPanel, historyLeftResult, point);
                }

                if (shouldApplyHistoryRight)
                {
                    ApplyHistoryResult(HistoryTab.RightPanel, historyRightResult, point);
                }

                // FIX (post-close wait race): collect close-retry actions BEFORE
                // SyncTradingFlowWithLivePairState. CollectPendingCloseRetryActions has a
                // side effect: when both legs confirmed closed it calls
                // TryBeginWaitAfterCloseFromPending → BeginWaitAfterClose, which transitions
                // engine WaitingClose → WaitingOpen with CurrentWaitSeconds set from config.
                // If sync runs first and sees BothFlat, it calls ForceWaitingOpen() that wipes
                // CurrentWaitSeconds = 0 and BeginWaitAfterClose's guard then returns early —
                // a new signal can open immediately after close; transition gate owns the wait.
                closeRetryActions = CollectPendingCloseRetryActions();

                RefreshManualOpenAvailability(ComputeToolAwarePairStateForOpenGate(livePairStateFromPoll));
                SyncTradingFlowWithLivePairState(livePairStateFromPoll);

                EvaluateAndApplyAutoOpenInvariantWatchdog(tradeLeftResult, tradeRightResult);

                TryRecoverWaitingCloseFromPolling(livePairStateFromPoll);

                // Track BothOpen state để watchdog hoạt động kể cả khi phase bị reset sang WaitingOpen
                if (livePairStateFromPoll == LivePairTradeState.BothOpen)
                {
                    _hadBothOpenRecently = true;
                }
                else if (livePairStateFromPoll == LivePairTradeState.BothFlat && !_externalPartialCloseInFlight)
                {
                    _hadBothOpenRecently = false;
                }

                TryDetectAndHandleExternalPartialClose(livePairStateFromPoll);

                timeoutActions = CollectPendingOpenTimeoutActions();
            });

            if (timeoutActions.Count > 0)
            {
                await ExecutePendingOpenTimeoutActionsAsync(timeoutActions, cancellationToken);
            }

            if (closeRetryActions.Count > 0)
            {
                await ExecutePendingCloseRetryActionsAsync(closeRetryActions, cancellationToken);
            }

            // HWND health-check định kỳ 60s (chỉ khi Auto đang chạy) — phát hiện MT4/MT5 tự out.
            // Chạy ngoài Dispatcher.Invoke (native, không block UI thread).
            if (IsTradingLogicEnabled && (DateTime.UtcNow - _lastHwndCheckUtc).TotalSeconds >= 60)
            {
                _lastHwndCheckUtc = DateTime.UtcNow;
                RunHwndHealthCheck("periodic");
            }
        }
    }

    private SharedMapReadResult<TradeSharedRecord> ReadTradesWithMmfLog(string mapName)
    {
        var result = _tradesSharedMemoryReader.ReadTrades(mapName);
        LogMmfStateIfChanged("MMF_TRADES", mapName, result.IsMapAvailable, result.IsParseSuccess);
        return result;
    }

    private SharedMapReadResult<HistorySharedRecord> ReadHistoryWithMmfLog(string mapName)
    {
        var result = _historySharedMemoryReader.ReadHistory(mapName);
        LogMmfStateIfChanged("MMF_HISTORY", mapName, result.IsMapAvailable, result.IsParseSuccess);
        return result;
    }

    private void LogMmfStateIfChanged(string category, string mapName, bool isAvailable, bool isParseSuccess)
    {
        try
        {
            _lastMmfAvailability.TryGetValue(mapName, out var lastAvail);
            _lastMmfParseSuccess.TryGetValue(mapName, out var lastParse);

            if (lastAvail != isAvailable)
            {
                var level = isAvailable ? "INFO" : "WARN";
                SafeVmLog($"[{category}][{level}] Map availability changed: map={mapName} {lastAvail?.ToString() ?? "unknown"} -> {isAvailable}");
                _lastMmfAvailability[mapName] = isAvailable;
            }

            if (isAvailable && lastParse != isParseSuccess)
            {
                var level = isParseSuccess ? "INFO" : "WARN";
                SafeVmLog($"[{category}][{level}] Map parse status changed: map={mapName} {lastParse?.ToString() ?? "unknown"} -> {isParseSuccess}");
                _lastMmfParseSuccess[mapName] = isParseSuccess;
            }
        }
        catch
        {
            // swallow by design
        }
    }

    private void LogTradeTicketChangeIfNeeded(string mapName, SharedMapReadResult<TradeSharedRecord> result)
    {
        if (!result.IsMapAvailable || !result.IsParseSuccess)
        {
            return;
        }

        var key = NormalizeMapName(mapName);
        var currentTickets = result.Records.Select(x => x.Ticket).ToHashSet();
        if (!_initialTradeTicketScanDoneMaps.Contains(key))
        {
            _initialTradeTicketScanDoneMaps.Add(key);
            _loggedTradeTicketsByMap[key] = currentTickets;
            return;
        }

        if (!_loggedTradeTicketsByMap.TryGetValue(key, out var previousTickets))
        {
            previousTickets = [];
        }

        var added = currentTickets.Where(x => !previousTickets.Contains(x)).Take(5).ToArray();
        var removed = previousTickets.Where(x => !currentTickets.Contains(x)).Take(5).ToArray();
        var hasChange = added.Length > 0 || removed.Length > 0 || currentTickets.Count != previousTickets.Count;
        if (!hasChange)
        {
            return;
        }

        SafeVmLog(
            $"[MMF_TRADES][INFO] Ticket set changed: map={key} total={currentTickets.Count} " +
            $"added={added.Length} [{string.Join(',', added)}] removed={removed.Length} [{string.Join(',', removed)}]");
        _loggedTradeTicketsByMap[key] = currentTickets;
    }

    private void LogHistoryTicketChangeIfNeeded(string mapName, SharedMapReadResult<HistorySharedRecord> result)
    {
        if (!result.IsMapAvailable || !result.IsParseSuccess)
        {
            return;
        }

        var key = NormalizeMapName(mapName);
        var currentTickets = result.Records.Select(x => x.Ticket).ToHashSet();
        if (!_initialHistoryTicketScanDoneMaps.Contains(key))
        {
            _initialHistoryTicketScanDoneMaps.Add(key);
            _loggedHistoryTicketsByMap[key] = currentTickets;
            return;
        }

        if (!_loggedHistoryTicketsByMap.TryGetValue(key, out var previousTickets))
        {
            previousTickets = [];
        }

        var added = currentTickets.Where(x => !previousTickets.Contains(x)).Take(5).ToArray();
        var removed = previousTickets.Where(x => !currentTickets.Contains(x)).Take(5).ToArray();
        var hasChange = added.Length > 0 || removed.Length > 0 || currentTickets.Count != previousTickets.Count;
        if (!hasChange)
        {
            return;
        }

        SafeVmLog(
            $"[MMF_HISTORY][INFO] Ticket set changed: map={key} total={currentTickets.Count} " +
            $"added={added.Length} [{string.Join(',', added)}] removed={removed.Length} [{string.Join(',', removed)}]");
        _loggedHistoryTicketsByMap[key] = currentTickets;
    }

    private void LogLatencyAnomalyIfNeeded(DashboardMetrics metrics)
    {
        var nowUtc = DateTime.UtcNow;
        var loggedA = LogLatencySpikeForExchange("A", metrics.ExchangeA.LatencyMs, ref _lastLoggedLatencyA, nowUtc);
        var loggedB = LogLatencySpikeForExchange("B", metrics.ExchangeB.LatencyMs, ref _lastLoggedLatencyB, nowUtc);
        if (loggedA || loggedB)
        {
            _lastLatencyLogAtUtc = nowUtc;
        }
    }

    private bool LogLatencySpikeForExchange(string exchange, decimal? latencyMs, ref double? lastLatencyMs, DateTime nowUtc)
    {
        var currentLatency = latencyMs.HasValue ? (double?)latencyMs.Value : null;
        var wasHigh = lastLatencyMs.HasValue && lastLatencyMs.Value >= LatencySpikeThresholdMs;
        var isHigh = currentLatency.HasValue && currentLatency.Value >= LatencySpikeThresholdMs;
        lastLatencyMs = currentLatency;

        if (!isHigh)
        {
            return false;
        }

        var canRepeat = (nowUtc - _lastLatencyLogAtUtc) >= TimeSpan.FromSeconds(LatencyLogMinIntervalSeconds);
        if (!wasHigh || canRepeat)
        {
            SafeVmLog($"[MARKET][WARN] Latency spike: exchange={exchange} latencyMs={currentLatency:0.##} thresholdMs={LatencySpikeThresholdMs:0.##}");
            return true;
        }

        return false;
    }

    private void LogStaleTickIfNeeded(DashboardMetrics metrics)
    {
        var nowUtc = DateTime.UtcNow;
        LogStaleTickForExchange(
            exchange: "A",
            token: BuildTickToken(metrics.ExchangeA, metrics.TimestampUtc),
            ref _lastTickTokenA,
            ref _lastTickObservedAtA,
            ref _isStaleA,
            nowUtc);

        LogStaleTickForExchange(
            exchange: "B",
            token: BuildTickToken(metrics.ExchangeB, metrics.TimestampUtc),
            ref _lastTickTokenB,
            ref _lastTickObservedAtB,
            ref _isStaleB,
            nowUtc);
    }

    private void LogStaleTickForExchange(
        string exchange,
        string token,
        ref string? lastToken,
        ref DateTime lastObservedAtUtc,
        ref bool isStale,
        DateTime nowUtc)
    {
        if (!string.Equals(lastToken, token, StringComparison.Ordinal))
        {
            lastToken = token;
            lastObservedAtUtc = nowUtc;
            if (isStale)
            {
                SafeVmLog($"[MARKET][INFO] Tick resumed: exchange={exchange} staleSec={StaleTickThresholdSeconds}");
                isStale = false;
            }

            return;
        }

        if (isStale)
        {
            return;
        }

        var staleDuration = nowUtc - lastObservedAtUtc;
        if (staleDuration >= TimeSpan.FromSeconds(StaleTickThresholdSeconds))
        {
            SafeVmLog($"[MARKET][WARN] Stale tick detected: exchange={exchange} staleForMs={(long)staleDuration.TotalMilliseconds}");
            isStale = true;
        }
    }

    private static string BuildTickToken(ExchangeDashboardMetrics exchange, DateTime timestampUtc)
        => $"{timestampUtc:O}|{exchange.Time}|{exchange.Bid?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{exchange.Ask?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

    // Band hoá số liệu để tạo signature thô: chỉ log lại khi tps/latency đổi đáng kể (không log mỗi phút khi ổn định).
    private static string BandPerfMetric(double? value, double bucket)
        => value.HasValue ? (Math.Round(value.Value / bucket) * bucket).ToString("0", CultureInfo.InvariantCulture) : "-";

    private static string BuildPerfSummarySignature(DashboardMetrics metrics)
    {
        string Side(ExchangeDashboardMetrics e)
            => $"{BandPerfMetric(e.Tps, 1d)}|{BandPerfMetric(e.LatencyMs.HasValue ? (double)e.LatencyMs.Value : (double?)null, 20d)}|{BandPerfMetric(e.AvgLatMs.HasValue ? (double)e.AvgLatMs.Value : (double?)null, 20d)}";
        return $"A({Side(metrics.ExchangeA)})B({Side(metrics.ExchangeB)})";
    }

    private void LogPerfSummaryIfDue(DashboardMetrics metrics)
    {
        var nowUtc = DateTime.UtcNow;
        if (_lastPerfSummaryAtUtc != DateTime.MinValue
            && (nowUtc - _lastPerfSummaryAtUtc) < TimeSpan.FromSeconds(PerfSummaryIntervalSeconds))
        {
            return;
        }

        // Bỏ qua nếu số liệu (band thô) không đổi và chưa tới heartbeat — tránh lặp 1 dòng/phút khi ổn định.
        var signature = BuildPerfSummarySignature(metrics);
        if (_lastPerfSummaryAtUtc != DateTime.MinValue
            && string.Equals(_lastPerfSummarySignature, signature, StringComparison.Ordinal)
            && (nowUtc - _lastPerfSummaryAtUtc) < TimeSpan.FromSeconds(PerfSummaryHeartbeatSeconds))
        {
            return;
        }
        _lastPerfSummarySignature = signature;

        SafeVmLog(
            "[MARKET][INFO] Perf summary: " +
            $"A(tps={FormatOneDecimalOrDash(metrics.ExchangeA.Tps)},lat={FormatNumberOrDash(metrics.ExchangeA.LatencyMs, 0)},avg={FormatNumberOrDash(metrics.ExchangeA.AvgLatMs, 0)},max={FormatNumberOrDash(metrics.ExchangeA.MaxLatMs, 0)}) " +
            $"B(tps={FormatOneDecimalOrDash(metrics.ExchangeB.Tps)},lat={FormatNumberOrDash(metrics.ExchangeB.LatencyMs, 0)},avg={FormatNumberOrDash(metrics.ExchangeB.AvgLatMs, 0)},max={FormatNumberOrDash(metrics.ExchangeB.MaxLatMs, 0)})");
        _lastPerfSummaryAtUtc = nowUtc;
    }

    private void EvaluateAndApplyAutoOpenInvariantWatchdog(
        SharedMapReadResult<TradeSharedRecord> tradeLeftResult,
        SharedMapReadResult<TradeSharedRecord> tradeRightResult)
    {
        var bothMapsHealthy = tradeLeftResult.IsMapAvailable
            && tradeLeftResult.IsParseSuccess
            && tradeRightResult.IsMapAvailable
            && tradeRightResult.IsParseSuccess;

        if (!bothMapsHealthy)
        {
            return;
        }

        // Chỉ đếm lệnh do tool mở — lệnh EA không ảnh hưởng invariant
        var toolRowsA = CountToolOpenedRows(tradeLeftResult);
        var toolRowsB = CountToolOpenedRows(tradeRightResult);
        var liveAutoPairCount = CountLiveAutoPairCount(tradeLeftResult, tradeRightResult);

        // Phase 3: multi-slot watchdog formula. Vi phạm nếu:
        //   - Tool tickets trên mỗi sàn vượt số slot tracking trong coordinator
        //   - Hoặc total slot vượt cap config
        //   - Hoặc side counts vượt per-side cap
        // Ở cap=1: coordinatorActiveCount ≤ 1 → toolA>1 hoặc toolB>1 → vi phạm (giống logic cũ).
        var coordinatorActiveCount = _portfolioCoordinator.LiveCount + _portfolioCoordinator.PendingCount;
        var quotaTotalViolation = coordinatorActiveCount > _runtimeConfigState.CurrentMaxTotalOpens;
        var quotaSideViolation =
            _portfolioCoordinator.LiveBuyCount > _runtimeConfigState.CurrentMaxBuyOpens
            || _portfolioCoordinator.LiveSellCount > _runtimeConfigState.CurrentMaxSellOpens;
        var toolOverTrackingViolation =
            toolRowsA > coordinatorActiveCount || toolRowsB > coordinatorActiveCount;
        var autoPairCapViolation = liveAutoPairCount > _runtimeConfigState.CurrentMaxTotalOpens;

        var hasInvariantViolation =
            quotaTotalViolation
            || quotaSideViolation
            || toolOverTrackingViolation
            || autoPairCapViolation;
        if (!hasInvariantViolation)
        {
            if (_isAutoOpenPausedByInvariant)
            {
                _invariantClearStreak++;
                if (_invariantClearStreak == 1)
                {
                    SafeVmLog($"[WATCHDOG][INFO] Invariant clear counting started: 1/{InvariantClearPollsRequired}");
                }

                if (_invariantClearStreak >= InvariantClearPollsRequired)
                {
                    _isAutoOpenPausedByInvariant = false;
                    _invariantClearStreak = 0;
                    SafeVmLog(
                        $"[WATCHDOG][INFO] Invariant cleared after {InvariantClearPollsRequired} stable polls. state=RESUMED");
                    AddSignalLog(
                        $"    - [{DateTime.Now:HH:mm:ss.fff}] Invariant cleared, auto-open resumed");
                }
            }
            return;
        }

        _invariantClearStreak = 0;

        // Self-heal: nếu drift chỉ là coordinator under-count (toolOverTracking) mà trạng thái vật lý
        // trên MMF vẫn hợp lệ ≤ cap, rebuild slot từ MMF để watchdog tự nhả thay vì pause vĩnh viễn.
        // Đây là integrity/recovery (giống resync lúc Start) — KHÔNG tạo open/close dispatch (Rule E safe).
        var rebuilt = BuildResyncedOpenSlots(tradeLeftResult.Records, tradeRightResult.Records);
        var rebuiltBuyCount = rebuilt.Count(s => s.Side == TradingPositionSide.Buy);
        var rebuiltSellCount = rebuilt.Count(s => s.Side == TradingPositionSide.Sell);
        var hasOpenOrCloseInFlight =
            _autoOpenInFlightBuy != 0
            || _autoOpenInFlightSell != 0
            || _closeDispatchInFlight != 0
            || _activeAutoCloseRecoveryCycle is not null
            || _portfolioCoordinator.PendingCloseSlots.Count > 0;
        var action = WatchdogSelfHealDecision.Decide(
            quotaTotalViolation,
            quotaSideViolation,
            autoPairCapViolation,
            toolOverTrackingViolation,
            coordinatorActiveCount,
            rebuilt.Count,
            rebuiltBuyCount,
            rebuiltSellCount,
            _runtimeConfigState.CurrentMaxTotalOpens,
            _runtimeConfigState.CurrentMaxBuyOpens,
            _runtimeConfigState.CurrentMaxSellOpens,
            hasOpenOrCloseInFlight);

        var tradeMapNameA = TradeTab.LeftPanel.TargetMapName;
        var tradeMapNameB = TradeTab.RightPanel.TargetMapName;
        var mapsNamed = !string.IsNullOrWhiteSpace(tradeMapNameA) && !string.IsNullOrWhiteSpace(tradeMapNameB);
        var throttleElapsed =
            (DateTime.UtcNow - _lastWatchdogSelfHealAtUtc).TotalSeconds >= WatchdogSelfHealMinIntervalSeconds;
        var canSelfHeal = action == WatchdogAction.SelfHeal && mapsNamed && throttleElapsed;

        var watchdogMessage =
            $"toolA={toolRowsA},toolB={toolRowsB},autoPairs={liveAutoPairCount}, " +
            $"slotsActive={coordinatorActiveCount}/{_runtimeConfigState.CurrentMaxTotalOpens}, " +
            $"liveBuy={_portfolioCoordinator.LiveBuyCount}/{_runtimeConfigState.CurrentMaxBuyOpens}, " +
            $"liveSell={_portfolioCoordinator.LiveSellCount}/{_runtimeConfigState.CurrentMaxSellOpens}, " +
            $"cond=[quotaTotal={quotaTotalViolation},quotaSide={quotaSideViolation}," +
            $"toolOver={toolOverTrackingViolation},autoPairCap={autoPairCapViolation}], " +
            $"action={action},selfHeal={canSelfHeal}(maps={mapsNamed},throttleOk={throttleElapsed}), " +
            $"rebuild={rebuilt.Count}(buy={rebuiltBuyCount},sell={rebuiltSellCount}), " +
            $"coordSlots=[{FormatCoordinatorSlotPairIds()}], " +
            $"toolTicketsA=[{FormatToolOpenedTickets(tradeLeftResult)}], " +
            $"toolTicketsB=[{FormatToolOpenedTickets(tradeRightResult)}]";

        if (canSelfHeal)
        {
            // ApplyResyncedOpenSlots rebuild coordinator từ MMF + clear _isAutoOpenPausedByInvariant + reset streak.
            ApplyResyncedOpenSlots(rebuilt, tradeMapNameA, tradeMapNameB);
            _lastWatchdogSelfHealAtUtc = DateTime.UtcNow;
            SafeVmLog(
                $"[WATCHDOG][INFO] Self-heal: rebuilt {rebuilt.Count} slot(s) from MMF, auto-open resumed. {watchdogMessage}");
            AddSignalLog(
                $"    - [{DateTime.Now:HH:mm:ss.fff}] Watchdog self-heal: rebuilt {rebuilt.Count} slot(s), auto-open resumed");
            _ = PersistCurrentSlotsSnapshotAsync("watchdog-self-heal");
            return;
        }

        // Không self-heal (vi phạm thật / close đang chạy / throttle / map chưa sẵn sàng) → pause.
        if (_isAutoOpenPausedByInvariant)
        {
            return;
        }

        _isAutoOpenPausedByInvariant = true;
        SafeVmLog($"[WATCHDOG][WARN] Invariant violation detected: {watchdogMessage}. state=PAUSED");
        AddSignalLog($"    - [{DateTime.Now:HH:mm:ss.fff}] Watchdog paused: {watchdogMessage}");
    }

    private static int GetOpenRowCount(SharedMapReadResult<TradeSharedRecord> tradeResult)
    {
        if (!tradeResult.IsMapAvailable || !tradeResult.IsParseSuccess)
        {
            return 0;
        }

        return tradeResult.Records.Count;
    }

    /// <summary>
    /// Đếm số lệnh đang mở được mở QUA TOOL (có trong _pairIdByTicket).
    /// Lệnh EA không do tool mở không được tính vào invariant check.
    /// </summary>
    private int CountToolOpenedRows(SharedMapReadResult<TradeSharedRecord> tradeResult)
    {
        if (!tradeResult.IsMapAvailable || !tradeResult.IsParseSuccess)
        {
            return 0;
        }

        return tradeResult.Records.Count(r => _pairIdByTicket.ContainsKey(r.Ticket));
    }

    /// <summary>
    /// Diagnostic: liệt kê PairId các slot coordinator đang giữ (L=Live, PO=PendingOpen, PC=PendingClose).
    /// </summary>
    private string FormatCoordinatorSlotPairIds()
    {
        var live = _portfolioCoordinator.LiveSlots.Select(s => $"L:{s.PairId}");
        var pendingOpen = _portfolioCoordinator.PendingOpenSlots.Select(s => $"PO:{s.PairId}");
        var pendingClose = _portfolioCoordinator.PendingCloseSlots.Select(s => $"PC:{s.PairId}");
        return string.Join(",", live.Concat(pendingOpen).Concat(pendingClose));
    }

    /// <summary>
    /// Diagnostic: liệt kê ticket:pairId các lệnh đang mở do tool mở (có trong _pairIdByTicket) trên 1 sàn.
    /// </summary>
    private string FormatToolOpenedTickets(SharedMapReadResult<TradeSharedRecord> tradeResult)
    {
        if (!tradeResult.IsMapAvailable || !tradeResult.IsParseSuccess)
        {
            return string.Empty;
        }

        return string.Join(",", tradeResult.Records
            .Where(r => _pairIdByTicket.ContainsKey(r.Ticket))
            .Select(r => $"{r.Ticket}:{_pairIdByTicket[r.Ticket]}"));
    }

    private int CountLiveAutoPairCount(
        SharedMapReadResult<TradeSharedRecord> tradeLeftResult,
        SharedMapReadResult<TradeSharedRecord> tradeRightResult)
    {
        var pairIds = new HashSet<string>(StringComparer.Ordinal);
        AddLiveAutoPairIds(tradeLeftResult, pairIds);
        AddLiveAutoPairIds(tradeRightResult, pairIds);
        return pairIds.Count;
    }

    private void AddLiveAutoPairIds(
        SharedMapReadResult<TradeSharedRecord> tradeResult,
        HashSet<string> pairIds)
    {
        if (!tradeResult.IsMapAvailable || !tradeResult.IsParseSuccess)
        {
            return;
        }

        foreach (var record in tradeResult.Records)
        {
            if (_pairIdByTicket.TryGetValue(record.Ticket, out var pairId)
                && !string.IsNullOrWhiteSpace(pairId)
                && pairId.StartsWith("AUTO-", StringComparison.OrdinalIgnoreCase))
            {
                pairIds.Add(pairId);
            }
        }
    }

    private List<PendingOpenTimeoutAction> CollectPendingOpenTimeoutActions()
    {
        var actions = new List<PendingOpenTimeoutAction>();
        var now = DateTimeOffset.Now;

        foreach (var state in _pendingOpenPairById.Values)
        {
            if (state.IsResolved || state.TimeoutCloseTriggered)
            {
                continue;
            }

            var timeoutMs = Math.Max(0, state.OpenPendingTimeoutMs);
            if (timeoutMs <= 0)
            {
                continue;
            }

            if (now - state.CreatedAtLocal < TimeSpan.FromMilliseconds(timeoutMs))
            {
                continue;
            }

            var hasOnlyA = state.OpenConfirmedA && !state.OpenConfirmedB && state.OpenedTicketA.HasValue;
            var hasOnlyB = state.OpenConfirmedB && !state.OpenConfirmedA && state.OpenedTicketB.HasValue;
            var hasPartialOpen = hasOnlyA || hasOnlyB;

            if (!hasPartialOpen)
            {
                state.TimeoutRecheckPending = false;
                state.TimeoutRecheckRequestedAtLocal = null;
                state.IsResolved = true;
                continue;
            }

            // Recheck once more after 1 second (aligned with poll cycle) before concluding
            // the missing leg cannot be opened and triggering close for opened leg.
            if (!state.TimeoutRecheckPending)
            {
                state.TimeoutRecheckPending = true;
                state.TimeoutRecheckRequestedAtLocal = now;
                continue;
            }

            var recheckElapsed = now - (state.TimeoutRecheckRequestedAtLocal ?? now);
            if (recheckElapsed < OpenPartialRecheckDelay)
            {
                continue;
            }

            state.TimeoutCloseTriggered = true;
            state.TimeoutRecheckPending = false;
            state.TimeoutRecheckRequestedAtLocal = null;
            SafeVmLog(
                $"[CYCLE][ERROR] Pending open timeout: pairId={state.PairId} elapsedMs={(now - state.CreatedAtLocal).TotalMilliseconds:0} " +
                $"confirmedA={state.OpenConfirmedA} confirmedB={state.OpenConfirmedB}");
            NotifyTelegram(
                eventCode: hasOnlyA ? "OPEN_PARTIAL_A_ONLY" : "OPEN_PARTIAL_B_ONLY",
                severity: "CRITICAL",
                detail: hasOnlyA
                    ? "Open timeout: A vào lệnh nhưng B không vào"
                    : "Open timeout: B vào lệnh nhưng A không vào",
                pairId: state.PairId,
                meta: new Dictionary<string, string?>
                {
                    ["openedExchange"] = hasOnlyA ? "A" : "B",
                    ["missingExchange"] = hasOnlyA ? "B" : "A",
                    ["elapsedMs"] = ((long)(now - state.CreatedAtLocal).TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
                    ["confirmedA"] = state.OpenConfirmedA.ToString(),
                    ["confirmedB"] = state.OpenConfirmedB.ToString()
                });

            if (hasOnlyA)
            {
                actions.Add(new PendingOpenTimeoutAction(
                    PairId: state.PairId,
                    IsAutoFlow: state.IsAutoFlow,
                    SlotNumber: state.SlotNumber,
                    OpenedExchange: "A",
                    MissingExchange: "B",
                    Ticket: state.OpenedTicketA!.Value,
                    TradeMapName: state.TradeMapNameA ?? string.Empty,
                    TradeType: state.TradeTypeA,
                    Symbol: state.SymbolA,
                    Volume: state.VolumeA));
            }
            else if (hasOnlyB)
            {
                actions.Add(new PendingOpenTimeoutAction(
                    PairId: state.PairId,
                    IsAutoFlow: state.IsAutoFlow,
                    SlotNumber: state.SlotNumber,
                    OpenedExchange: "B",
                    MissingExchange: "A",
                    Ticket: state.OpenedTicketB!.Value,
                    TradeMapName: state.TradeMapNameB ?? string.Empty,
                    TradeType: state.TradeTypeB,
                    Symbol: state.SymbolB,
                    Volume: state.VolumeB));
            }
        }

        return actions;
    }

    private async Task ExecutePendingOpenTimeoutActionsAsync(
        IReadOnlyList<PendingOpenTimeoutAction> actions,
        CancellationToken cancellationToken)
    {
        foreach (var action in actions)
        {
            if (await CloseOpenedLegByTimeoutAsync(action, cancellationToken))
            {
                // Cleanup only after the compensating close was actually dispatched.
                _portfolioCoordinator.AbortPendingOpen(action.PairId);
                SafeVmLog($"[SLOT][WARN] Slot pending open aborted by timeout: pairId={action.PairId}");
            }
        }
    }

    private async Task<bool> CloseOpenedLegByTimeoutAsync(PendingOpenTimeoutAction action, CancellationToken cancellationToken)
    {
        _portfolioCoordinator.BeginNonAutoCloseOperation(action.PairId);
        System.Windows.Application.Current.Dispatcher.Invoke(RaiseNonAutoCloseUiStateChanged);

        var isExchangeA = string.Equals(action.OpenedExchange, "A", StringComparison.OrdinalIgnoreCase);
        var platform = ResolveTradeLegPlatform(isExchangeA ? _runtimeConfigState.CurrentPlatformA : _runtimeConfigState.CurrentPlatformB);
        var tradeHwnd = isExchangeA ? _runtimeConfigState.CurrentTradeHwndA : _runtimeConfigState.CurrentTradeHwndB;
        var rowIndex = FindRowIndexForTicket(action.TradeMapName, action.Ticket);

        var appCloseRequestTimeLocal = DateTimeOffset.Now;
        var appCloseRequestRawMs = Environment.TickCount64;

        // Đăng ký leg vào hệ retry-close TRƯỚC khi check skip: kể cả khi lần dispatch đầu bị hoãn
        // (switch OFF / HWND invalid), CollectPendingCloseRetryActions vẫn retry tới khi MMF xác nhận
        // leg đóng — tránh bỏ leg mồ côi như bug fire-once cũ.
        if (action.TradeType.HasValue)
        {
            var expectedClose = ResolveExpectedClosePrice(_runtimeConfigState.CurrentDashboardMetrics, isExchangeA, action.TradeType.Value);
            RegisterPendingCloseRequest(
                tradeMapName: action.TradeMapName,
                ticket: action.Ticket,
                tradeType: action.TradeType.Value,
                expectedPrice: expectedClose,
                appCloseRequestTimeLocal: appCloseRequestTimeLocal,
                appCloseRequestRawMs: appCloseRequestRawMs,
                symbol: action.Symbol,
                volume: action.Volume,
                isAutoFlow: action.IsAutoFlow,
                slotNumber: action.SlotNumber,
                exchangeLabel: action.OpenedExchange,
                pairIdOverride: action.PairId);
            if (_pendingClosePairById.TryGetValue(action.PairId, out var recoveryState))
            {
                recoveryState.Origin = PendingCloseOrigin.Recovery;
            }
        }
        else
        {
            // Không biết TradeType → không đăng ký được retry-close → KHÔNG bỏ âm thầm, escalate.
            SafeVmLog($"[CYCLE][ERROR] Timeout rollback thiếu TradeType, không auto-retry đóng leg được: pairId={action.PairId} exch={action.OpenedExchange} ticket={action.Ticket} — CẦN xử lý tay.");
            NotifyTelegram(
                eventCode: "OPEN_ROLLBACK_NO_RETRY",
                severity: "CRITICAL",
                detail: $"Rollback mở-lệch không có TradeType, không thể auto-retry đóng leg {action.OpenedExchange} (ticket={action.Ticket})",
                pairId: action.PairId,
                meta: new Dictionary<string, string?>
                {
                    ["openedExchange"] = action.OpenedExchange,
                    ["ticket"] = action.Ticket.ToString(CultureInfo.InvariantCulture)
                });
        }

        // Open-cycle coi như xử lý xong (leg do hệ retry-close sở hữu, hoặc đã escalate ở trên).
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_pendingOpenPairById.TryGetValue(action.PairId, out var openState))
            {
                openState.IsResolved = true;
            }
        });

        // Hoãn dispatch lần đầu khi đang bị chặn (switch OFF / HWND invalid) — retry-close lo tiếp.
        if (ShouldSkipTradeOp($"recovery-timeout-close pairId={action.PairId}"))
        {
            return false;
        }

        var closeResult = await _tradeExecutionRouter.ClosePairAsync(
            new TradeClosePairRequest(
                LegA: isExchangeA
                    ? new TradeCloseLegRequest(
                        Exchange: "A",
                        Platform: platform,
                        TradeHwnd: tradeHwnd,
                        Ticket: action.Ticket,
                        Action: TradeLegAction.Close,
                        RowIndex: rowIndex,
                        TradeMapName: action.TradeMapName)
                    : null,
                LegB: isExchangeA
                    ? null
                    : new TradeCloseLegRequest(
                        Exchange: "B",
                        Platform: platform,
                        TradeHwnd: tradeHwnd,
                        Ticket: action.Ticket,
                        Action: TradeLegAction.Close,
                        RowIndex: rowIndex,
                        TradeMapName: action.TradeMapName),
                Context: CreateRecoveryContext(
                    TradeExecutionReason.OpenPartialRollback,
                    "open-timeout-rollback",
                    action.PairId,
                    action.SlotNumber,
                    action.Ticket,
                    $"confirmed-{action.OpenedExchange}-missing-{action.MissingExchange}")),
            cancellationToken);

        if (closeResult.IsDispatchBlocked)
        {
            _portfolioCoordinator.EndNonAutoCloseOperation(action.PairId);
            System.Windows.Application.Current.Dispatcher.Invoke(RaiseNonAutoCloseUiStateChanged);
            RemovePendingCloseRequests(appCloseRequestRawMs);
            if (_pendingOpenPairById.TryGetValue(action.PairId, out var openState))
            {
                openState.IsResolved = false;
                openState.TimeoutCloseTriggered = false;
            }
            if (_pendingClosePairById.TryGetValue(action.PairId, out var closeState))
            {
                closeState.IsResolved = true;
            }
            SafeVmLog($"[TRADE_GATE][BLOCKED] rollback CLOSE pairId={action.PairId} remainingMs={closeResult.GateRemainingMilliseconds}");
            return false;
        }

        NotifyOpenCloseFailures("CLOSE", closeResult, action.PairId);

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            SignalLogItems.Insert(0,
                $"[{DateTime.Now:HH:mm:ss.fff}] {action.OpenedExchange} close by {action.MissingExchange} can not open.");
        });

        Debug.WriteLine($"[ExecOpen][TimeoutClose] pairId={action.PairId}, opened={action.OpenedExchange}, missing={action.MissingExchange}, ticket={action.Ticket}, success={closeResult.Success}");
        return true;
    }

    private List<PendingCloseRetryAction> CollectPendingCloseRetryActions()
    {
        var actions = new List<PendingCloseRetryAction>();
        var now = DateTimeOffset.Now;

        // Dọn entries đã resolved từ vòng trước — chống _pendingClosePairById grow vô hạn
        // trong long session. PairId unique theo TickCount64 nên reuse pairId không xảy ra.
        var resolvedKeys = _pendingClosePairById
            .Where(kv => kv.Value.IsResolved)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in resolvedKeys)
        {
            _pendingClosePairById.Remove(key);
        }

        foreach (var state in _pendingClosePairById.Values)
        {
            if (state.IsResolved)
            {
                continue;
            }

            var configMs = Math.Max(0, _runtimeConfigState.CurrentClosePendingTimeMs);
            if (configMs <= 0)
            {
                continue; // disable mid-flight qua config vẫn hoạt động
            }

            // Dùng giá trị random đã sinh cho chu kỳ hiện tại — KHÔNG random lại mỗi tick
            // (tránh nhảy ngưỡng so sánh). Chỉ sinh mới nếu chưa có (registration từng =0 do config=0).
            var timeoutMs = state.ClosePendingTimeoutMs;
            if (timeoutMs <= 0)
            {
                timeoutMs = NextClosePendingTimeoutMs(configMs);
                state.ClosePendingTimeoutMs = timeoutMs;
            }

            if (now - state.LastCheckedAtLocal < TimeSpan.FromMilliseconds(timeoutMs))
            {
                continue;
            }

            state.LastCheckedAtLocal = now;
            state.ClosePendingTimeoutMs = NextClosePendingTimeoutMs(configMs); // fresh jitter cho chu kỳ retry kế tiếp

            var needCheckA = state.CloseConfirmedA == false && state.TicketA.HasValue;
            var needCheckB = state.CloseConfirmedB == false && state.TicketB.HasValue;

            if (!needCheckA && !needCheckB)
            {
                state.IsResolved = true;
                continue;
            }

            if (needCheckA)
            {
                if (TryIsTicketStillOpen(state.TradeMapNameA, state.TicketA!.Value, out var isStillOpenA))
                {
                    if (!isStillOpenA)
                    {
                        state.CloseConfirmedA = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(state.TradeMapNameA) &&
                             state.PlatformA.HasValue &&
                             !string.IsNullOrWhiteSpace(state.TradeHwndA))
                    {
                        actions.Add(new PendingCloseRetryAction(
                            PairId: state.PairId,
                            IsAutoFlow: state.IsAutoFlow,
                            SlotNumber: state.SlotNumber,
                            Exchange: "A",
                            TradeMapName: state.TradeMapNameA!,
                            Platform: state.PlatformA.Value,
                            TradeHwnd: state.TradeHwndA!,
                            Ticket: state.TicketA!.Value,
                            TradeType: state.TradeTypeA,
                            Symbol: state.SymbolA,
                            Volume: state.VolumeA));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(state.TradeMapNameA) &&
                         state.PlatformA.HasValue &&
                         !string.IsNullOrWhiteSpace(state.TradeHwndA))
                {
                    actions.Add(new PendingCloseRetryAction(
                        PairId: state.PairId,
                        IsAutoFlow: state.IsAutoFlow,
                        SlotNumber: state.SlotNumber,
                        Exchange: "A",
                        TradeMapName: state.TradeMapNameA!,
                        Platform: state.PlatformA.Value,
                        TradeHwnd: state.TradeHwndA!,
                        Ticket: state.TicketA!.Value,
                        TradeType: state.TradeTypeA,
                        Symbol: state.SymbolA,
                        Volume: state.VolumeA));
                }
            }

            if (needCheckB)
            {
                if (TryIsTicketStillOpen(state.TradeMapNameB, state.TicketB!.Value, out var isStillOpenB))
                {
                    if (!isStillOpenB)
                    {
                        state.CloseConfirmedB = true;
                    }
                    else if (!string.IsNullOrWhiteSpace(state.TradeMapNameB) &&
                             state.PlatformB.HasValue &&
                             !string.IsNullOrWhiteSpace(state.TradeHwndB))
                    {
                        actions.Add(new PendingCloseRetryAction(
                            PairId: state.PairId,
                            IsAutoFlow: state.IsAutoFlow,
                            SlotNumber: state.SlotNumber,
                            Exchange: "B",
                            TradeMapName: state.TradeMapNameB!,
                            Platform: state.PlatformB.Value,
                            TradeHwnd: state.TradeHwndB!,
                            Ticket: state.TicketB!.Value,
                            TradeType: state.TradeTypeB,
                            Symbol: state.SymbolB,
                            Volume: state.VolumeB));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(state.TradeMapNameB) &&
                         state.PlatformB.HasValue &&
                         !string.IsNullOrWhiteSpace(state.TradeHwndB))
                {
                    actions.Add(new PendingCloseRetryAction(
                        PairId: state.PairId,
                        IsAutoFlow: state.IsAutoFlow,
                        SlotNumber: state.SlotNumber,
                        Exchange: "B",
                        TradeMapName: state.TradeMapNameB!,
                        Platform: state.PlatformB.Value,
                        TradeHwnd: state.TradeHwndB!,
                        Ticket: state.TicketB!.Value,
                        TradeType: state.TradeTypeB,
                        Symbol: state.SymbolB,
                        Volume: state.VolumeB));
                }
            }

            if (state.CloseConfirmedA && state.CloseConfirmedB)
            {
                TryBeginWaitAfterCloseFromPending(state);
                state.IsResolved = true;
                continue;
            }

            // Keep pending unresolved and continue retrying in subsequent checks
            // until both legs are confirmed closed.
        }

        return actions;
    }

    private void TryBeginWaitAfterCloseFromPending(PendingClosePairState state)
    {
        if (state.Origin == PendingCloseOrigin.Recovery)
        {
            _portfolioCoordinator.EndNonAutoCloseOperation(state.PairId);
            RaiseNonAutoCloseUiStateChanged();
        }

        if (!state.IsAutoFlow)
        {
            // Đóng tay theo pairId: remove slot khỏi coordinator (coordinator-only, không đụng auto state).
            if (state.Origin == PendingCloseOrigin.ManualPair)
            {
                _portfolioCoordinator.CloseSlotManually(state.PairId, DateTime.UtcNow);
                RaiseNonAutoCloseUiStateChanged();
                SafeVmLog($"[CYCLE][INFO] Manual close resolved: pairId={state.PairId}");
                RaiseCurrentPositionTextChanged();
            }

            return;
        }

        // Khi active cycle đã null, main close flow đã trigger BeginWaitAfterClose từ trước —
        // pending close finalize ở đây chỉ là safety-net polling, không cần log noise.
        if (_activeAutoCycle is null)
        {
            return;
        }

        if (!IsPendingCloseStateForActiveCycle(state))
        {
            SignalLogItems.Insert(0,
                $"    - [{DateTime.Now:HH:mm:ss.fff}] Close pending ignored: pair does not match active auto cycle ({state.PairId})");
            return;
        }

        var closeCompletedAtUtc = DateTime.UtcNow;
        var closeCompletedAtLocal = closeCompletedAtUtc.ToLocalTime();
        _tradingFlowEngine.BeginWaitAfterClose(
            closeCompletedAtUtc,
            _portfolioCoordinator.GlobalCooldownMinSec,
            _portfolioCoordinator.GlobalCooldownMaxSec,
            state.PairId);
        LogFlowTransitionIfChanged("begin-wait-after-close-from-pending");

        if (_tradingFlowEngine.ClosedAtUtc != closeCompletedAtUtc)
        {
            return;
        }

        SafeVmLog($"[CYCLE][INFO] Close cycle resolved: slot={state.SlotNumber} closeCompletedAtUtc={closeCompletedAtUtc:O}");

        var waitSeconds = _tradingFlowEngine.CurrentWaitSeconds;
        SignalLogItems.Insert(0,
            $"    - [{DateTime.Now:HH:mm:ss.fff}] Close pending confirmed by ticket check: pair={state.PairId}");
        SignalLogItems.Insert(0,
            SignalLogFormatter.FormatRandomWaitingTime(closeCompletedAtLocal, waitSeconds));
        ClearActiveAutoCycleOnCloseFinalize();
        RaiseCurrentPositionTextChanged();
        OnPropertyChanged(nameof(CurrentPhaseText));
    }

    private LivePairTradeState GetLivePairTradeState()
    {
        var tradeLeftResult = ReadTradesWithMmfLog(TradeTab.LeftPanel.TargetMapName);
        var tradeRightResult = ReadTradesWithMmfLog(TradeTab.RightPanel.TargetMapName);
        return GetLivePairTradeState(tradeLeftResult, tradeRightResult);
    }

    private LivePairTradeState GetLivePairTradeStateStrict()
    {
        try
        {
            var tradeLeftResult = ReadTradesWithMmfLog(TradeTab.LeftPanel.TargetMapName);
            var tradeRightResult = ReadTradesWithMmfLog(TradeTab.RightPanel.TargetMapName);
            return GetLivePairTradeState(tradeLeftResult, tradeRightResult);
        }
        catch (Exception ex)
        {
            SafeVmLog($"[VM][WARN] Suppressed exception at GetLivePairTradeStateStrict: {ex.Message}");
            return LivePairTradeState.MapUnavailableOrParseError;
        }
    }

    private static LivePairTradeState GetLivePairTradeState(
        SharedMapReadResult<TradeSharedRecord> tradeLeftResult,
        SharedMapReadResult<TradeSharedRecord> tradeRightResult)
    {
        if (!tradeLeftResult.IsMapAvailable
            || !tradeRightResult.IsMapAvailable
            || !tradeLeftResult.IsParseSuccess
            || !tradeRightResult.IsParseSuccess)
        {
            return LivePairTradeState.MapUnavailableOrParseError;
        }

        var hasOpenA = tradeLeftResult.Count > 0 && tradeLeftResult.Records.Count > 0;
        var hasOpenB = tradeRightResult.Count > 0 && tradeRightResult.Records.Count > 0;

        if (!hasOpenA && !hasOpenB)
        {
            return LivePairTradeState.BothFlat;
        }

        if (hasOpenA && hasOpenB)
        {
            return LivePairTradeState.BothOpen;
        }

        return hasOpenA
            ? LivePairTradeState.OnlyAOpen
            : LivePairTradeState.OnlyBOpen;
    }

    private void SyncTradingFlowWithLivePairState(LivePairTradeState pairState)
    {
        if (!IsTradingLogicEnabled)
        {
            return;
        }

        // Pending auto open in flight → engine phase là nguồn sự thật. MMF chưa
        // đủ dữ kiện để correct phase/side (1 leg có thể xuất hiện trước leg kia
        // hàng giây khi execution chậm). Không sync để tránh:
        //  - Ép phase WaitingClose -> WaitingOpen khi cả 2 leg chưa confirm (BothFlat thoáng qua).
        //  - Suy side sai từ TradeType của 1 leg đơn (OnlyAOpen/OnlyBOpen) khiến
        //    CurrentOpenMode bị đảo ngược chiều so với trigger gốc.
        // Pending có cơ chế resolve riêng: MarkOpenPairLegConfirmed (cả 2 confirm)
        // hoặc CollectPendingOpenTimeoutActions (timeout). Sau resolve, sync resume.
        if (TryGetUnresolvedAutoPendingOpenPairId(out _))
        {
            return;
        }

        if (pairState == LivePairTradeState.BothFlat)
        {
            if (_tradingFlowEngine.CurrentPhase != TradingFlowPhase.WaitingOpen)
            {
                _tradingFlowEngine.ForceWaitingOpen();
                LogFlowTransitionIfChanged("sync-force-waiting-open-both-flat");
                RaiseCurrentPositionTextChanged();
                OnPropertyChanged(nameof(CurrentPhaseText));
            }

            return;
        }

        if (pairState == LivePairTradeState.MapUnavailableOrParseError)
        {
            return;
        }

        // ▼ FIX: chỉ sync khi có ít nhất 1 lệnh được mở QUA TOOL
        //        Lệnh mở từ EA/bên ngoài không được ảnh hưởng state machine
        {
            // Đọc fresh để tính isToolFlat — không dùng cache tránh stale 1 nhịp
            var freshLeftSync = ReadTradesWithMmfLog(TradeTab.LeftPanel.TargetMapName);
            var freshRightSync = ReadTradesWithMmfLog(TradeTab.RightPanel.TargetMapName);
            var hasToolTicket = (freshLeftSync.IsMapAvailable && freshLeftSync.IsParseSuccess
                                 && freshLeftSync.Records.Any(r => _pairIdByTicket.ContainsKey(r.Ticket)))
                             || (freshRightSync.IsMapAvailable && freshRightSync.IsParseSuccess
                                 && freshRightSync.Records.Any(r => _pairIdByTicket.ContainsKey(r.Ticket)));

            if (!hasToolTicket)
            {
                // Không còn tool ticket. Nếu phase WaitingClose và cycle confirmed → ForceWaitingOpen.
                var isWaitingClose = _tradingFlowEngine.CurrentPhase == TradingFlowPhase.WaitingCloseFromGapBuy
                    || _tradingFlowEngine.CurrentPhase == TradingFlowPhase.WaitingCloseFromGapSell;
                if (isWaitingClose)
                {
                    var canFinalize = _activeAutoCloseRecoveryCycle is null
                        || IsActiveCloseRecoveryCycleClosed(freshLeftSync, freshRightSync, out _);
                    if (canFinalize)
                    {
                        SignalLogItems.Insert(0,
                            $"    - [{DateTime.Now:HH:mm:ss.fff}] Flow -> WaitingOpen: reason=tool-flat " +
                            $"phase={_tradingFlowEngine.CurrentPhase} " +
                            $"cycle={(_activeAutoCloseRecoveryCycle is null ? "null" : $"slot={_activeAutoCloseRecoveryCycle.Slot}")} " +
                            $"source=SyncTradingFlow");
                        _tradingFlowEngine.ForceWaitingOpen();
                        LogFlowTransitionIfChanged("sync-force-waiting-open-tool-flat");
                        RaiseCurrentPositionTextChanged();
                        OnPropertyChanged(nameof(CurrentPhaseText));
                    }
                }

                return;
            }
        }

        var side = ResolveLivePositionSideFromMaps();
        if (side == TradingPositionSide.None)
        {
            return;
        }

        if (_tradingFlowEngine.CurrentPhase != TradingFlowPhase.WaitingCloseFromGapBuy
            && _tradingFlowEngine.CurrentPhase != TradingFlowPhase.WaitingCloseFromGapSell)
        {
            _tradingFlowEngine.ForceWaitingClose(side);
            LogFlowTransitionIfChanged($"sync-force-waiting-close-side={side}");
            RaiseCurrentPositionTextChanged();
            OnPropertyChanged(nameof(CurrentPhaseText));
            return;
        }

        if (_tradingFlowEngine.CurrentPositionSide != side)
        {
            _tradingFlowEngine.ForceWaitingClose(side);
            LogFlowTransitionIfChanged($"sync-force-waiting-close-correct-side={side}");
            RaiseCurrentPositionTextChanged();
            OnPropertyChanged(nameof(CurrentPhaseText));
        }
    }

    private TradingPositionSide ResolveLivePositionSideFromMaps()
    {
        try
        {
            var left = _latestTradeLeftResult ?? ReadTradesWithMmfLog(TradeTab.LeftPanel.TargetMapName);
            var right = _latestTradeRightResult ?? ReadTradesWithMmfLog(TradeTab.RightPanel.TargetMapName);

            var candidate = left.Records.FirstOrDefault() ?? right.Records.FirstOrDefault();
            if (candidate is null)
            {
                return TradingPositionSide.None;
            }

            return candidate.TradeType == 0
                ? TradingPositionSide.Buy
                : TradingPositionSide.Sell;
        }
        catch (Exception ex)
        {
            SafeVmLog($"[VM][WARN] Suppressed exception at ResolveLivePositionSideFromMaps: {ex.Message}");
            return TradingPositionSide.None;
        }
    }

    /// <summary>
    /// Trả về true nếu có ít nhất 1 ticket đang mở trong latest results
    /// được mở QUA TOOL (có trong _pairIdByTicket).
    /// </summary>
    private bool HasAnyToolOpenedTicketInLatestResults()
    {
        var leftRecords = _latestTradeLeftResult?.Records ?? (IReadOnlyList<TradeSharedRecord>)[];
        var rightRecords = _latestTradeRightResult?.Records ?? (IReadOnlyList<TradeSharedRecord>)[];
        return leftRecords.Concat(rightRecords).Any(r => _pairIdByTicket.ContainsKey(r.Ticket));
    }

    /// <summary>
    /// Silent variant: cùng predicate với <see cref="HasUnresolvedAutoPendingOpenCycle"/>
    /// nhưng KHÔNG ghi log. Dùng cho các call site high-frequency (mỗi poll) để tránh
    /// spam log và tránh log message sai context ("Auto open blocked").
    /// </summary>
    private bool TryGetUnresolvedAutoPendingOpenPairId(out string? blockingPairId)
    {
        foreach (var state in _pendingOpenPairById.Values)
        {
            if (!state.IsAutoFlow)
            {
                continue;
            }

            if (state.IsResolved || state.TimeoutCloseTriggered)
            {
                continue;
            }

            if (state.OpenConfirmedA && state.OpenConfirmedB)
            {
                continue;
            }

            blockingPairId = state.PairId;
            return true;
        }

        blockingPairId = null;
        return false;
    }

    /// <summary>
    /// Trả về true nếu có ít nhất 1 pending open (auto hoặc manual) chưa confirm
    /// đủ 2 leg VÀ vẫn còn trong window OpenPendingTimeoutMs của chính nó. Dùng
    /// cho external-close watchdog để không đóng nhầm leg vừa mở khi leg còn lại
    /// đang chờ execution. Sau window, primary timeout handler
    /// (CollectPendingOpenTimeoutActions) tiếp quản.
    /// </summary>
    private bool TryGetPendingOpenInTimeoutWindow(out string? blockingPairId)
    {
        var now = DateTimeOffset.Now;
        foreach (var state in _pendingOpenPairById.Values)
        {
            if (state.IsResolved || state.TimeoutCloseTriggered)
            {
                continue;
            }

            if (state.OpenConfirmedA && state.OpenConfirmedB)
            {
                continue;
            }

            var timeoutMs = Math.Max(0, state.OpenPendingTimeoutMs);
            if (timeoutMs <= 0)
            {
                continue;
            }

            if (now - state.CreatedAtLocal >= TimeSpan.FromMilliseconds(timeoutMs))
            {
                continue;
            }

            blockingPairId = state.PairId;
            return true;
        }

        blockingPairId = null;
        return false;
    }

    /// <summary>
    /// Trả về true nếu có ít nhất 1 auto pending open cycle chưa hoàn tất.
    /// </summary>
    private bool HasUnresolvedAutoPendingOpenCycle(out string? blockingPairId)
    {
        // Phase 1: prefer coordinator quota check (covers pending + live + cap config).
        // Fall back to legacy _pendingOpenPairById iteration for safety net.
        if (_portfolioCoordinator.LiveAndPendingTotalCount >= _runtimeConfigState.CurrentMaxTotalOpens)
        {
            var pending = _portfolioCoordinator.PendingOpenSlots.FirstOrDefault();
            blockingPairId = pending?.PairId
                ?? _portfolioCoordinator.LiveSlots.FirstOrDefault()?.PairId;
            SafeVmLog($"[CYCLE][WARN] Auto open blocked by quota: total={_portfolioCoordinator.LiveAndPendingTotalCount}/{_runtimeConfigState.CurrentMaxTotalOpens} blockingPairId={blockingPairId}");
            return true;
        }

        foreach (var state in _pendingOpenPairById.Values)
        {
            if (!state.IsAutoFlow)
            {
                continue;
            }

            if (state.IsResolved || state.TimeoutCloseTriggered)
            {
                continue;
            }

            if (state.OpenConfirmedA && state.OpenConfirmedB)
            {
                continue;
            }

            blockingPairId = state.PairId;
            SafeVmLog($"[CYCLE][WARN] Auto open blocked by unresolved pending cycle: blockingPairId={blockingPairId}");
            return true;
        }

        blockingPairId = null;
        return false;
    }

    /// <summary>
    /// Trả về LivePairTradeState "tool-aware" dùng cho gate mở lệnh:
    /// nếu có lệnh đang mở nhưng KHÔNG phải do tool -> coi như BothFlat.
    /// </summary>
    private LivePairTradeState ComputeToolAwarePairStateForOpenGate(LivePairTradeState rawState)
    {
        if (rawState == LivePairTradeState.BothFlat
            || rawState == LivePairTradeState.MapUnavailableOrParseError)
        {
            return rawState;
        }

        return HasAnyToolOpenedTicketInLatestResults()
            ? rawState
            : LivePairTradeState.BothFlat;
    }

    private bool FinalizeCloseFlowIfPairFlat(string source)
    {
        var tradeLeftResult = ReadTradesWithMmfLog(TradeTab.LeftPanel.TargetMapName);
        var tradeRightResult = ReadTradesWithMmfLog(TradeTab.RightPanel.TargetMapName);
        if (!IsActiveCloseRecoveryCycleClosed(tradeLeftResult, tradeRightResult, out var blockReason))
        {
            SignalLogItems.Insert(0,
                $"    - [{DateTime.Now:HH:mm:ss.fff}] Close finalize blocked: active cycle not fully closed ({blockReason})");
            _closeBothFlatPollStreak = 0;
            return false;
        }

        var closeCompletedAtUtc = DateTime.UtcNow;
        var closeCompletedAtLocal = closeCompletedAtUtc.ToLocalTime();
        _tradingFlowEngine.BeginWaitAfterClose(
            closeCompletedAtUtc,
            _portfolioCoordinator.GlobalCooldownMinSec,
            _portfolioCoordinator.GlobalCooldownMaxSec,
            _activeAutoCloseRecoveryCycle?.PairIdA ?? _activeAutoCloseRecoveryCycle?.PairIdB);
        LogFlowTransitionIfChanged($"finalize-close-flow source={source}");

        if (_tradingFlowEngine.ClosedAtUtc != closeCompletedAtUtc)
        {
            return false;
        }

        SafeVmLog($"[CYCLE][INFO] Close cycle resolved: source={source} closeCompletedAtUtc={closeCompletedAtUtc:O}");

        var waitSeconds = _tradingFlowEngine.CurrentWaitSeconds;
        SignalLogItems.Insert(0,
            $"    - [{DateTime.Now:HH:mm:ss.fff}] Flow -> WaitingOpen after close confirmed ({source})");
        SignalLogItems.Insert(0,
            SignalLogFormatter.FormatRandomWaitingTime(closeCompletedAtLocal, waitSeconds));
        ClearActiveAutoCycleOnCloseFinalize();
        _closeBothFlatPollStreak = 0;
        RaiseCurrentPositionTextChanged();
        OnPropertyChanged(nameof(CurrentPhaseText));
        return true;
    }

    private bool FinalizeCloseFlowIfPairFlat(
        string source,
        SharedMapReadResult<TradeSharedRecord> tradeLeftResult,
        SharedMapReadResult<TradeSharedRecord> tradeRightResult)
    {
        if (!IsActiveCloseRecoveryCycleClosed(tradeLeftResult, tradeRightResult, out var blockReason))
        {
            SignalLogItems.Insert(0,
                $"    - [{DateTime.Now:HH:mm:ss.fff}] Close finalize blocked: active cycle not fully closed ({blockReason})");
            _closeBothFlatPollStreak = 0;
            return false;
        }

        var closeCompletedAtUtc = DateTime.UtcNow;
        var closeCompletedAtLocal = closeCompletedAtUtc.ToLocalTime();
        _tradingFlowEngine.BeginWaitAfterClose(
            closeCompletedAtUtc,
            _portfolioCoordinator.GlobalCooldownMinSec,
            _portfolioCoordinator.GlobalCooldownMaxSec,
            _activeAutoCloseRecoveryCycle?.PairIdA ?? _activeAutoCloseRecoveryCycle?.PairIdB);
        LogFlowTransitionIfChanged($"finalize-close-flow(cached) source={source}");

        if (_tradingFlowEngine.ClosedAtUtc != closeCompletedAtUtc)
        {
            return false;
        }

        var waitSeconds = _tradingFlowEngine.CurrentWaitSeconds;
        SignalLogItems.Insert(0,
            $"    - [{DateTime.Now:HH:mm:ss.fff}] Flow -> WaitingOpen after close confirmed ({source})");
        SignalLogItems.Insert(0,
            SignalLogFormatter.FormatRandomWaitingTime(closeCompletedAtLocal, waitSeconds));
        ClearActiveAutoCycleOnCloseFinalize();
        _closeBothFlatPollStreak = 0;
        RaiseCurrentPositionTextChanged();
        OnPropertyChanged(nameof(CurrentPhaseText));
        return true;
    }

    private void TryRecoverWaitingCloseFromPolling(LivePairTradeState pairState)
    {
        var phase = _tradingFlowEngine.CurrentPhase;
        var isWaitingClosePhase = phase == TradingFlowPhase.WaitingCloseFromGapBuy
            || phase == TradingFlowPhase.WaitingCloseFromGapSell;

        if (!isWaitingClosePhase)
        {
            _closeBothFlatPollStreak = 0;
            return;
        }

        // Đọc SharedMemory 1 lần duy nhất cho nhịp này — dùng lại cho cả isToolFlat và finalize
        var freshLeft = ReadTradesWithMmfLog(TradeTab.LeftPanel.TargetMapName);
        var freshRight = ReadTradesWithMmfLog(TradeTab.RightPanel.TargetMapName);

        if (pairState != LivePairTradeState.BothFlat)
        {
            // Tool-aware flat: nếu map khả dụng và không còn tool ticket → tiếp tục finalize
            var mapOk = freshLeft.IsMapAvailable && freshLeft.IsParseSuccess
                     && freshRight.IsMapAvailable && freshRight.IsParseSuccess;
            var isToolFlat = mapOk
                && !freshLeft.Records.Concat(freshRight.Records)
                                     .Any(r => _pairIdByTicket.ContainsKey(r.Ticket));

            if (!isToolFlat)
            {
                _closeBothFlatPollStreak = 0;
                return;
            }

            // Log chỉ khi streak = 0 (lần đầu detect tool-flat trong chuỗi này)
            if (_closeBothFlatPollStreak == 0)
            {
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] [RECOVER] tool-flat detected " +
                    $"(rawState={FormatLivePairTradeState(pairState)}) " +
                    $"cycle={(_activeAutoCloseRecoveryCycle is null ? "null" : $"slot={_activeAutoCloseRecoveryCycle.Slot}")} " +
                    $"proceeding with close cycle check");
            }
        }

        // Dùng freshLeft/freshRight đã đọc — không đọc lại lần nữa
        if (!IsActiveCloseRecoveryCycleClosed(freshLeft, freshRight, out var closeBlockReason))
        {
            _closeBothFlatPollStreak = 0;
            SignalLogItems.Insert(0,
                $"    - [{DateTime.Now:HH:mm:ss.fff}] Close reconcile blocked: active cycle not matched ({closeBlockReason})");
            return;
        }

        _closeBothFlatPollStreak++;
        if (_closeBothFlatPollStreak < StableBothFlatPollsRequired)
        {
            return;
        }

        // Dùng overload mới — truyền freshLeft/freshRight, không đọc lại
        var source = pairState == LivePairTradeState.BothFlat
            ? "polling watchdog/MMF"
            : "polling watchdog/tool-flat";
        var reconciled = FinalizeCloseFlowIfPairFlat(source, freshLeft, freshRight);
        if (!reconciled)
        {
            return;
        }

        SignalLogItems.Insert(0,
            $"    - [{DateTime.Now:HH:mm:ss.fff}] Close reconcile result: " +
            $"{(pairState == LivePairTradeState.BothFlat ? "BothFlat" : "tool-flat")} " +
            $"(stable polling {StableBothFlatPollsRequired}/{StableBothFlatPollsRequired})");
    }

    private void TryDetectAndHandleExternalPartialClose(LivePairTradeState currentPairState)
    {
        // Guard 1: chỉ chạy khi trading logic đang bật
        if (!IsTradingLogicEnabled)
        {
            _externalPartialCloseStreak = 0;
            _lastExternalCloseGuardBlockReason = string.Empty;
            return;
        }

        // Guard 6 (đặt sớm): có pending open (auto hoặc manual) đang chờ confirm
        // cả 2 leg VÀ còn trong window OpenPendingTimeoutMs. Trong giai đoạn này,
        // MMF state OnlyAOpen/OnlyBOpen KHÔNG đồng nghĩa "EA đóng 1 leg externally"
        // — nó chỉ phản ánh việc 1 leg xác nhận trước (execution chậm). Nếu hành
        // động ở đây, hệ thống sẽ đóng nhầm leg vừa mở. Hết window thì
        // CollectPendingOpenTimeoutActions xử lý timeout/compensation riêng.
        if (TryGetPendingOpenInTimeoutWindow(out var pendingPairId))
        {
            _externalPartialCloseStreak = 0;
            LogExternalCloseGuardBlockOnce($"Guard6: pendingOpen in flight, within timeout window ({pendingPairId})");
            return;
        }

        // Guard 2: phase phải là WaitingClose
        // Fallback: nếu phase bị reset tạm sang WaitingOpen (do SyncTradingFlowWithLivePairState
        // thấy BothFlat thoáng qua), vẫn cho chạy nếu auto pair đã confirm đủ 2 ticket
        var phase = _tradingFlowEngine.CurrentPhase;
        var isWaitingClose = phase == TradingFlowPhase.WaitingCloseFromGapBuy
            || phase == TradingFlowPhase.WaitingCloseFromGapSell;
        var hasConfirmedAutoPair = _activeAutoCycle is not null
            && _activeAutoCycle.TicketA.HasValue
            && _activeAutoCycle.TicketB.HasValue;
        if (!isWaitingClose && !hasConfirmedAutoPair && !_hadBothOpenRecently)
        {
            _externalPartialCloseStreak = 0;
            LogExternalCloseGuardBlockOnce($"Guard2: phase={phase}, noConfirmedAutoPair, notHadBothOpenRecently");
            return;
        }

        // Guard 3: không chạy khi tool đang trong tiến trình tự close bình thường
        // (AutoCloseOrderAsync đã set _activeAutoCloseRecoveryCycle)
        if (_activeAutoCloseRecoveryCycle is not null)
        {
            // Kiểm tra cycle có bị stale không (ticket đã đóng từ lâu, không còn liên quan)
            // bằng cách dùng _latestTradeLeftResult / _latestTradeRightResult đang có sẵn
            var leftForCycleCheck = _latestTradeLeftResult
                ?? ReadTradesWithMmfLog(TradeTab.LeftPanel.TargetMapName);
            var rightForCycleCheck = _latestTradeRightResult
                ?? ReadTradesWithMmfLog(TradeTab.RightPanel.TargetMapName);

            if (IsActiveCloseRecoveryCycleClosed(leftForCycleCheck, rightForCycleCheck, out _))
            {
                // Cycle đã stale (ticket không còn mở) — clear để không block watchdog
                _activeAutoCloseRecoveryCycle = null;
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] [EXTERNAL CLOSE] Stale activeAutoCloseRecoveryCycle cleared.");
            }
            else
            {
                // Cycle đang active (tool đang tự close) — block đúng
                _externalPartialCloseStreak = 0;
                LogExternalCloseGuardBlockOnce($"Guard3: activeAutoCloseRecoveryCycle active (slot={_activeAutoCloseRecoveryCycle.Slot})");
                return;
            }
        }

        // Guard 4: không chạy khi đang có close in flight từ watchdog này
        if (_externalPartialCloseInFlight)
        {
            return;
        }

        // Chỉ xử lý khi partial open (1 sàn còn mở, 1 sàn đã flat)
        var isPartialOpen = currentPairState == LivePairTradeState.OnlyAOpen
            || currentPairState == LivePairTradeState.OnlyBOpen;

        if (!isPartialOpen)
        {
            _externalPartialCloseStreak = 0;
            _lastExternalCloseGuardBlockReason = string.Empty;
            return;
        }

        // Guard 5: chỉ xử lý nếu lệnh còn lại được mở QUA TOOL
        // Nếu ticket không có trong _pairIdByTicket -> lệnh mở trực tiếp trên EA -> bỏ qua
        var remainingExchange = currentPairState == LivePairTradeState.OnlyAOpen ? "A" : "B";
        var remainingResult = remainingExchange == "A"
            ? (_latestTradeLeftResult ?? ReadTradesWithMmfLog(TradeTab.LeftPanel.TargetMapName))
            : (_latestTradeRightResult ?? ReadTradesWithMmfLog(TradeTab.RightPanel.TargetMapName));
        var remainingTicket = remainingResult?.Records.FirstOrDefault()?.Ticket;
        if (!remainingTicket.HasValue || !_pairIdByTicket.ContainsKey(remainingTicket.Value))
        {
            _externalPartialCloseStreak = 0;
            LogExternalCloseGuardBlockOnce($"Guard5: ticket={remainingTicket?.ToString() ?? "null"} not opened via tool");
            return;
        }

        // Reset log throttle khi tất cả guards đã pass
        _lastExternalCloseGuardBlockReason = string.Empty;

        _externalPartialCloseStreak++;
        if (_externalPartialCloseStreak < ExternalPartialCloseStreakRequired)
        {
            SignalLogItems.Insert(0,
                $"    - [{DateTime.Now:HH:mm:ss.fff}] [EXTERNAL CLOSE] Partial state detected ({FormatLivePairTradeState(currentPairState)}) streak {_externalPartialCloseStreak}/{ExternalPartialCloseStreakRequired}. Confirming...");
            return;
        }

        // Đủ streak — xác nhận EA đã close 1 leg từ bên ngoài
        _externalPartialCloseStreak = 0;
        _externalPartialCloseInFlight = true;

        // Lấy ticket của leg còn lại từ activeAutoCycle nếu có (auto flow)
        // Manual flow sẽ là null — SelectCloseCandidateForExchange sẽ tự tìm
        var knownTicket = _activeAutoCycle is not null
            ? (remainingExchange == "A" ? _activeAutoCycle.TicketA : _activeAutoCycle.TicketB)
            : null;

        SignalLogItems.Insert(0,
            $"    - [{DateTime.Now:HH:mm:ss.fff}] [EXTERNAL CLOSE] EA closed 1 leg externally. Scheduling close of remaining leg {remainingExchange} (ticket={knownTicket?.ToString() ?? remainingTicket.Value.ToString()}, mode={(_activeAutoCycle is not null ? "auto" : "manual")}, phase={phase}).");

        _ = Task.Run(() => CloseRemainingLegAfterExternalCloseAsync(remainingExchange, knownTicket));
    }

    // Log guard block chỉ khi reason thay đổi — tránh spam mỗi poll
    private void LogExternalCloseGuardBlockOnce(string reason)
    {
        if (string.Equals(_lastExternalCloseGuardBlockReason, reason, StringComparison.Ordinal))
        {
            return;
        }

        _lastExternalCloseGuardBlockReason = reason;
        SignalLogItems.Insert(0,
            $"    - [{DateTime.Now:HH:mm:ss.fff}] [EXTERNAL CLOSE] Blocked: {reason}");
    }

    private async Task CloseRemainingLegAfterExternalCloseAsync(string remainingExchange, ulong? knownTicket)
    {
        if (ShouldSkipTradeOp($"recovery-external-close exch={remainingExchange}"))
        {
            return;
        }

        try
        {
            var isExchangeA = string.Equals(remainingExchange, "A", StringComparison.OrdinalIgnoreCase);
            var tradeMapName = isExchangeA
                ? TradeTab.LeftPanel.TargetMapName
                : TradeTab.RightPanel.TargetMapName;
            var tradeHwnd = isExchangeA
                ? _runtimeConfigState.CurrentTradeHwndA
                : _runtimeConfigState.CurrentTradeHwndB;
            var platform = ResolveTradeLegPlatform(isExchangeA
                ? _runtimeConfigState.CurrentPlatformA
                : _runtimeConfigState.CurrentPlatformB);

            var selection = SelectCloseCandidateForExchange(
                exchangeLabel: remainingExchange,
                tradeMapName: tradeMapName,
                tradeHwnd: tradeHwnd);

            if (selection.Request is null)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    SignalLogItems.Insert(0,
                        $"    - [{DateTime.Now:HH:mm:ss.fff}] [EXTERNAL CLOSE] No candidate found for {remainingExchange}. Skipping."));
                return;
            }

            // Guard: kiểm tra ticket khớp để tránh close nhầm lệnh.
            // Chỉ áp dụng khi knownTicket có giá trị (auto flow).
            // Manual flow bỏ qua bước này vì không track ticket.
            if (knownTicket.HasValue && selection.Request.Ticket != knownTicket.Value)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    SignalLogItems.Insert(0,
                        $"    - [{DateTime.Now:HH:mm:ss.fff}] [EXTERNAL CLOSE] Ticket mismatch for {remainingExchange} (expected={knownTicket}, found={selection.Request.Ticket}). Skipping to avoid closing wrong trade."));
                return;
            }

            var appCloseRequestTimeLocal = DateTimeOffset.Now;
            var appCloseRequestRawMs = Environment.TickCount64;
            var slot = Math.Max(0, _autoSlot - 1);
            var recoveryPairId = _activeAutoCycle?.PairIdA
                ?? _activeAutoCycle?.PairIdB
                ?? (_pairIdByTicket.TryGetValue(selection.Request.Ticket, out var mappedPairId)
                    ? mappedPairId
                    : $"EXTERNAL-{selection.Request.Ticket}");

            _portfolioCoordinator.BeginNonAutoCloseOperation(recoveryPairId);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                RaiseNonAutoCloseUiStateChanged();
                // Set _activeAutoCloseRecoveryCycle để FinalizeCloseFlowIfPairFlat
                // hoạt động đúng sau khi close xong (không bị block bởi "no active close recovery cycle")
                var activeCycle = _activeAutoCycle;
                if (activeCycle is not null)
                {
                    // Auto flow: dùng thông tin đầy đủ từ active cycle
                    _activeAutoCloseRecoveryCycle = new ActiveAutoCycleState
                    {
                        Slot = activeCycle.Slot,
                        OpenedAtLocal = activeCycle.OpenedAtLocal,
                        PairIdA = isExchangeA ? activeCycle.PairIdA : null,
                        PairIdB = isExchangeA ? null : activeCycle.PairIdB,
                        TicketA = isExchangeA ? selection.Request.Ticket : null,
                        TicketB = isExchangeA ? null : selection.Request.Ticket,
                    };
                }
                else
                {
                    // Manual flow: tạo cycle tối thiểu chỉ với ticket vừa tìm được
                    // để IsActiveCloseRecoveryCycleClosed có thể verify sau khi close xong
                    _activeAutoCloseRecoveryCycle = new ActiveAutoCycleState
                    {
                        Slot = 0,
                        OpenedAtLocal = DateTimeOffset.Now,
                        PairIdA = isExchangeA ? selection.Request.Ticket.ToString() : null,
                        PairIdB = isExchangeA ? null : selection.Request.Ticket.ToString(),
                        TicketA = isExchangeA ? selection.Request.Ticket : null,
                        TicketB = isExchangeA ? null : selection.Request.Ticket,
                    };
                }

                RegisterPendingCloseRequest(
                    tradeMapName: tradeMapName,
                    ticket: selection.Request.Ticket,
                    tradeType: selection.TradeType ?? 0,
                    expectedPrice: null,
                    appCloseRequestTimeLocal: appCloseRequestTimeLocal,
                    appCloseRequestRawMs: appCloseRequestRawMs,
                    symbol: selection.Symbol,
                    volume: selection.Volume,
                    isAutoFlow: activeCycle is not null,
                    slotNumber: slot,
                    exchangeLabel: remainingExchange,
                    pairIdOverride: recoveryPairId);

                if (_pendingClosePairById.TryGetValue(recoveryPairId, out var recoveryState))
                {
                    recoveryState.Origin = PendingCloseOrigin.Recovery;
                }
            });

            var closeRequest = new TradeClosePairRequest(
                LegA: isExchangeA
                    ? new TradeCloseLegRequest(
                        Exchange: "A",
                        Platform: platform,
                        TradeHwnd: tradeHwnd,
                        Ticket: selection.Request.Ticket,
                        Action: TradeLegAction.Close,
                        DelayMs: 0,
                        RowIndex: selection.Request.RowIndex,
                        TradeMapName: selection.TradeMapName)
                    : null,
                LegB: isExchangeA
                    ? null
                    : new TradeCloseLegRequest(
                        Exchange: "B",
                        Platform: platform,
                        TradeHwnd: tradeHwnd,
                        Ticket: selection.Request.Ticket,
                        Action: TradeLegAction.Close,
                        DelayMs: 0,
                        RowIndex: selection.Request.RowIndex,
                        TradeMapName: selection.TradeMapName),
                Context: CreateRecoveryContext(
                    TradeExecutionReason.ExternalPartialCloseRecovery,
                    "external-partial-close",
                    recoveryPairId,
                    _activeAutoCycle?.Slot,
                    selection.Request.Ticket,
                    $"partial-state-remaining-{remainingExchange}"));

            var closeResult = await _tradeExecutionRouter.ClosePairAsync(closeRequest);

            if (closeResult.IsDispatchBlocked)
            {
                _portfolioCoordinator.EndNonAutoCloseOperation(recoveryPairId);
                RemovePendingCloseRequests(appCloseRequestRawMs);
                _activeAutoCloseRecoveryCycle = null;
                _externalPartialCloseInFlight = false;
                System.Windows.Application.Current.Dispatcher.Invoke(RaiseNonAutoCloseUiStateChanged);
                SafeVmLog($"[TRADE_GATE][BLOCKED] external CLOSE remainingMs={closeResult.GateRemainingMilliseconds}");
                return;
            }

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var success = closeResult.Legs.Any(x => x.Success);
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] [EXTERNAL CLOSE] Close {remainingExchange} ticket={selection.Request.Ticket} result: {(success ? "SUCCESS" : "FAILED")}");

                if (success)
                {
                    // Reset streak để polling reconcile (TryRecoverWaitingCloseFromPolling) xử lý tiếp
                    _closeBothFlatPollStreak = 0;
                }
                else
                {
                    // Close thất bại — reset _activeAutoCloseRecoveryCycle để không block finalize mãi mãi
                    _activeAutoCloseRecoveryCycle = null;
                }

                RaiseCurrentPositionTextChanged();
                OnPropertyChanged(nameof(CurrentPhaseText));
            });
        }
        catch (Exception ex)
        {
            SafeVmLog($"[VM][ERROR] Error closing remaining leg after external close: {ex}");
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] [EXTERNAL CLOSE] Error closing remaining leg: {ex.Message}");
                _activeAutoCloseRecoveryCycle = null;
            });
        }
        finally
        {
            _externalPartialCloseInFlight = false;
        }
    }

    private void LogFlowRecoveryState(
        LivePairTradeState pairState,
        string context,
        bool hadCloseCandidateA,
        bool hadCloseCandidateB,
        bool closeSuccessA,
        bool closeSuccessB,
        bool hasCloseSuccessBoth,
        bool immediateFlatObserved)
    {
        var now = DateTime.Now;
        var pairStateText = FormatLivePairTradeState(pairState);

        SignalLogItems.Insert(0,
            $"    - [{now:HH:mm:ss.fff}] {context}: candidates(A={hadCloseCandidateA},B={hadCloseCandidateB}), routerSuccess(A={closeSuccessA},B={closeSuccessB},both={hasCloseSuccessBoth})");
        SignalLogItems.Insert(0,
            $"    - [{now:HH:mm:ss.fff}] Close reconcile result: {pairStateText}");

        if (immediateFlatObserved)
        {
            SignalLogItems.Insert(0,
                $"    - [{now:HH:mm:ss.fff}] Post-close reconcile sees BothFlat (1/{StableBothFlatPollsRequired}); waiting for polling confirmation");
            return;
        }

        if (pairState == LivePairTradeState.OnlyAOpen || pairState == LivePairTradeState.OnlyBOpen)
        {
            SignalLogItems.Insert(0,
                $"    - [{now:HH:mm:ss.fff}] Post-close reconcile sees partial open trades; waiting for polling confirmation");
            return;
        }

        if (pairState == LivePairTradeState.BothOpen)
        {
            SignalLogItems.Insert(0,
                $"    - [{now:HH:mm:ss.fff}] Post-close reconcile still sees open trades; waiting for polling confirmation");
            return;
        }

        SignalLogItems.Insert(0,
            $"    - [{now:HH:mm:ss.fff}] Post-close reconcile unavailable (map/parse); waiting for polling confirmation");
    }

    private static string FormatLivePairTradeState(LivePairTradeState pairState)
        => pairState switch
        {
            LivePairTradeState.BothFlat => "BothFlat",
            LivePairTradeState.OnlyAOpen => "OnlyAOpen",
            LivePairTradeState.OnlyBOpen => "OnlyBOpen",
            LivePairTradeState.BothOpen => "BothOpen",
            _ => "Unknown/Error"
        };

    private async Task ExecutePendingCloseRetryActionsAsync(
        IReadOnlyList<PendingCloseRetryAction> actions,
        CancellationToken cancellationToken)
    {
        foreach (var action in actions)
        {
            await RetryCloseLegByPendingAsync(action, cancellationToken);
        }
    }

    private async Task RetryCloseLegByPendingAsync(PendingCloseRetryAction action, CancellationToken cancellationToken)
    {
        if (!_pendingClosePairById.TryGetValue(action.PairId, out var state) || state.IsResolved)
        {
            return;
        }

        if (ShouldSkipTradeOp($"recovery-retry-close pairId={action.PairId}"))
        {
            return;
        }

        state.RetryChecks++;

        var appCloseRequestTimeLocal = DateTimeOffset.Now;
        var appCloseRequestRawMs = Environment.TickCount64;
        if (action.TradeType.HasValue)
        {
            var isExchangeA = string.Equals(action.Exchange, "A", StringComparison.OrdinalIgnoreCase);
            var expectedClose = ResolveExpectedClosePrice(_runtimeConfigState.CurrentDashboardMetrics, isExchangeA, action.TradeType.Value);
            RegisterPendingCloseRequest(
                tradeMapName: action.TradeMapName,
                ticket: action.Ticket,
                tradeType: action.TradeType.Value,
                expectedPrice: expectedClose,
                appCloseRequestTimeLocal: appCloseRequestTimeLocal,
                appCloseRequestRawMs: appCloseRequestRawMs,
                symbol: action.Symbol,
                volume: action.Volume,
                isAutoFlow: action.IsAutoFlow,
                slotNumber: action.SlotNumber,
                exchangeLabel: action.Exchange);
        }

        var rowIndex = FindRowIndexForTicket(action.TradeMapName, action.Ticket);

        var closeResult = await _tradeExecutionRouter.ClosePairAsync(
            new TradeClosePairRequest(
                LegA: string.Equals(action.Exchange, "A", StringComparison.OrdinalIgnoreCase)
                    ? new TradeCloseLegRequest(
                        Exchange: "A",
                        Platform: action.Platform,
                        TradeHwnd: action.TradeHwnd,
                        Ticket: action.Ticket,
                        Action: TradeLegAction.Close,
                        RowIndex: rowIndex,
                        TradeMapName: action.TradeMapName)
                    : null,
                LegB: string.Equals(action.Exchange, "B", StringComparison.OrdinalIgnoreCase)
                    ? new TradeCloseLegRequest(
                        Exchange: "B",
                        Platform: action.Platform,
                        TradeHwnd: action.TradeHwnd,
                        Ticket: action.Ticket,
                        Action: TradeLegAction.Close,
                        RowIndex: rowIndex,
                        TradeMapName: action.TradeMapName)
                    : null,
                Context: CreateRecoveryContext(
                    TradeExecutionReason.PendingCloseRetry,
                    "pending-close-retry",
                    action.PairId,
                    action.SlotNumber,
                    action.Ticket,
                    $"retry-check-{state.RetryChecks}")),
            cancellationToken);

        if (closeResult.IsDispatchBlocked)
        {
            RemovePendingCloseRequests(appCloseRequestRawMs);
            SafeVmLog($"[TRADE_GATE][BLOCKED] retry CLOSE pairId={action.PairId} remainingMs={closeResult.GateRemainingMilliseconds}");
            return;
        }

        NotifyOpenCloseFailures("CLOSE", closeResult, action.PairId);

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            SignalLogItems.Insert(0,
                $"    - [{DateTime.Now:HH:mm:ss.fff}] Close pending retry #{state.RetryChecks}: pair={action.PairId}, exchange={action.Exchange}, success={closeResult.Success}");
        });

        Debug.WriteLine($"[ExecClose][PendingRetry] pairId={action.PairId}, exchange={action.Exchange}, retryChecks={state.RetryChecks}, success={closeResult.Success}");
    }

    private bool TryIsTicketStillOpen(string? tradeMapName, ulong ticket, out bool isStillOpen)
    {
        isStillOpen = false;

        if (string.IsNullOrWhiteSpace(tradeMapName))
        {
            return false;
        }

        var result = ReadTradesWithMmfLog(tradeMapName);
        if (!result.IsMapAvailable || !result.IsParseSuccess)
        {
            return false;
        }

        isStillOpen = result.Records.Any(x => x.Ticket == ticket);
        return true;
    }

    private bool ShouldApplyTradeResult(string mapName, SharedMapReadResult<TradeSharedRecord> result)
    {
        if (!result.IsMapAvailable || !result.IsParseSuccess)
        {
            _lastTradeTimestampByMap.Remove(mapName ?? string.Empty);
            return true;
        }

        var key = mapName ?? string.Empty;
        if (_lastTradeTimestampByMap.TryGetValue(key, out var lastTimestamp) && lastTimestamp == result.Timestamp)
        {
            return false;
        }

        _lastTradeTimestampByMap[key] = result.Timestamp;
        return true;
    }

    private bool ShouldApplyHistoryResult(string mapName, SharedMapReadResult<HistorySharedRecord> result)
    {
        if (!result.IsMapAvailable || !result.IsParseSuccess)
        {
            _lastHistoryTimestampByMap.Remove(mapName ?? string.Empty);
            return true;
        }

        var key = mapName ?? string.Empty;
        if (_lastHistoryTimestampByMap.TryGetValue(key, out var lastTimestamp) && lastTimestamp == result.Timestamp)
        {
            return false;
        }

        _lastHistoryTimestampByMap[key] = result.Timestamp;
        return true;
    }

    private void ApplyTradeResult(
        OrderPanelStatusViewModel panel,
        SharedMapReadResult<TradeSharedRecord> result,
        DashboardMetrics? snapshot,
        bool isExchangeA,
        int point)
    {
        if (!result.IsMapAvailable)
        {
            panel.SetMapNotFound(panel.TargetMapName);
            RebuildTradeRealtimeProfitRows();
            return;
        }

        if (!result.IsParseSuccess)
        {
            panel.SetParseError(result.ErrorMessage ?? "Lỗi parse dữ liệu");
            RebuildTradeRealtimeProfitRows();
            return;
        }

        if (result.Count == 0)
        {
            panel.SetEmpty();
            RebuildTradeRealtimeProfitRows();
            return;
        }

        if (result.Records.Count == 0)
        {
            panel.SetParseError("Lỗi parse dữ liệu: count > 0 nhưng không có records");
            RebuildTradeRealtimeProfitRows();
            return;
        }

        RegisterOpenExpectedForNewTickets(panel.TargetMapName, result.Records);
        var appGeneratedRecords = result.Records
            .Where(x => IsAppGeneratedTicket(x.Ticket))
            .ToList();

        if (appGeneratedRecords.Count == 0)
        {
            panel.SetEmpty();
            RebuildTradeRealtimeProfitRows();
            return;
        }

        var rows = BuildTradeRows(appGeneratedRecords, appGeneratedRecords.Count, result.Timestamp, snapshot, isExchangeA, point);
        panel.SetTradeData(rows);

        // Forward the same realtime per-leg profit shown in the Trade tab;
        // coordinator sums A+B into the slot profit used by close priority and TP.
        foreach (var record in appGeneratedRecords)
        {
            _portfolioCoordinator.UpdateProfit(record.Ticket, CalculateTradeProfit(record, snapshot, isExchangeA, point));
        }

        RebuildTradeRealtimeProfitRows();
    }

    private void RebuildTradeRealtimeProfitRows()
    {
        var sumByStt = new Dictionary<int, double>();
        var pairIdByStt = new Dictionary<int, string>();

        AccumulateTradeProfitRows(TradeTab.LeftPanel.TradeRows, sumByStt, pairIdByStt);
        AccumulateTradeProfitRows(TradeTab.RightPanel.TradeRows, sumByStt, pairIdByStt);

        var rebuilt = sumByStt
            .OrderBy(x => x.Key)
            .Select(x => new TradePairRealtimeProfitRowViewModel(
                stt: x.Key.ToString(CultureInfo.InvariantCulture),
                profitRealtime: x.Value.ToString("0.00", CultureInfo.InvariantCulture),
                pairId: pairIdByStt.TryGetValue(x.Key, out var pid) ? pid : string.Empty))
            .ToList();

        // Preserve existing row/button instances. Clearing this collection every UI refresh
        // can replace a Button between mouse-down and mouse-up, causing WPF to lose the click.
        var desiredStt = rebuilt.Select(row => row.Stt).ToHashSet(StringComparer.Ordinal);
        for (var i = TradeRealtimeProfitRows.Count - 1; i >= 0; i--)
        {
            if (!desiredStt.Contains(TradeRealtimeProfitRows[i].Stt))
            {
                TradeRealtimeProfitRows.RemoveAt(i);
            }
        }

        for (var targetIndex = 0; targetIndex < rebuilt.Count; targetIndex++)
        {
            var desired = rebuilt[targetIndex];
            var existingIndex = -1;
            for (var i = 0; i < TradeRealtimeProfitRows.Count; i++)
            {
                if (string.Equals(TradeRealtimeProfitRows[i].Stt, desired.Stt, StringComparison.Ordinal))
                {
                    existingIndex = i;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                TradeRealtimeProfitRows.Insert(targetIndex, desired);
                continue;
            }

            var existing = TradeRealtimeProfitRows[existingIndex];
            existing.Update(desired.ProfitRealtime, desired.PairId);
            if (existingIndex != targetIndex)
            {
                TradeRealtimeProfitRows.Move(existingIndex, targetIndex);
            }
        }
    }

    private static void AccumulateTradeProfitRows(
        IEnumerable<TradeRowViewModel> rows,
        Dictionary<int, double> sumByStt,
        Dictionary<int, string> pairIdByStt)
    {
        foreach (var row in rows)
        {
            if (!int.TryParse(row.Stt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stt) || stt <= 0)
            {
                continue;
            }

            if (!double.TryParse(row.Profit, NumberStyles.Float, CultureInfo.InvariantCulture, out var profit))
            {
                continue;
            }

            sumByStt[stt] = sumByStt.TryGetValue(stt, out var existing)
                ? existing + profit
                : profit;

            if (!string.IsNullOrWhiteSpace(row.PairId) && row.PairId != "-"
                && !pairIdByStt.ContainsKey(stt))
            {
                pairIdByStt[stt] = row.PairId;
            }
        }
    }

    private void ApplyHistoryResult(
        OrderPanelStatusViewModel panel,
        SharedMapReadResult<HistorySharedRecord> result,
        int point)
    {
        if (!result.IsMapAvailable)
        {
            panel.SetMapNotFound(panel.TargetMapName);
            RebuildHistoryRealtimeProfitRows();
            return;
        }

        if (!result.IsParseSuccess)
        {
            panel.SetParseError(result.ErrorMessage ?? "Lỗi parse dữ liệu");
            RebuildHistoryRealtimeProfitRows();
            return;
        }

        if (result.Count == 0)
        {
            panel.SetEmpty();
            RebuildHistoryRealtimeProfitRows();
            return;
        }

        if (result.Records.Count == 0)
        {
            panel.SetParseError("Lỗi parse dữ liệu: count > 0 nhưng không có records");
            RebuildHistoryRealtimeProfitRows();
            return;
        }

        RegisterCloseExecutionForNewHistoryTickets(panel.TargetMapName, result.Records);
        var appGeneratedRecords = result.Records
            .Where(x => IsAppGeneratedTicket(x.Ticket))
            .OrderByDescending(x => x.CloseEaTimeLocal)
            .ToList();

        if (appGeneratedRecords.Count == 0)
        {
            panel.SetEmpty();
            RebuildHistoryRealtimeProfitRows();
            return;
        }

        var rows = BuildHistoryRows(appGeneratedRecords, appGeneratedRecords.Count, result.Timestamp, point);
        panel.SetHistoryData(rows);
        RebuildHistoryRealtimeProfitRows();
    }

    private void RebuildHistoryRealtimeProfitRows()
    {
        var sumByStt = new Dictionary<int, (double Profit, double ProfitDollar)>();

        AccumulateHistoryProfitRows(HistoryTab.LeftPanel.HistoryRows, sumByStt);
        AccumulateHistoryProfitRows(HistoryTab.RightPanel.HistoryRows, sumByStt);

        var totalProfit = sumByStt.Values.Sum(x => x.Profit);
        var totalProfitDollar = sumByStt.Values.Sum(x => x.ProfitDollar);
        HistoryRealtimeProfitSummary =
            $"{totalProfit.ToString("0.00", CultureInfo.InvariantCulture)} | {totalProfitDollar.ToString("0.00", CultureInfo.InvariantCulture)} $";

        var rebuilt = sumByStt
            .OrderBy(x => x.Key)
            .Select(x => new HistoryPairProfitRowViewModel(
                stt: x.Key.ToString(CultureInfo.InvariantCulture),
                profit: x.Value.Profit.ToString("0.00", CultureInfo.InvariantCulture),
                profitDollar: x.Value.ProfitDollar.ToString("0.00", CultureInfo.InvariantCulture)))
            .ToList();

        HistoryRealtimeProfitRows.Clear();
        foreach (var row in rebuilt)
        {
            HistoryRealtimeProfitRows.Add(row);
        }
    }

    private static void AccumulateHistoryProfitRows(
        IEnumerable<HistoryRowViewModel> rows,
        Dictionary<int, (double Profit, double ProfitDollar)> sumByStt)
    {
        foreach (var row in rows)
        {
            if (!int.TryParse(row.Stt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stt) || stt <= 0)
            {
                continue;
            }

            if (!double.TryParse(row.Profit, NumberStyles.Float, CultureInfo.InvariantCulture, out var profit))
            {
                continue;
            }

            if (!double.TryParse(row.FeeSpread, NumberStyles.Float, CultureInfo.InvariantCulture, out var profitDollar))
            {
                continue;
            }

            if (sumByStt.TryGetValue(stt, out var existing))
            {
                sumByStt[stt] = (existing.Profit + profit, existing.ProfitDollar + profitDollar);
            }
            else
            {
                sumByStt[stt] = (profit, profitDollar);
            }
        }
    }

    private bool IsAppGeneratedTicket(ulong ticket)
        => _pairIdByTicket.TryGetValue(ticket, out var pairId)
           && !string.IsNullOrWhiteSpace(pairId)
           && !string.Equals(pairId, "-", StringComparison.Ordinal);

    private void RegisterOpenExpectedForNewTickets(string tradeMapName, IReadOnlyList<TradeSharedRecord> records)
    {
        var key = NormalizeMapName(tradeMapName);
        if (!_knownTradeTicketsByMap.TryGetValue(key, out var knownTickets))
        {
            knownTickets = [];
            _knownTradeTicketsByMap[key] = knownTickets;
        }

        var currentTickets = records.Select(r => r.Ticket).ToHashSet();
        var newRecords = records.Where(r => !knownTickets.Contains(r.Ticket)).OrderBy(r => r.TimeMsc).ToList();
        var removedTickets = knownTickets.Where(ticket => !currentTickets.Contains(ticket)).ToList();

        foreach (var removedTicket in removedTickets)
        {
            _profitSnapshotByTicket.Remove(removedTicket);
        }

        foreach (var newRecord in newRecords)
        {
            _profitSnapshotByTicket.TryAdd(newRecord.Ticket, newRecord.Profit);

            if (!TryConsumePendingOpenRequest(key, newRecord, out var pendingRequest, out var matchKey))
            {
                continue;
            }

            _openRequestByTicket[newRecord.Ticket] = pendingRequest;
            _pairIdByTicket[newRecord.Ticket] = pendingRequest.PairId;
            MarkOpenPairLegConfirmed(pendingRequest, newRecord.Ticket, newRecord.Symbol, newRecord.Lot);
            var openExecutionMs = ComputeExecutionMilliseconds(
                newRecord.OpenEaTimeLocal,
                pendingRequest.AppOpenRequestRawMs);
            if (openExecutionMs.HasValue)
            {
                _openExecutionMsByTicket[newRecord.Ticket] = openExecutionMs.Value;
            }

            Debug.WriteLine(
                $"[ExecOpen][Raw] key={matchKey}, ticket={newRecord.Ticket}, app_open_request_time_raw={pendingRequest.AppOpenRequestTimeLocal:O}, app_open_request_raw_ms={pendingRequest.AppOpenRequestRawMs}, " +
                $"open_ea_time_local_raw={newRecord.OpenEaTimeLocal}");

            Debug.WriteLine(
                $"[ExecOpen][Match] key={matchKey}, ticket={newRecord.Ticket}, app_open_request_time={pendingRequest.AppOpenRequestTimeLocal:O}, " +
                $"open_ea_time_local={newRecord.OpenEaTimeLocal}, app_open_request_raw_ms={pendingRequest.AppOpenRequestRawMs}, " +
                $"open_execution={(openExecutionMs.HasValue ? openExecutionMs.Value.ToString(CultureInfo.InvariantCulture) : "--")}");

            // Phase 2: Open Confirm signal log
            var openSlippage = CalculateTradeOpenSlippage(newRecord, _runtimeConfigState.CurrentPoint);
            NotifySlippageAndDelayIfNeeded(
                isOpen: true,
                exchange: pendingRequest.ExchangeLabel,
                ticket: newRecord.Ticket,
                pairId: pendingRequest.PairId,
                slippagePt: openSlippage,
                executionMs: openExecutionMs);
            var openTypeText = SignalLogFormatter.TradeTypeString(newRecord.TradeType);
            SignalLogItems.Insert(0, SignalLogFormatter.FormatOpenConfirm(
                DateTime.Now,
                ResolveDisplayStt(pendingRequest.PairId, pendingRequest.SlotNumber),
                pendingRequest.ExchangeLabel,
                openTypeText,
                newRecord.Symbol,
                newRecord.Price,
                openSlippage,
                openExecutionMs));

            if (pendingRequest.IsAutoFlow)
            {
                if (!_openConfirmBySlot.TryGetValue(pendingRequest.SlotNumber, out var openCycle))
                {
                    openCycle = new OpenConfirmCycleState();
                    _openConfirmBySlot[pendingRequest.SlotNumber] = openCycle;
                }

                if (string.Equals(pendingRequest.ExchangeLabel, "A", StringComparison.OrdinalIgnoreCase))
                {
                    openCycle.HasA = true;
                }
                else if (string.Equals(pendingRequest.ExchangeLabel, "B", StringComparison.OrdinalIgnoreCase))
                {
                    openCycle.HasB = true;
                }

                openCycle.HoldingSeconds = Math.Max(openCycle.HoldingSeconds, pendingRequest.HoldingSeconds);

                if (openCycle.HasA && openCycle.HasB && !openCycle.HoldingLogged && openCycle.HoldingSeconds > 0)
                {
                    openCycle.HoldingLogged = true;
                    SignalLogItems.Insert(0,
                        SignalLogFormatter.FormatRandomHoldingTime(DateTime.Now, openCycle.HoldingSeconds));
                }
            }
        }

        _knownTradeTicketsByMap[key] = currentTickets;
    }

    private void MarkOpenPairLegConfirmed(PendingOpenRequest pendingRequest, ulong ticket, string symbol, double volume)
    {
        if (!_pendingOpenPairById.TryGetValue(pendingRequest.PairId, out var state))
        {
            return;
        }

        if (string.Equals(pendingRequest.ExchangeLabel, "A", StringComparison.OrdinalIgnoreCase))
        {
            state.OpenConfirmedA = true;
            state.OpenedTicketA = ticket;
            state.SymbolA ??= symbol;
            state.VolumeA ??= volume;
            state.TradeMapNameA ??= pendingRequest.TradeMapName;
            state.TradeTypeA ??= pendingRequest.TradeType;
        }
        else if (string.Equals(pendingRequest.ExchangeLabel, "B", StringComparison.OrdinalIgnoreCase))
        {
            state.OpenConfirmedB = true;
            state.OpenedTicketB = ticket;
            state.SymbolB ??= symbol;
            state.VolumeB ??= volume;
            state.TradeMapNameB ??= pendingRequest.TradeMapName;
            state.TradeTypeB ??= pendingRequest.TradeType;
        }

        SafeVmLog(
            $"[CYCLE][INFO] Pending open leg confirmed: pairId={pendingRequest.PairId} exchange={pendingRequest.ExchangeLabel} " +
            $"ticket={ticket} symbol={symbol} volume={volume.ToString(CultureInfo.InvariantCulture)} " +
            $"bothConfirmed={(state.OpenConfirmedA && state.OpenConfirmedB)}");

        if (_activeAutoCycle is not null
            && pendingRequest.IsAutoFlow
            && pendingRequest.SlotNumber == _activeAutoCycle.Slot)
        {
            if (string.Equals(pendingRequest.ExchangeLabel, "A", StringComparison.OrdinalIgnoreCase))
            {
                _activeAutoCycle.PairIdA = pendingRequest.PairId;
                _activeAutoCycle.TicketA = ticket;
            }
            else if (string.Equals(pendingRequest.ExchangeLabel, "B", StringComparison.OrdinalIgnoreCase))
            {
                _activeAutoCycle.PairIdB = pendingRequest.PairId;
                _activeAutoCycle.TicketB = ticket;
            }
        }

        if (state.OpenConfirmedA && state.OpenConfirmedB)
        {
            state.IsResolved = true;
            SafeVmLog($"[CYCLE][INFO] Pending open resolved: pairId={pendingRequest.PairId} ticketA={state.OpenedTicketA} ticketB={state.OpenedTicketB}");

            // Phase 1: notify coordinator so slot transitions PendingOpen → Live and tickets are stored.
            if (state.IsAutoFlow && state.OpenedTicketA.HasValue && state.OpenedTicketB.HasValue)
            {
                _portfolioCoordinator.MarkSlotOpenConfirmed(
                    pendingRequest.PairId,
                    state.OpenedTicketA.Value,
                    state.OpenedTicketB.Value,
                    DateTime.UtcNow);
                SafeVmLog($"[SLOT][INFO] Slot open confirmed: pairId={pendingRequest.PairId} ticketA={state.OpenedTicketA.Value} ticketB={state.OpenedTicketB.Value}");
            }

            // Persist current tickets to Supabase for recovery on restart
            if (state.OpenedTicketA.HasValue && state.OpenedTicketB.HasValue)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _configService.SaveCurrentTicksAsync(
                            state.OpenedTicketA.Value.ToString(),
                            state.OpenedTicketB.Value.ToString());
                        await PersistCurrentSlotsSnapshotAsync("open-confirmed");
                        SafeVmLog($"[RECOVERY][INFO] Saved current ticks to DB: ticketA={state.OpenedTicketA.Value} ticketB={state.OpenedTicketB.Value}");
                    }
                    catch (Exception ex)
                    {
                        SafeVmLog($"[RECOVERY][WARN] Failed to save current ticks: {ex.Message}");
                    }
                });
            }
        }
    }

    private void RegisterCloseExecutionForNewHistoryTickets(string historyMapName, IReadOnlyList<HistorySharedRecord> records)
    {
        var key = NormalizeMapName(historyMapName);
        var tradeMapName = ResolveTradeMapNameFromHistoryMap(historyMapName);

        if (!_knownHistoryTicketsByMap.TryGetValue(key, out var knownTickets))
        {
            knownTickets = [];
            _knownHistoryTicketsByMap[key] = knownTickets;
        }

        var currentTickets = records.Select(r => r.Ticket).ToHashSet();
        var newRecords = records
            .Where(r => !knownTickets.Contains(r.Ticket))
            .OrderBy(r => r.CloseTimeMsc)
            .ToList();

        foreach (var record in newRecords)
        {
            if (_closeExecutionMsByTicket.ContainsKey(record.Ticket))
            {
                continue;
            }

            if (!TryConsumePendingCloseRequest(tradeMapName, record, out var pendingRequest, out var matchKey))
            {
                continue;
            }

            _closeRequestByTicket[record.Ticket] = pendingRequest;
            _pairIdByTicket[record.Ticket] = pendingRequest.PairId;
            var closeExecutionMs = ComputeExecutionMilliseconds(
                record.CloseEaTimeLocal,
                pendingRequest.AppCloseRequestRawMs);
            if (closeExecutionMs.HasValue)
            {
                _closeExecutionMsByTicket[record.Ticket] = closeExecutionMs.Value;
            }

            Debug.WriteLine(
                $"[ExecClose][Raw] key={matchKey}, ticket={record.Ticket}, app_close_request_time_raw={pendingRequest.AppCloseRequestTimeLocal:O}, app_close_request_raw_ms={pendingRequest.AppCloseRequestRawMs}, " +
                $"close_ea_time_local_raw={record.CloseEaTimeLocal}");

            Debug.WriteLine(
                $"[ExecClose][Match] key={matchKey}, ticket={record.Ticket}, app_close_request_time={pendingRequest.AppCloseRequestTimeLocal:O}, " +
                $"close_ea_time_local={record.CloseEaTimeLocal}, app_close_request_raw_ms={pendingRequest.AppCloseRequestRawMs}, " +
                $"close_execution={(closeExecutionMs.HasValue ? closeExecutionMs.Value.ToString(CultureInfo.InvariantCulture) : "--")}");

            // Phase 2: Close Confirm signal log
            var closeSlippage = CalculateHistoryCloseSlippage(record, _runtimeConfigState.CurrentPoint);
            NotifySlippageAndDelayIfNeeded(
                isOpen: false,
                exchange: pendingRequest.ExchangeLabel,
                ticket: record.Ticket,
                pairId: pendingRequest.PairId,
                slippagePt: closeSlippage,
                executionMs: closeExecutionMs);
            var closeTypeText = SignalLogFormatter.TradeTypeString(record.TradeType);
            SignalLogItems.Insert(0, SignalLogFormatter.FormatCloseConfirm(
                DateTime.Now,
                ResolveDisplayStt(pendingRequest.PairId, pendingRequest.SlotNumber),
                pendingRequest.ExchangeLabel,
                closeTypeText,
                record.Symbol,
                record.ClosePrice,
                closeSlippage,
                closeExecutionMs));

            if (pendingRequest.IsAutoFlow)
            {
                if (!_closeConfirmBySlot.TryGetValue(pendingRequest.SlotNumber, out var closeCycle))
                {
                    closeCycle = new CloseConfirmCycleState();
                    _closeConfirmBySlot[pendingRequest.SlotNumber] = closeCycle;
                }

                if (string.Equals(pendingRequest.ExchangeLabel, "A", StringComparison.OrdinalIgnoreCase))
                {
                    closeCycle.HasA = true;
                }
                else if (string.Equals(pendingRequest.ExchangeLabel, "B", StringComparison.OrdinalIgnoreCase))
                {
                    closeCycle.HasB = true;
                }

                if (closeCycle.HasA && closeCycle.HasB && !closeCycle.WaitingStarted)
                {
                    closeCycle.WaitingStarted = true;
                    var closeCompletedAtUtc = DateTime.UtcNow;
                    var closeCompletedAtLocal = closeCompletedAtUtc.ToLocalTime();
                    _tradingFlowEngine.BeginWaitAfterClose(
                        closeCompletedAtUtc,
                        _portfolioCoordinator.GlobalCooldownMinSec,
                        _portfolioCoordinator.GlobalCooldownMaxSec,
                        pendingRequest.PairId);
                    LogFlowTransitionIfChanged("history-close-confirm-both-legs");

                    if (_tradingFlowEngine.ClosedAtUtc == closeCompletedAtUtc)
                    {
                        var waitSeconds = _tradingFlowEngine.CurrentWaitSeconds;
                        SignalLogItems.Insert(0,
                            SignalLogFormatter.FormatRandomWaitingTime(closeCompletedAtLocal, waitSeconds));
                        RaiseCurrentPositionTextChanged();
                        OnPropertyChanged(nameof(CurrentPhaseText));
                    }
                }
            }
        }

        _knownHistoryTicketsByMap[key] = currentTickets;
    }

    private IEnumerable<TradeRowViewModel> BuildTradeRows(
        IReadOnlyList<TradeSharedRecord> records,
        int count,
        ulong timestamp,
        DashboardMetrics? snapshot,
        bool isExchangeA,
        int point)
        => records.Select(record =>
        {
            _openExecutionMsByTicket.TryGetValue(record.Ticket, out var openExecutionMsValue);
            var openExecutionMs = _openExecutionMsByTicket.ContainsKey(record.Ticket) ? openExecutionMsValue : (long?)null;
            _openRequestByTicket.TryGetValue(record.Ticket, out var openRequest);
            var pairId = _pairIdByTicket.TryGetValue(record.Ticket, out var value) ? value : "-";
            var stt = ResolveStt(pairId);
            var tradeOpenSlippage = CalculateTradeOpenSlippage(record, point);

            return new TradeRowViewModel(
                stt: stt,
                pairId: pairId,
                timestamp: FormatRawTimestamp(timestamp),
                count: count.ToString(CultureInfo.InvariantCulture),
                symbol: record.Symbol,
                ticket: record.Ticket.ToString(CultureInfo.InvariantCulture),
                type: FormatTradeType(record.TradeType),
                lot: FormatLot(record.Lot),
                price: FormatPrice(record.Price),
                sl: FormatPrice(record.Sl),
                tp: FormatPrice(record.Tp),
                slippage: FormatTradeOpenSlippageDebug(record, openRequest, point, tradeOpenSlippage),
                profit: FormatProfit(CalculateTradeProfit(record, snapshot, isExchangeA, point)),
                feeSpread: ResolveTradeProfitSnapshot(record.Ticket, record.Profit),
                time: FormatTradeTime(record.TimeMsc),
                openEaTimeLocal: FormatEaLocalTime(record.OpenEaTimeLocal),
                openExecution: FormatOpenExecutionDebug(record.OpenEaTimeLocal, openRequest?.AppOpenRequestRawMs, openExecutionMs));
        });

    private string ResolveTradeProfitSnapshot(ulong ticket, double currentProfit)
    {
        if (!_profitSnapshotByTicket.TryGetValue(ticket, out var snapshotProfit))
        {
            snapshotProfit = currentProfit;
            _profitSnapshotByTicket[ticket] = snapshotProfit;
        }

        return FormatProfit(snapshotProfit);
    }

    private IEnumerable<HistoryRowViewModel> BuildHistoryRows(
        IReadOnlyList<HistorySharedRecord> records,
        int count,
        ulong timestamp,
        int point)
        => records.Select(record =>
        {
            _openExecutionMsByTicket.TryGetValue(record.Ticket, out var openExecutionMsValue);
            _closeExecutionMsByTicket.TryGetValue(record.Ticket, out var closeExecutionMsValue);
            var openExecutionMs = _openExecutionMsByTicket.ContainsKey(record.Ticket) ? openExecutionMsValue : (long?)null;
            var closeExecutionMs = _closeExecutionMsByTicket.ContainsKey(record.Ticket) ? closeExecutionMsValue : (long?)null;
            _closeRequestByTicket.TryGetValue(record.Ticket, out var closeRequest);
            var pairId = _pairIdByTicket.TryGetValue(record.Ticket, out var value) ? value : "-";
            var stt = ResolveStt(pairId);
            var historyCloseSlippage = CalculateHistoryCloseSlippage(record, point);

            return new HistoryRowViewModel(
                stt: stt,
                pairId: pairId,
                timestamp: FormatRawTimestamp(timestamp),
                count: count.ToString(CultureInfo.InvariantCulture),
                symbol: record.Symbol,
                ticket: record.Ticket.ToString(CultureInfo.InvariantCulture),
                type: FormatTradeType(record.TradeType),
                volume: FormatRawDouble(record.Volume),
                openPrice: FormatRawDouble(record.OpenPrice),
                closePrice: FormatRawDouble(record.ClosePrice),
                openSlippage: FormatOptionalProfit(CalculateHistoryOpenSlippage(record, point)),
                closeSlippage: FormatHistoryCloseSlippageDebug(record, closeRequest, point, historyCloseSlippage),
                profit: FormatRawDouble(CalculateHistoryProfit(record)),
                feeSpread: FormatRawDouble(record.Profit),
                commission: FormatRawDouble(record.Commission),
                sl: FormatRawDouble(record.Sl),
                tp: FormatRawDouble(record.Tp),
                openTime: FormatTradeTime(record.OpenTimeMsc),
                closeTime: FormatTradeTime(record.CloseTimeMsc),
                closeEaTimeLocal: FormatEaLocalTime(record.CloseEaTimeLocal),
                openExecution: FormatExecutionMs(openExecutionMs),
                closeExecution: FormatCloseExecutionDebug(record.CloseEaTimeLocal, closeRequest?.AppCloseRequestRawMs, closeExecutionMs));
        });

    // stt hiển thị cho signal log = đúng STT mà grid history/trade dùng (ResolveStt theo pairId).
    // Cùng pairId → cùng số → dò ngược log ↔ grid khớp. Fallback số legacy khi pairId chưa hợp lệ.
    private int ResolveDisplayStt(string? pairId, int fallback)
        => int.TryParse(
            ResolveStt(pairId ?? string.Empty),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : fallback;

    private string ResolveStt(string pairId)
    {
        if (string.IsNullOrWhiteSpace(pairId) || string.Equals(pairId, "-", StringComparison.Ordinal))
        {
            return "-";
        }

        if (_sttByPairId.TryGetValue(pairId, out var existing))
        {
            return existing.ToString(CultureInfo.InvariantCulture);
        }

        var next = _nextStt++;
        _sttByPairId[pairId] = next;
        return next.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatOpenExecutionDebug(ulong openEaTimeLocal, long? appOpenRequestRawMs, long? openExecutionMs)
    {
        return FormatExecutionMs(openExecutionMs);
    }

    private static string FormatCloseExecutionDebug(ulong closeEaTimeLocal, long? appCloseRequestRawMs, long? closeExecutionMs)
    {
        return FormatExecutionMs(closeExecutionMs);
    }

    private static string FormatTradeOpenSlippageDebug(TradeSharedRecord record, PendingOpenRequest? openRequest, int point, double? slippage)
    {
        return FormatOptionalProfit(slippage);
    }

    private static string FormatHistoryCloseSlippageDebug(HistorySharedRecord record, PendingCloseRequest? closeRequest, int point, double? slippage)
    {
        return FormatOptionalProfit(slippage);
    }

    private bool TryConsumePendingOpenRequest(
        string tradeMapName,
        TradeSharedRecord tradeRecord,
        out PendingOpenRequest pendingRequest,
        out string matchKey)
    {
        pendingRequest = default!;
        matchKey = string.Empty;

        if (!_pendingOpenRequestsByMap.TryGetValue(tradeMapName, out var pendingList) || pendingList.Count == 0)
        {
            return false;
        }

        PruneStalePendingOpenRequests(pendingList);
        if (pendingList.Count == 0)
        {
            _pendingOpenRequestsByMap.Remove(tradeMapName);
            return false;
        }

        var strictCandidates = pendingList
            .Where(x => x.TradeType == tradeRecord.TradeType)
            .Where(x => IsNullOrMatch(x.Symbol, tradeRecord.Symbol))
            .Where(x => IsNullOrVolumeMatch(x.Volume, tradeRecord.Lot))
            .ToList();

        var selected = strictCandidates
            .OrderBy(x => Math.Abs(tradeRecord.TimeMsc > long.MaxValue
                ? long.MaxValue - x.AppOpenRequestUnixMs
                : (long)tradeRecord.TimeMsc - x.AppOpenRequestUnixMs))
            .FirstOrDefault();

        if (selected is null)
        {
            var typeOnlyCandidates = pendingList
                .Where(x => x.TradeType == tradeRecord.TradeType)
                .ToList();

            selected = typeOnlyCandidates
                .OrderBy(x => Math.Abs(tradeRecord.TimeMsc > long.MaxValue
                    ? long.MaxValue - x.AppOpenRequestUnixMs
                    : (long)tradeRecord.TimeMsc - x.AppOpenRequestUnixMs))
                .FirstOrDefault();
        }

        if (selected is null)
        {
            var fallbackCandidates = pendingList
                .Where(x => Math.Abs(x.AppOpenRequestUnixMs - DateTimeOffset.Now.ToUnixTimeMilliseconds()) <= 30_000)
                .ToList();

            selected = fallbackCandidates
                .OrderBy(x => Math.Abs(tradeRecord.TimeMsc > long.MaxValue
                    ? long.MaxValue - x.AppOpenRequestUnixMs
                    : (long)tradeRecord.TimeMsc - x.AppOpenRequestUnixMs))
                .FirstOrDefault();
        }

        if (selected is null)
        {
            var pendingDump = string.Join(" | ", pendingList.Select(x =>
                $"type={x.TradeType},symbol={x.Symbol ?? "-"},vol={(x.Volume.HasValue ? x.Volume.Value.ToString(CultureInfo.InvariantCulture) : "-")},app_open_raw={x.AppOpenRequestRawMs}"));

            Debug.WriteLine(
                $"[ExecOpen][Reject] map={tradeMapName}, ticket={tradeRecord.Ticket}, symbol={tradeRecord.Symbol}, type={tradeRecord.TradeType}, volume={tradeRecord.Lot.ToString(CultureInfo.InvariantCulture)}, reason=no_matching_pending_by_keys");
            Debug.WriteLine($"[ExecOpen][Reject][PendingDump] map={tradeMapName}, pending={pendingDump}");
            return false;
        }

        pendingList.Remove(selected);
        if (pendingList.Count == 0)
        {
            _pendingOpenRequestsByMap.Remove(tradeMapName);
        }

        pendingRequest = selected;
        matchKey = $"map={tradeMapName};symbol={tradeRecord.Symbol};type={tradeRecord.TradeType};volume={tradeRecord.Lot.ToString(CultureInfo.InvariantCulture)};mode=best_effort";
        return true;
    }

    private bool TryConsumePendingCloseRequest(
        string tradeMapName,
        HistorySharedRecord historyRecord,
        out PendingCloseRequest pendingRequest,
        out string matchKey)
    {
        pendingRequest = default!;
        matchKey = string.Empty;

        if (!_pendingCloseRequestsByMap.TryGetValue(tradeMapName, out var pendingList) || pendingList.Count == 0)
        {
            return false;
        }

        PruneStalePendingCloseRequests(pendingList);
        if (pendingList.Count == 0)
        {
            _pendingCloseRequestsByMap.Remove(tradeMapName);
            return false;
        }

        var ticketMatch = pendingList.FirstOrDefault(x => x.Ticket.HasValue && x.Ticket.Value == historyRecord.Ticket);
        if (ticketMatch is not null)
        {
            pendingList.Remove(ticketMatch);
            if (pendingList.Count == 0)
            {
                _pendingCloseRequestsByMap.Remove(tradeMapName);
            }

            pendingRequest = ticketMatch;
            matchKey = $"ticket={historyRecord.Ticket};map={tradeMapName};mode=ticket";
            return true;
        }

        var candidates = pendingList
            .Where(x => x.TradeType == historyRecord.TradeType)
            .Where(x => IsNullOrMatch(x.Symbol, historyRecord.Symbol))
            .Where(x => IsNullOrVolumeMatch(x.Volume, historyRecord.Volume))
            .ToList();

        if (candidates.Count == 0)
        {
            Debug.WriteLine(
                $"[ExecClose][Reject] map={tradeMapName}, ticket={historyRecord.Ticket}, symbol={historyRecord.Symbol}, type={historyRecord.TradeType}, volume={historyRecord.Volume.ToString(CultureInfo.InvariantCulture)}, reason=no_matching_pending_by_keys");
            return false;
        }

        var selected = candidates
            .OrderBy(x => Math.Abs(historyRecord.CloseTimeMsc > long.MaxValue
                ? long.MaxValue - x.AppCloseRequestUnixMs
                : (long)historyRecord.CloseTimeMsc - x.AppCloseRequestUnixMs))
            .FirstOrDefault();

        if (selected is null)
        {
            return false;
        }

        pendingList.Remove(selected);
        if (pendingList.Count == 0)
        {
            _pendingCloseRequestsByMap.Remove(tradeMapName);
        }

        pendingRequest = selected;
        matchKey = $"map={tradeMapName};symbol={historyRecord.Symbol};type={historyRecord.TradeType};volume={historyRecord.Volume.ToString(CultureInfo.InvariantCulture)};mode=fallback";
        return true;
    }

    private static long? ComputeExecutionMilliseconds(
        ulong eaTimeLocalMs,
        long appRequestRawMs)
    {
        if (eaTimeLocalMs == 0)
        {
            return null;
        }

        var eaRawMs = eaTimeLocalMs > long.MaxValue ? long.MaxValue : (long)eaTimeLocalMs;
        return eaRawMs - appRequestRawMs;
    }

    private static bool IsNullOrMatch(string? pending, string? actual)
        => string.IsNullOrWhiteSpace(pending)
           || (!string.IsNullOrWhiteSpace(actual)
               && string.Equals(pending.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool IsNullOrVolumeMatch(double? pendingVolume, double actualVolume)
        => !pendingVolume.HasValue || Math.Abs(pendingVolume.Value - actualVolume) < 0.0000001d;

    private static void PruneStalePendingOpenRequests(List<PendingOpenRequest> pendingList)
    {
        var now = DateTimeOffset.Now;
        pendingList.RemoveAll(x => now - x.AppOpenRequestTimeLocal > TimeSpan.FromSeconds(30));
    }

    private static void PruneStalePendingCloseRequests(List<PendingCloseRequest> pendingList)
    {
        var now = DateTimeOffset.Now;
        pendingList.RemoveAll(x => now - x.AppCloseRequestTimeLocal > TimeSpan.FromSeconds(30));
    }

    private double? CalculateTradeOpenSlippage(TradeSharedRecord record, int point)
    {
        if (!_openRequestByTicket.TryGetValue(record.Ticket, out var expected)
            || expected.TradeType != record.TradeType
            || !expected.ExpectedPrice.HasValue)
        {
            return null;
        }

        var pointValue = Math.Max(1, point);
        // Open BUY  (tradeType==0): (Expected Ask − Fill Ask) × point
        // Open SELL (tradeType==1): (Fill Bid − Expected Bid) × point
        var slippage = record.TradeType == 0
            ? (expected.ExpectedPrice.Value - record.Price) * pointValue
            : (record.Price - expected.ExpectedPrice.Value) * pointValue;

        _openSlippageByTicket[record.Ticket] = slippage;
        return slippage;
    }

    private double? CalculateHistoryOpenSlippage(HistorySharedRecord record, int point)
    {
        if (!_openSlippageByTicket.TryGetValue(record.Ticket, out var openSlippage))
        {
            return null;
        }

        return openSlippage;
    }

    private double? CalculateHistoryCloseSlippage(HistorySharedRecord record, int point)
    {
        if (!_closeRequestByTicket.TryGetValue(record.Ticket, out var expected)
            || expected.TradeType != record.TradeType
            || !expected.ExpectedPrice.HasValue)
        {
            return null;
        }

        var pointValue = Math.Max(1, point);
        return record.TradeType == 0
            ? (record.ClosePrice - expected.ExpectedPrice.Value) * pointValue
            : (expected.ExpectedPrice.Value - record.ClosePrice) * pointValue;
    }

    private static double CalculateTradeProfit(
        TradeSharedRecord record,
        DashboardMetrics? snapshot,
        bool isExchangeA,
        int point)
    {
        if (snapshot is null)
        {
            return 0d;
        }

        var exchange = isExchangeA ? snapshot.ExchangeA : snapshot.ExchangeB;
        var openPrice = (decimal)record.Price;
        var pointValue = Math.Max(1, point);
        var isBuy = record.TradeType == 0;

        if (isBuy)
        {
            if (!exchange.Bid.HasValue)
            {
                return 0d;
            }

            return (double)((exchange.Bid.Value - openPrice) * pointValue);
        }

        if (!exchange.Ask.HasValue)
        {
            return 0d;
        }

        return (double)((openPrice - exchange.Ask.Value) * pointValue);
    }

    private static double CalculateHistoryProfit(HistorySharedRecord record)
        => record.TradeType == 0
            ? (record.ClosePrice - record.OpenPrice) * 100d
            : (record.OpenPrice - record.ClosePrice) * 100d;

    private static string FormatTradeType(int tradeType)
        => tradeType == 0 ? "BUY" : "SELL";

    private static string FormatRawTimestamp(ulong timestamp)
        => timestamp.ToString(CultureInfo.InvariantCulture);

    private static string FormatRawDouble(double value)
        => value.ToString("0.#####", CultureInfo.InvariantCulture);

    private static string FormatLot(double value)
        => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatPrice(double value)
        => value.ToString("0.00000", CultureInfo.InvariantCulture);

    private static string FormatProfit(double value)
        => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatOptionalProfit(double? value)
        => value.HasValue
            ? value.Value.ToString("0.00", CultureInfo.InvariantCulture)
            : "-";

    private static string FormatExecutionMs(long? value)
        => value.HasValue
            ? $"{value.Value.ToString(CultureInfo.InvariantCulture)} ms"
            : "-";

    private string FormatTradeTime(ulong timeMsc)
    {
        if (timeMsc == 0)
        {
            return "-";
        }

        try
        {
            var clamped = timeMsc > long.MaxValue ? long.MaxValue : (long)timeMsc;
            return DateTimeOffset.FromUnixTimeMilliseconds(clamped).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            SafeVmLog($"[VM][WARN] Suppressed exception at FormatTradeTime: {ex.Message}");
            return FormatRawTimestamp(timeMsc);
        }
    }

    private string FormatEaLocalTime(ulong timeMsc)
    {
        if (timeMsc == 0)
        {
            return "-";
        }

        try
        {
            var clamped = timeMsc > long.MaxValue ? long.MaxValue : (long)timeMsc;
            var time = TimeSpan.FromMilliseconds(clamped);
            return $"{time.Hours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
        }
        catch (Exception ex)
        {
            SafeVmLog($"[VM][WARN] Suppressed exception at FormatEaLocalTime: {ex.Message}");
            return FormatRawTimestamp(timeMsc);
        }
    }

    private async Task InitializeRuntimeConfigAsync()
    {
        const string InlineDbHostName = "win-vps-01";
        try
        {
            LoadingMessage = "Đang tải cấu hình runtime...";

            var result = await _configService.LoadByMachineHostNameAsync();
            if (result.IsSuccess && result.Exists)
            {
                ClearConfigError();
                _runtimeConfigState.Update(
                    machineHostName: result.MachineHostName,
                    mapName1: result.MapName1,
                    mapName2: result.MapName2,
                    platformA: result.PlatformA,
                    platformB: result.PlatformB,
                    point: result.Point,
                    openPts: result.OpenPts,
                    confirmGapPts: result.ConfirmGapPts,
                    holdConfirmMs: result.HoldConfirmMs,
                    openPriceFreezeMs: result.OpenPriceFreezeMs,
                    closePts: result.ClosePts,
                    closeConfirmGapPts: result.CloseConfirmGapPts,
                    closeTpProfit: result.CloseTpProfit,
                    closeConfirmTpProfit: result.CloseConfirmTpProfit,
                    closeHoldConfirmMs: result.CloseHoldConfirmMs,
                    closePriceFreezeMs: result.ClosePriceFreezeMs,
                    startTimeHold: result.StartTimeHold,
                    endTimeHold: result.EndTimeHold,
                    confirmLatencyMs: result.ConfirmLatencyMs,
                    maxGap: result.MaxGap,
                    limitMaxGap: result.LimitMaxGap,
                    maxSpread: result.MaxSpread,
                    openMaxTimesTick: result.OpenMaxTimesTick,
                    closeMaxTimesTick: result.CloseMaxTimesTick,
                    openPendingTimeMs: result.OpenPendingTimeMs,
                    closePendingTimeMs: result.ClosePendingTimeMs,
                    delayOpenAMs: result.DelayOpenAMs,
                    delayOpenBMs: result.DelayOpenBMs,
                    delayCloseAMs: result.DelayCloseAMs,
                    delayCloseBMs: result.DelayCloseBMs,
                    openNumberOfQualifyingTimes: result.OpenNumberOfQualifyingTimes,
                    closeNumberOfQualifyingTimes: result.CloseNumberOfQualifyingTimes,
                    maxLifeTimeBySecond: result.MaxLifeTimeBySecond,
                    closeMaxTpProfit: result.CloseMaxTpProfit,
                    limitMaxTp: result.LimitMaxTp,
                    freezeLastN: result.FreezeLastN,
                    sosTriggerProfitPts: result.SosTriggerProfitPts,
                    sosTriggerAfterSeconds: result.SosTriggerAfterSeconds,
                    sosCloseConfirmGapPts: result.SosCloseConfirmGapPts,
                    sosCloseGapPts: result.SosCloseGapPts,
                    oppositeSideLockSeconds: result.OppositeSideLockSeconds,
                    oppositeOpenMinDistancePts: result.OppositeOpenMinDistancePts,
                    rdStartSameActionLockSeconds: result.RdStartSameActionLockSeconds,
                    rdEndSameActionLockSeconds: result.RdEndSameActionLockSeconds,
                    rdStartPostCloseLockSeconds: result.RdStartPostCloseLockSeconds,
                    rdEndPostCloseLockSeconds: result.RdEndPostCloseLockSeconds,
                    rdStartPostOpenLockSeconds: result.RdStartPostOpenLockSeconds,
                    rdEndPostOpenLockSeconds: result.RdEndPostOpenLockSeconds);
                _runtimeConfigState.UpdateScheduleSleeping(result.ScheduleSleepingJson);
                _runtimeConfigState.UpdateQuota(
                    result.MaxTotalOpens,
                    result.MaxBuyOpens,
                    result.MaxSellOpens);
                _runtimeConfigState.UpdateManualTradeHwnd(result.ManualHwndColumns);
                IsShowConfigVisible = result.IsShowConfig == 1;
                ResetTradingLogicState();

                // Recovery: prefer multi-slot snapshot, fallback to legacy single pair fields.
                var recoveredFromSlots = await TryRecoverSlotsFromConfigAsync(result.CurrentSlots);
                if (!recoveredFromSlots)
                {
                    await TryRecoverTicketsFromConfigAsync(result.CurrentTickA, result.CurrentTickB);
                }

                if (string.Equals(result.MachineHostName, InlineDbHostName, StringComparison.OrdinalIgnoreCase))
                {
                    DbInlineData =
                        $"[DB] id={result.ConfigId} | hostname={result.MachineHostName} | point={result.Point} | open_pts={result.OpenPts} | open_confirm_gap_pts={result.ConfirmGapPts} | opposite_open_min_distance_pts={result.OppositeOpenMinDistancePts} | rd_same_action={result.RdStartSameActionLockSeconds}..{result.RdEndSameActionLockSeconds}s | open_hold_confirm_ms={result.HoldConfirmMs} | open_price_freeze_ms={result.OpenPriceFreezeMs} | open_max_times_tick={result.OpenMaxTimesTick} | close_pts={result.ClosePts} | close_confirm_gap_pts={result.CloseConfirmGapPts} | close_tp_profit={result.CloseTpProfit} | close_confirm_tp_profit={result.CloseConfirmTpProfit} | close_hold_confirm_ms={result.CloseHoldConfirmMs} | close_price_freeze_ms={result.ClosePriceFreezeMs} | close_max_times_tick={result.CloseMaxTimesTick} | freeze_last_n={result.FreezeLastN} | start_time_hold={result.StartTimeHold} | end_time_hold={result.EndTimeHold} | sans={result.SansJson}";
                    IsDbInlineDataVisible = true;
                }
                else
                {
                    DbInlineData = string.Empty;
                    IsDbInlineDataVisible = false;
                }

                LoadingMessage = "Đang chờ dữ liệu shared memory...";
                return;
            }

            if (string.Equals(result.MachineHostName, InlineDbHostName, StringComparison.OrdinalIgnoreCase))
            {
                DbInlineData = "[DB] Không lấy được dữ liệu config từ DB cho hostname win-vps-01";
                IsDbInlineDataVisible = true;
            }
            else
            {
                DbInlineData = string.Empty;
                IsDbInlineDataVisible = false;
            }

            var warning = string.IsNullOrWhiteSpace(result.MachineHostName)
                ? "[Config] Không lấy được host name local để tải config."
                : $"[Config] Không tìm thấy config cho host name hiện tại: {result.MachineHostName}";

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ShowConfigError(warning);
                if (!string.IsNullOrWhiteSpace(result.MachineHostName))
                {
                    _runtimeConfigState.Update(
                        result.MachineHostName,
                        _runtimeConfigState.MapName1,
                        _runtimeConfigState.MapName2,
                        _runtimeConfigState.CurrentPoint);
                }

                LoadingMessage = "Đang chờ dữ liệu shared memory...";
            });
        }
        catch (Exception ex)
        {
            SafeVmLog($"[VM][ERROR] InitializeRuntimeConfigAsync failed: {ex}");
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ShowConfigError($"[Config] Lỗi tải config runtime: {ex.Message}");
                LoadingMessage = "Đang chờ dữ liệu shared memory...";
            });
        }
    }

    private async Task StartExchangeReaderSafeAsync(IExchangePairReader exchangePairReader)
    {
        try
        {
            await exchangePairReader.StartAsync();
        }
        catch (Exception ex)
        {
            SafeVmLog($"[VM][ERROR] StartExchangeReaderSafeAsync failed: {ex}");
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ShowConfigError($"[Reader] Không thể start shared memory reader: {ex.Message}");
                LoadingMessage = "Không thể kết nối shared memory. Mở Config hoặc kiểm tra log.";
            });
        }
    }

    private void OnSnapshotReceived(object? sender, SharedMemorySnapshot snapshot)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var metrics = _dashboardMetricsMapper.Map(snapshot);
            _runtimeConfigState.UpdateDashboardMetrics(metrics);
            IsLoading = false;

            // Throttle UI-heavy work; diagnostics and logic still run every tick.
            var nowTickMs = Environment.TickCount64;
            var canRenderUi = (nowTickMs - _lastSnapshotUiRenderTickMs) >= SnapshotUiRenderMinIntervalMs;
            if (canRenderUi)
            {
                _lastSnapshotUiRenderTickMs = nowTickMs;
                BindDashboardMetrics(metrics);
            }
            LogLatencyAnomalyIfNeeded(metrics);
            LogStaleTickIfNeeded(metrics);
            LogPerfSummaryIfDue(metrics);
            if (canRenderUi)
            {
                RefreshTradeRowsFromSnapshot(metrics, _runtimeConfigState.CurrentPoint);
            }
            SignalEntryGuard.TrackPriceHistory(_priceHistory, metrics);

            if (!IsTradingLogicEnabled)
            {
                return;
            }

            // Gốc fix: profit nuôi quyết định TP phải tươi MỖI tick, đồng bộ với gap-snapshot
            // dùng ngay dưới — KHÔNG kẹp vào throttle render UI (canRenderUi ~200ms). Nếu không,
            // _tpState.Profits bị nhồi-lặp giá trị cũ và có thể latch spike ảo.
            RefreshSlotProfitsEveryTick(metrics, _runtimeConfigState.CurrentPoint);

            // Phase 1: business decision flows through PortfolioCoordinator directly.
            // Adapter (_tradingFlowEngine) only used for legacy scalar reads (CurrentPhase, etc.).
            var portfolioResult = _portfolioCoordinator.ProcessSnapshot(
                new GapSignalSnapshot(
                    metrics.TimestampUtc,
                    metrics.ExchangeA.Bid,
                    metrics.ExchangeA.Ask,
                    metrics.ExchangeB.Bid,
                    metrics.ExchangeB.Ask,
                    metrics.GapBuy,
                    metrics.GapSell,
                    _runtimeConfigState.CurrentPoint),
                new GapSignalConfirmationConfig(
                    ConfirmGapPts: _runtimeConfigState.CurrentConfirmGapPts,
                    OpenPts: _runtimeConfigState.CurrentOpenPts,
                    HoldConfirmMs: _runtimeConfigState.CurrentHoldConfirmMs,
                    CloseConfirmGapPts: _runtimeConfigState.CurrentCloseConfirmGapPts,
                    ClosePts: _runtimeConfigState.CurrentClosePts,
                    CloseConfirmTpProfit: _runtimeConfigState.CurrentCloseConfirmTpProfit,
                    CloseTpProfit: _runtimeConfigState.CurrentCloseTpProfit,
                    CloseMaxTpProfit: _runtimeConfigState.CurrentCloseMaxTpProfit,
                    CloseHoldConfirmMs: _runtimeConfigState.CurrentCloseHoldConfirmMs,
                    StartTimeHold: _runtimeConfigState.CurrentStartTimeHold,
                    EndTimeHold: _runtimeConfigState.CurrentEndTimeHold,
                    OpenMaxTimesTick: _runtimeConfigState.CurrentOpenMaxTimesTick,
                    CloseMaxTimesTick: _runtimeConfigState.CurrentCloseMaxTimesTick,
                    LimitMaxGap: _runtimeConfigState.CurrentLimitMaxGap,
                    LimitMaxTp: _runtimeConfigState.CurrentLimitMaxTp,
                    SosTriggerProfitPts: _runtimeConfigState.CurrentSosTriggerProfitPts,
                    SosTriggerAfterSeconds: _runtimeConfigState.CurrentSosTriggerAfterSeconds,
                    SosCloseConfirmGapPts: _runtimeConfigState.CurrentSosCloseConfirmGapPts,
                    SosCloseGapPts: _runtimeConfigState.CurrentSosCloseGapPts));

            // Reduce 3-field result to single trigger for legacy guard code path below.
            // For close trigger, also capture the target slot (Phase 1 cap=1: at most 1 slot).
            var trigger = portfolioResult.OpenTrigger ?? portfolioResult.CloseTrigger;
            var closeTargetSlot = portfolioResult.CloseTargetSlot;
            LogFlowTransitionIfChanged("process-snapshot");

            if (canRenderUi)
            {
                RaiseCurrentPositionTextChanged();
                OnPropertyChanged(nameof(CurrentPhaseText));
            }

            if (trigger is null || !trigger.Triggered)
            {
                return;
            }

            // Guard: kiểm tra điều kiện lọc trước khi vào lệnh
            var holdMs = trigger.Action == GapSignalAction.Open
                ? _runtimeConfigState.CurrentOpenPriceFreezeMs
                : _runtimeConfigState.CurrentClosePriceFreezeMs;
            var guardConfig = new SignalEntryGuard.GuardConfig(
                ConfirmLatencyMs: _runtimeConfigState.CurrentConfirmLatencyMs,
                MaxGap: _runtimeConfigState.CurrentMaxGap,
                MaxSpread: _runtimeConfigState.CurrentMaxSpread,
                PointMultiplier: _runtimeConfigState.CurrentPoint,
                FreezeLastN: _runtimeConfigState.CurrentFreezeLastN);
            var guardResult = SignalEntryGuard.Check(
                trigger, metrics, guardConfig, _priceHistory, holdMs,
                _runtimeConfigState.CurrentCloseHoldConfirmMs);
            if (!guardResult.CanTrade)
            {
                SafeVmLog(
                    "[GUARD][WARN] Auto trade rejected: " +
                    $"trigger={trigger.TriggerType} side={trigger.PrimarySide} action={trigger.Action} " +
                    $"reason=\"{guardResult.SkipReason}\" gap={(trigger.LastBuyGap ?? trigger.LastSellGap)} " +
                    $"confirmLatencyMs={guardConfig.ConfirmLatencyMs} maxGap={guardConfig.MaxGap} maxSpread={guardConfig.MaxSpread}");

                if (trigger.Action == GapSignalAction.Close)
                {
                    _tradingFlowEngine.AbortPendingCloseExecution();
                    LogFlowTransitionIfChanged("close-aborted-by-guard");
                }
                else if (trigger.Action == GapSignalAction.Open)
                {
                    // Open phase is switched to WaitingClose immediately when trigger is produced.
                    // If guard rejects execution, rollback to WaitingOpen to keep UI/state consistent.
                    _tradingFlowEngine.AbortPendingOpenExecution();
                    LogFlowTransitionIfChanged("open-aborted-by-guard");
                }

                RaiseCurrentPositionTextChanged();
                OnPropertyChanged(nameof(CurrentPhaseText));

                return;
            }

            // Keep latest signal summary, but do not append legacy multiline signal format into SignalLogItems.
            LastSignalText = BuildAutoSignalSummary(trigger);

            if (trigger.Action == GapSignalAction.Open
                && !TryAllowAutoOpenByToggle(trigger, out var blockedReason))
            {
                // Open trigger already moved flow to WaitingClose inside engine.
                // Rollback to WaitingOpen when user toggle disables this open side.
                _tradingFlowEngine.AbortPendingOpenExecution();
                LogFlowTransitionIfChanged("open-aborted-by-toggle");
                RaiseCurrentPositionTextChanged();
                OnPropertyChanged(nameof(CurrentPhaseText));

                SignalLogItems.Insert(0,
                    $"    - [{DateTime.Now:HH:mm:ss.fff}] Open blocked by toggle: {blockedReason}");
                return;
            }

            if (trigger.Action == GapSignalAction.Open)
            {
                var requiredN = _runtimeConfigState.CurrentOpenNumberOfQualifyingTimes;
                var canExecute = _tradingFlowEngine.TryConsumeQualifyingForOpen(requiredN);
                if (!canExecute)
                {
                    var current = _tradingFlowEngine.CurrentOpenQualifyingCount;
                    SignalLogItems.Insert(0,
                        $"[{DateTime.Now:HH:mm:ss.fff}] [SKIP OPEN] qualifying {current}/{requiredN} - side={trigger.PrimarySide}");

                    _tradingFlowEngine.AbortPendingOpenExecution();
                    LogFlowTransitionIfChanged("open-aborted-by-qualifying");
                    RaiseCurrentPositionTextChanged();
                    OnPropertyChanged(nameof(CurrentPhaseText));
                    return;
                }

                SignalLogItems.Insert(0,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [EXEC OPEN] qualifying {requiredN}/{requiredN} - side={trigger.PrimarySide}");
            }
            else if (trigger.Action == GapSignalAction.Close)
            {
                var requiredN = _runtimeConfigState.CurrentCloseNumberOfQualifyingTimes;
                var canExecute = _tradingFlowEngine.TryConsumeQualifyingForClose(requiredN);
                if (!canExecute)
                {
                    var current = _tradingFlowEngine.CurrentCloseQualifyingCount;
                    SignalLogItems.Insert(0,
                        $"[{DateTime.Now:HH:mm:ss.fff}] [SKIP CLOSE] qualifying {current}/{requiredN} - mode={_tradingFlowEngine.CurrentOpenMode}");

                    _tradingFlowEngine.AbortPendingCloseExecution();
                    LogFlowTransitionIfChanged("close-aborted-by-qualifying");
                    RaiseCurrentPositionTextChanged();
                    OnPropertyChanged(nameof(CurrentPhaseText));
                    return;
                }

                SignalLogItems.Insert(0,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [EXEC CLOSE] qualifying {requiredN}/{requiredN} reached");
            }

            // Auto-execute trade from signal trigger
            if (trigger.Action == GapSignalAction.Open)
            {
                _ = DispatchOpenTriggerAsync(trigger);
            }
            else if (trigger.Action == GapSignalAction.Close && closeTargetSlot is not null)
            {
                _ = DispatchCloseTriggerAsync(closeTargetSlot, trigger);
            }
        });
    }

    private void OnQualifyingConfigChanged(object? sender, EventArgs e)
    {
        _tradingFlowEngine.ResetQualifyingCounters();
        LogFlowTransitionIfChanged("qualifying-config-changed");
        SignalLogItems.Insert(0,
            $"[{DateTime.Now:HH:mm:ss.fff}] [RESET QUALIFY] config N changed, counters cleared");
    }

    private void LogFlowTransitionIfChanged(string reason)
    {
        var phase = _tradingFlowEngine.CurrentPhase;
        var openMode = _tradingFlowEngine.CurrentOpenMode;
        var side = _tradingFlowEngine.CurrentPositionSide;

        if (phase == _lastLoggedPhase
            && openMode == _lastLoggedOpenMode
            && side == _lastLoggedPositionSide)
        {
            return;
        }

        SafeVmLog(
            $"[FLOW][TRANSITION] reason={reason} " +
            $"phase={_lastLoggedPhase}->{phase} " +
            $"openMode={_lastLoggedOpenMode}->{openMode} " +
            $"side={_lastLoggedPositionSide}->{side}");

        _lastLoggedPhase = phase;
        _lastLoggedOpenMode = openMode;
        _lastLoggedPositionSide = side;
    }

    private bool TryAllowOppositeOpenPriceGuard(int requestedTradeType, string stage, string? pairId)
    {
        var records = _latestTradeLeftResult is { IsMapAvailable: true, IsParseSuccess: true }
            ? _latestTradeLeftResult.Records
            : (IReadOnlyList<TradeSharedRecord>)[];
        var eligibleTickets = _portfolioCoordinator.LiveSlots
            .Concat(_portfolioCoordinator.PendingCloseSlots)
            .Where(x => x.TicketA.HasValue)
            .Select(x => x.TicketA!.Value)
            .ToHashSet();
        var metrics = _runtimeConfigState.CurrentDashboardMetrics;
        var result = eligibleTickets.Count > 0
            && _runtimeConfigState.CurrentOppositeOpenMinDistancePts > 0
            && _latestTradeLeftResult is not { IsMapAvailable: true, IsParseSuccess: true }
                ? OppositeOpenPriceGuardResult.Block(
                    "OPEN_PRICE_A_MISSING",
                    "Không có giá mở hợp lệ trên sàn A để tính giá trung bình")
                : OppositeOpenPriceGuard.Evaluate(
                    records,
                    eligibleTickets,
                    requestedTradeType,
                    metrics?.ExchangeA.Bid,
                    metrics?.ExchangeA.Ask,
                    _runtimeConfigState.CurrentPoint,
                    _runtimeConfigState.CurrentOppositeOpenMinDistancePts);

        var status = result.Skipped ? "SKIP" : result.Allowed ? "PASS" : "BLOCK";
        var direction = result.LastTradeType switch
        {
            0 when requestedTradeType == 1 => "BUY->SELL",
            1 when requestedTradeType == 0 => "SELL->BUY",
            _ => requestedTradeType == 0 ? "NONE->BUY" : "NONE->SELL"
        };
        var signature = $"{direction}|{result.ReasonCode}|{result.LastTradeType}|{result.RequiredPts}";
        var nowUtc = DateTime.UtcNow;
        var shouldLog = (result.Allowed && !result.Skipped)
            || !string.Equals(signature, _lastOppositeOpenGuardBlockSignature, StringComparison.Ordinal)
            || nowUtc - _lastOppositeOpenGuardBlockLogAtUtc >= OppositeOpenGuardBlockLogInterval;

        if (shouldLog)
        {
            if (!result.Allowed || result.Skipped)
            {
                _lastOppositeOpenGuardBlockSignature = signature;
                _lastOppositeOpenGuardBlockLogAtUtc = nowUtc;
            }
            else if (result.Allowed && !result.Skipped)
            {
                _lastOppositeOpenGuardBlockSignature = string.Empty;
            }

            var log = FormatOppositeOpenGuardLog(stage, status, direction, pairId, result);
            SafeVmLog(log);
            AddSignalLog($"[{DateTime.Now:HH:mm:ss.fff}] {log}");
        }

        return result.Allowed;
    }

    private static string FormatOppositeOpenGuardLog(
        string stage,
        string status,
        string direction,
        string? pairId,
        OppositeOpenPriceGuardResult result)
    {
        static string Number(double? value) => value?.ToString("F5", CultureInfo.InvariantCulture) ?? "null";
        return $"[OPPOSITE_OPEN_GUARD][{stage}_{status}] direction={direction} pairId={pairId ?? "-"} " +
               $"avgOpenA={Number(result.AverageOpenPriceA)} currentA={Number(result.CurrentPriceA)} " +
               $"currentPriceType={result.CurrentPriceType ?? "-"} positionCount={result.PositionCount} " +
               $"distancePts={result.DistancePts?.ToString(CultureInfo.InvariantCulture) ?? "null"} " +
               $"requiredPts={result.RequiredPts} result={status} reasonCode={result.ReasonCode} " +
               $"reason=\"{result.ReasonVietnamese}\"";
    }

    private bool TryAllowAutoOpenByToggle(GapSignalTriggerResult trigger, out string blockedReason)
    {
        blockedReason = string.Empty;

        if (trigger.Action != GapSignalAction.Open)
        {
            return true;
        }

        if (trigger.PrimarySide == GapSignalSide.Buy && !IsOpenGapBuyEnabled)
        {
            blockedReason = "GAP_BUY is OFF";
            return false;
        }

        if (trigger.PrimarySide == GapSignalSide.Sell && !IsOpenGapSellEnabled)
        {
            blockedReason = "GAP_SELL is OFF";
            return false;
        }

        return true;
    }

    // Cập nhật profit slot MỖI tick, tách khỏi throttle render UI (canRenderUi).
    // Dùng cache records (giá mở/ticket tĩnh) + metrics hiện tại (bid/ask tươi) →
    // LastProfitSnapshot đồng bộ với gap-snapshot mà ProcessSnapshot dùng ngay dưới.
    // KHÔNG đụng UI; phần vẽ panel + RebuildTradeRealtimeProfitRows vẫn throttle 200ms như cũ.
    private void RefreshSlotProfitsEveryTick(DashboardMetrics metrics, int point)
    {
        UpdateSlotProfitsFromCachedTrades(_latestTradeLeftResult, metrics, isExchangeA: true, point);
        UpdateSlotProfitsFromCachedTrades(_latestTradeRightResult, metrics, isExchangeA: false, point);
    }

    private void UpdateSlotProfitsFromCachedTrades(
        SharedMapReadResult<TradeSharedRecord>? cached,
        DashboardMetrics metrics,
        bool isExchangeA,
        int point)
    {
        if (cached is null || !cached.IsMapAvailable || !cached.IsParseSuccess)
        {
            return;
        }

        foreach (var record in cached.Records)
        {
            if (!IsAppGeneratedTicket(record.Ticket))
            {
                continue;
            }

            _portfolioCoordinator.UpdateProfit(record.Ticket, CalculateTradeProfit(record, metrics, isExchangeA, point));
        }
    }

    private void RefreshTradeRowsFromSnapshot(DashboardMetrics metrics, int point)
    {
        if (_latestTradeLeftResult is not null)
        {
            ApplyTradeResult(
                TradeTab.LeftPanel,
                _latestTradeLeftResult,
                metrics,
                isExchangeA: true,
                point);
        }

        if (_latestTradeRightResult is not null)
        {
            ApplyTradeResult(
                TradeTab.RightPanel,
                _latestTradeRightResult,
                metrics,
                isExchangeA: false,
                point);
        }
    }

    private string ResolveCurrentPositionText(bool isExchangeA)
    {
        if (!IsTradingLogicEnabled)
        {
            return "NONE";
        }

        // Đếm trực tiếp từ MMF physical trades trên từng sàn (TradeType: 0=Buy, 1=Sell)
        // — phản ánh đúng số lệnh đang mở trên sàn, kể cả orphan/external opens.
        var records = isExchangeA
            ? _latestTradeLeftResult?.Records
            : _latestTradeRightResult?.Records;
        var buy = records?.Count(r => r.TradeType == 0) ?? 0;
        var sell = records?.Count(r => r.TradeType == 1) ?? 0;
        var total = buy + sell;
        var maxBuy = _runtimeConfigState.CurrentMaxBuyOpens;
        var maxSell = _runtimeConfigState.CurrentMaxSellOpens;
        var maxTotal = _runtimeConfigState.CurrentMaxTotalOpens;

        var text = $"Buy {buy}/{maxBuy} | Sell {sell}/{maxSell} | Total {total}/{maxTotal}";

        // Quota Rule A vẫn áp dụng global (coordinator-slot based). Phần FULL ở đây
        // là UI hint dựa trên MMF physical count theo sàn, không can thiệp block logic.
        if (total >= maxTotal)
        {
            text += $" — FULL (Total {total}/{maxTotal})";
        }
        else if (buy >= maxBuy)
        {
            text += $" — FULL (Buy {buy}/{maxBuy})";
        }
        else if (sell >= maxSell)
        {
            text += $" — FULL (Sell {sell}/{maxSell})";
        }
        return text;
    }

    private void RaiseCurrentPositionTextChanged()
    {
        OnPropertyChanged(nameof(CurrentPositionTextA));
        OnPropertyChanged(nameof(CurrentPositionTextB));
    }

    private string ResolveCurrentPhaseText()
    {
        // Phase 6: countdown for cooldown + opposite-lock status.
        var now = DateTime.UtcNow;

        // Priority 1: manual/recovery close barrier active.
        if (_portfolioCoordinator.HasNonAutoCloseInFlight)
        {
            return "MANUAL/RECOVERY CLOSE IN PROGRESS";
        }

        // Priority 2: auto cooldown active.
        if (_portfolioCoordinator.GlobalActionLockUntilUtc is { } lockUntil
            && lockUntil > now)
        {
            var remaining = (int)Math.Ceiling((lockUntil - now).TotalSeconds);
            return $"COOLDOWN {remaining}s";
        }

        // Priority 3: post-close auto lock window.
        if (_portfolioCoordinator.LastCloseConfirmedAtUtc is { } lastCloseAt)
        {
            var elapsedSec = (now - lastCloseAt).TotalSeconds;
            if (elapsedSec < _portfolioCoordinator.LastSelectedPostCloseLockSeconds)
            {
                var remaining = _portfolioCoordinator.LastSelectedPostCloseLockSeconds - (int)elapsedSec;
                return $"POST-CLOSE LOCK (no open for {remaining}s)";
            }
        }

        // Priority 4: opposite-side lock window.
        if (_portfolioCoordinator.LastOpenConfirmedAtUtc is { } lastOpenAt
            && _portfolioCoordinator.LastOpenConfirmedSide != TradingPositionSide.None)
        {
            var elapsedSec = (now - lastOpenAt).TotalSeconds;
            if (elapsedSec < _portfolioCoordinator.OppositeSideLockSeconds)
            {
                var remaining = _portfolioCoordinator.OppositeSideLockSeconds - (int)elapsedSec;
                var lockedSide = _portfolioCoordinator.LastOpenConfirmedSide == TradingPositionSide.Buy
                    ? "Sell" : "Buy";
                return $"READY (no {lockedSide} for {remaining}s)";
            }
        }

        // Priority 5: quota full.
        if (_portfolioCoordinator.LiveAndPendingTotalCount >= _runtimeConfigState.CurrentMaxTotalOpens)
        {
            return "QUOTA FULL";
        }

        // Priority 5: legacy engine state (fallback for single-slot semantics).
        var phase = _tradingFlowEngine.CurrentPhase;
        return phase switch
        {
            TradingFlowPhase.WaitingCloseFromGapBuy => "WAITING CLOSE (GAP_SELL)",
            TradingFlowPhase.WaitingCloseFromGapSell => "WAITING CLOSE (GAP_BUY)",
            _ => "READY"
        };
    }

    /// <summary>
    /// Phase 6: hide manual Buy/Sell/Close buttons khi multi-slot enabled.
    /// XAML binds Visibility=Collapsed via BoolToVisibilityConverter.
    /// Hardcoded false; future toggle qua feature flag nếu cần debug.
    /// </summary>
    public bool IsManualTradeButtonsVisible => false;

    private void BindDashboardMetrics(DashboardMetrics metrics)
    {
        ExchangeASymbol = FormatTextOrDash(metrics.ExchangeA.Symbol);
        ExchangeABid = FormatTrimmedNumberOrDash(metrics.ExchangeA.Bid);
        ExchangeAAsk = FormatTrimmedNumberOrDash(metrics.ExchangeA.Ask);
        ExchangeASpread = FormatTrimmedNumberOrDash(metrics.ExchangeA.Spread);
        ExchangeALatencyMs = FormatNumberOrDash(metrics.ExchangeA.LatencyMs, 0);
        ExchangeATps = FormatOneDecimalOrDash(metrics.ExchangeA.Tps);
        ExchangeATime = FormatTextOrDash(metrics.ExchangeA.Time);
        ExchangeAMaxLatMs = FormatNumberOrDash(metrics.ExchangeA.MaxLatMs, 0);
        ExchangeAAvgLatMs = FormatNumberOrDash(metrics.ExchangeA.AvgLatMs, 0);

        ExchangeBSymbol = FormatTextOrDash(metrics.ExchangeB.Symbol);
        ExchangeBBid = FormatTrimmedNumberOrDash(metrics.ExchangeB.Bid);
        ExchangeBAsk = FormatTrimmedNumberOrDash(metrics.ExchangeB.Ask);
        ExchangeBSpread = FormatTrimmedNumberOrDash(metrics.ExchangeB.Spread);
        ExchangeBLatencyMs = FormatNumberOrDash(metrics.ExchangeB.LatencyMs, 0);
        ExchangeBTps = FormatOneDecimalOrDash(metrics.ExchangeB.Tps);
        ExchangeBTime = FormatTextOrDash(metrics.ExchangeB.Time);
        ExchangeBMaxLatMs = FormatNumberOrDash(metrics.ExchangeB.MaxLatMs, 0);
        ExchangeBAvgLatMs = FormatNumberOrDash(metrics.ExchangeB.AvgLatMs, 0);

        GapBuy = FormatIntegerOrDash(metrics.GapBuy);
        GapSell = FormatIntegerOrDash(metrics.GapSell);
    }

    private string BuildAutoSignalSummary(GapSignalTriggerResult trigger)
    {
        var now = trigger.TriggeredAtUtc.ToLocalTime();
        var symbolA = _runtimeConfigState.CurrentDashboardMetrics?.ExchangeA.Symbol ?? "-";
        var isGapBuyFamily = trigger.TriggerType is GapSignalTriggerType.OpenByGapBuy or GapSignalTriggerType.CloseByGapBuy;
        var gapLabel = isGapBuyFamily ? "Gap BUY" : "Gap SELL";
        var lastGap = isGapBuyFamily ? trigger.LastBuyGap : trigger.LastSellGap;
        var allGaps = isGapBuyFamily ? trigger.BuyGaps : trigger.SellGaps;

        if (trigger.Action == GapSignalAction.Open)
        {
            var slot = _autoSlot;
            var isBuy = trigger.PrimarySide == GapSignalSide.Buy;
            var type = isBuy ? "BUY" : "SELL";
            var price = isBuy ? trigger.LastAAsk : trigger.LastABid;
            var spreadText = BuildSpreadPtsText(_runtimeConfigState.CurrentDashboardMetrics);
            return SignalLogFormatter.FormatAutoOpen(
                now,
                slot,
                "A",
                type,
                symbolA,
                price,
                gapLabel,
                lastGap,
                allGaps,
                spreadText);
        }

        var closeSlot = Math.Max(0, _autoSlot - 1);
        var isBuyPosition = trigger.TriggerType == GapSignalTriggerType.CloseByGapSell;
        var closeType = isBuyPosition ? "BUY" : "SELL";
        var closePrice = SignalLogFormatter.ResolveClosePrice(trigger.LastABid, trigger.LastAAsk, isBuyPosition);
        return SignalLogFormatter.FormatAutoClose(
            now,
            closeSlot,
            "A",
            closeType,
            symbolA,
            closePrice,
            gapLabel,
            lastGap,
            allGaps,
            trigger.CloseReason,
            trigger.CloseTpProfit,
            trigger.CloseTpTarget,
            trigger.CloseTpProfits);
    }

    private static string FormatNumberOrDash(decimal? value, int decimalPlaces = 5)
        => value.HasValue
            ? value.Value.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture)
            : "-";

    private string BuildSpreadPtsText(DashboardMetrics? metrics)
    {
        if (metrics is null)
        {
            return "SpreadA=- pt | SpreadB=- pt";
        }

        var point = Math.Max(1, _runtimeConfigState.CurrentPoint);
        var spreadAPts = metrics.ExchangeA.Spread.HasValue
            ? ((int)(metrics.ExchangeA.Spread.Value * point)).ToString(CultureInfo.InvariantCulture)
            : "-";
        var spreadBPts = metrics.ExchangeB.Spread.HasValue
            ? ((int)(metrics.ExchangeB.Spread.Value * point)).ToString(CultureInfo.InvariantCulture)
            : "-";

        return $"SpreadA={spreadAPts} pt | SpreadB={spreadBPts} pt";
    }

    private static string FormatTrimmedNumberOrDash(decimal? value, int maxDecimalPlaces = 5)
        => value.HasValue
            ? value.Value.ToString($"0.{new string('#', maxDecimalPlaces)}", CultureInfo.InvariantCulture)
            : "-";

    private static string FormatTrimmedNumberOrDash(float? value, int maxDecimalPlaces = 5)
        => value.HasValue
            ? value.Value.ToString($"0.{new string('#', maxDecimalPlaces)}", CultureInfo.InvariantCulture)
            : "-";

    private static string FormatOneDecimalOrDash(float? value)
        => value.HasValue
            ? value.Value.ToString("F1", CultureInfo.InvariantCulture)
            : "-";

    private static string FormatIntegerOrDash(int? value)
        => value.HasValue
            ? value.Value.ToString(CultureInfo.InvariantCulture)
            : "-";

    private static string FormatTextOrDash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private async Task<bool> TryRecoverSlotsFromConfigAsync(string currentSlotsJson)
    {
        try
        {
            var persistedSlots = SlotPersistence.Deserialize(currentSlotsJson);
            if (persistedSlots.Count == 0)
            {
                return false;
            }

            var tradeMapNameA = TradeTab.LeftPanel.TargetMapName;
            var tradeMapNameB = TradeTab.RightPanel.TargetMapName;
            if (string.IsNullOrWhiteSpace(tradeMapNameA) || string.IsNullOrWhiteSpace(tradeMapNameB))
            {
                SafeVmLog("[RECOVERY][WARN] Trade map names not available yet, cannot recover persisted slots.");
                return false;
            }

            var tradeResultA = _tradesSharedMemoryReader.ReadTrades(tradeMapNameA);
            var tradeResultB = _tradesSharedMemoryReader.ReadTrades(tradeMapNameB);
            if (!AreTradeMapsHealthy(tradeResultA, tradeResultB))
            {
                SafeVmLog("[RECOVERY][WARN] Trade maps are not healthy, cannot recover persisted slots.");
                return false;
            }

            var liveSlots = persistedSlots
                .Where(slot => tradeResultA.Records.Any(r => r.Ticket == slot.TicketA)
                               && tradeResultB.Records.Any(r => r.Ticket == slot.TicketB))
                .ToList();

            if (liveSlots.Count == 0)
            {
                SafeVmLog("[RECOVERY][INFO] Persisted slots not found in shared memory. Clearing DB slot snapshot.");
                await _configService.SaveCurrentSlotsAsync("[]");
                return false;
            }

            _portfolioCoordinator.RecoverSlotsFromPersisted(liveSlots);
            RegisterRecoveredSlotMappings(liveSlots, tradeResultA.Records, tradeResultB.Records, tradeMapNameA, tradeMapNameB);
            _autoSlot = Math.Max(_autoSlot, liveSlots.Max(s => s.SlotId) + 1);
            SetActiveAutoCycleFromRecoveredSlots(liveSlots);

            SafeVmLog($"[RECOVERY][INFO] Recovered {liveSlots.Count}/{persistedSlots.Count} slots from current_slots.");
            AddSignalLog($"    - [{DateTime.Now:HH:mm:ss.fff}] Recovered {liveSlots.Count} open slot(s) from DB");

            if (liveSlots.Count != persistedSlots.Count)
            {
                await PersistCurrentSlotsSnapshotAsync("drop-stale-persisted-slots");
            }

            return true;
        }
        catch (Exception ex)
        {
            SafeVmLog($"[RECOVERY][ERROR] Failed to recover persisted slots: {ex.Message}");
            return false;
        }
    }

    private async Task TryRecoverTicketsFromConfigAsync(string currentTickA, string currentTickB)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(currentTickA) || string.IsNullOrWhiteSpace(currentTickB))
            {
                return;
            }

            if (!ulong.TryParse(currentTickA, out var ticketA) || ticketA == 0
                || !ulong.TryParse(currentTickB, out var ticketB) || ticketB == 0)
            {
                return;
            }

            SafeVmLog($"[RECOVERY][INFO] Found persisted tickets in DB: ticketA={ticketA} ticketB={ticketB}. Checking shared memory...");

            var tradeMapNameA = TradeTab.LeftPanel.TargetMapName;
            var tradeMapNameB = TradeTab.RightPanel.TargetMapName;

            if (string.IsNullOrWhiteSpace(tradeMapNameA) || string.IsNullOrWhiteSpace(tradeMapNameB))
            {
                SafeVmLog("[RECOVERY][WARN] Trade map names not available yet, cannot recover tickets.");
                await _configService.SaveCurrentTicksAsync("", "");
                return;
            }

            var tradeResultA = _tradesSharedMemoryReader.ReadTrades(tradeMapNameA);
            var tradeResultB = _tradesSharedMemoryReader.ReadTrades(tradeMapNameB);

            var foundA = tradeResultA.IsMapAvailable && tradeResultA.IsParseSuccess
                         && tradeResultA.Records.Any(r => r.Ticket == ticketA);
            var foundB = tradeResultB.IsMapAvailable && tradeResultB.IsParseSuccess
                         && tradeResultB.Records.Any(r => r.Ticket == ticketB);

            if (!foundA || !foundB)
            {
                SafeVmLog($"[RECOVERY][INFO] Previous tickets not found in shared memory (foundA={foundA} foundB={foundB}). Clearing DB.");
                await _configService.SaveCurrentTicksAsync("", "");
                return;
            }

            // Both tickets exist in shared memory — register them into tool tracking
            var recoveryPairId = $"RECOVERY-0-{Environment.TickCount64}";

            _pairIdByTicket[ticketA] = recoveryPairId;
            _pairIdByTicket[ticketB] = recoveryPairId;

            var keyA = NormalizeMapName(tradeMapNameA);
            var keyB = NormalizeMapName(tradeMapNameB);

            if (!_knownTradeTicketsByMap.ContainsKey(keyA))
                _knownTradeTicketsByMap[keyA] = new HashSet<ulong>();
            _knownTradeTicketsByMap[keyA].Add(ticketA);

            if (!_knownTradeTicketsByMap.ContainsKey(keyB))
                _knownTradeTicketsByMap[keyB] = new HashSet<ulong>();
            _knownTradeTicketsByMap[keyB].Add(ticketB);

            _activeAutoCycle = new ActiveAutoCycleState
            {
                Slot = 0,
                OpenedAtLocal = DateTimeOffset.Now,
                PairIdA = recoveryPairId,
                PairIdB = recoveryPairId,
                TicketA = ticketA,
                TicketB = ticketB
            };

            // Register slot vào coordinator để toolRowsX == coordinatorActiveCount.
            // Nếu không có, invariant watchdog sẽ thấy `toolRowsX (1) > coordinatorActiveCount (0)`
            // → pause auto-open vĩnh viễn sau recovery (CLAUDE.md §5).
            if (_portfolioCoordinator.GetSlotByTicket(ticketA) is null
                && _portfolioCoordinator.GetSlotByTicket(ticketB) is null)
            {
                var recordA = tradeResultA.Records.First(r => r.Ticket == ticketA);
                var side = recordA.TradeType == 0
                    ? TradingPositionSide.Buy
                    : TradingPositionSide.Sell;
                var openMode = side == TradingPositionSide.Buy
                    ? TradingOpenMode.GapBuy
                    : TradingOpenMode.GapSell;

                _portfolioCoordinator.RegisterSyncedSlot(
                    pairId: recoveryPairId,
                    side: side,
                    openMode: openMode,
                    ticketA: ticketA,
                    ticketB: ticketB,
                    openConfirmedAtUtc: DateTime.UtcNow,
                    holdingSeconds: Math.Max(0, _tradingFlowEngine.CurrentHoldingSeconds));

                SafeVmLog($"[RECOVERY][INFO] Registered coordinator slot: pairId={recoveryPairId} side={side} mode={openMode} ticketA={ticketA} ticketB={ticketB}");
            }

            SafeVmLog($"[RECOVERY][INFO] Recovered tickets from previous session: ticketA={ticketA} ticketB={ticketB} pairId={recoveryPairId}");
            await PersistCurrentSlotsSnapshotAsync("legacy-ticket-recovery");
        }
        catch (Exception ex)
        {
            SafeVmLog($"[RECOVERY][ERROR] Failed to recover tickets: {ex.Message}");
        }
    }

    private static bool AreTradeMapsHealthy(
        SharedMapReadResult<TradeSharedRecord> tradeResultA,
        SharedMapReadResult<TradeSharedRecord> tradeResultB)
        => tradeResultA.IsMapAvailable
           && tradeResultA.IsParseSuccess
           && tradeResultB.IsMapAvailable
           && tradeResultB.IsParseSuccess;

    private List<ResyncedOpenSlot> BuildResyncedOpenSlots(
        IReadOnlyList<TradeSharedRecord> recordsA,
        IReadOnlyList<TradeSharedRecord> recordsB)
    {
        var tracked = BuildTrackedResyncedOpenSlots(recordsA, recordsB);
        return tracked.Count > 0 ? tracked : BuildInferredResyncedOpenSlots(recordsA, recordsB);
    }

    /// <summary>
    /// Lõi đồng bộ của resync: rebuild coordinator slots từ MMF tool-pair đang mở (integrity/recovery —
    /// KHÔNG tạo open/close dispatch, Rule E safe). Trả số slot đã rebuild; 0 nếu không có pair pairable.
    /// Caller tự lo persist + log ngữ cảnh, và phải verify maps healthy trước khi gọi.
    /// Dùng chung bởi <see cref="ResyncOpenTradesAsync"/> (Start) và self-heal watchdog.
    /// </summary>
    private int TryRebuildCoordinatorFromMmf(
        IReadOnlyList<TradeSharedRecord> recordsA,
        IReadOnlyList<TradeSharedRecord> recordsB,
        string tradeMapNameA,
        string tradeMapNameB)
    {
        var resyncedSlots = BuildResyncedOpenSlots(recordsA, recordsB);
        if (resyncedSlots.Count == 0)
        {
            return 0;
        }

        ApplyResyncedOpenSlots(resyncedSlots, tradeMapNameA, tradeMapNameB);
        return resyncedSlots.Count;
    }

    private List<ResyncedOpenSlot> BuildTrackedResyncedOpenSlots(
        IReadOnlyList<TradeSharedRecord> recordsA,
        IReadOnlyList<TradeSharedRecord> recordsB)
    {
        var byPairA = recordsA
            .Where(r => _pairIdByTicket.ContainsKey(r.Ticket))
            .GroupBy(r => _pairIdByTicket[r.Ticket], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.TimeMsc).First(), StringComparer.Ordinal);
        var byPairB = recordsB
            .Where(r => _pairIdByTicket.ContainsKey(r.Ticket))
            .GroupBy(r => _pairIdByTicket[r.Ticket], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.TimeMsc).First(), StringComparer.Ordinal);

        var slots = new List<ResyncedOpenSlot>();
        foreach (var pairId in byPairA.Keys.Intersect(byPairB.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            var recordA = byPairA[pairId];
            var recordB = byPairB[pairId];
            if (!TryResolveOpenSide(recordA, recordB, out var side, out var mode))
            {
                continue;
            }

            slots.Add(new ResyncedOpenSlot(slots.Count + 1, pairId, side, mode, recordA, recordB));
        }

        return slots;
    }

    private List<ResyncedOpenSlot> BuildInferredResyncedOpenSlots(
        IReadOnlyList<TradeSharedRecord> recordsA,
        IReadOnlyList<TradeSharedRecord> recordsB)
    {
        var slots = new List<ResyncedOpenSlot>();
        PairInferredTrades(recordsA, recordsB, slots, tradeTypeA: 0, tradeTypeB: 1, TradingPositionSide.Buy, TradingOpenMode.GapBuy);
        PairInferredTrades(recordsA, recordsB, slots, tradeTypeA: 1, tradeTypeB: 0, TradingPositionSide.Sell, TradingOpenMode.GapSell);
        return slots
            .OrderBy(s => Math.Min(s.RecordA.TimeMsc, s.RecordB.TimeMsc))
            .Select((slot, index) => slot with { SlotId = index + 1 })
            .ToList();
    }

    private static void PairInferredTrades(
        IReadOnlyList<TradeSharedRecord> recordsA,
        IReadOnlyList<TradeSharedRecord> recordsB,
        List<ResyncedOpenSlot> slots,
        int tradeTypeA,
        int tradeTypeB,
        TradingPositionSide side,
        TradingOpenMode mode)
    {
        var left = recordsA.Where(r => r.TradeType == tradeTypeA).OrderBy(r => r.TimeMsc).ToList();
        var right = recordsB.Where(r => r.TradeType == tradeTypeB).OrderBy(r => r.TimeMsc).ToList();
        var count = Math.Min(left.Count, right.Count);

        for (var i = 0; i < count; i++)
        {
            var pairId = $"RESYNC-{slots.Count + 1:D4}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            slots.Add(new ResyncedOpenSlot(slots.Count + 1, pairId, side, mode, left[i], right[i]));
        }
    }

    private static bool TryResolveOpenSide(
        TradeSharedRecord recordA,
        TradeSharedRecord recordB,
        out TradingPositionSide side,
        out TradingOpenMode mode)
    {
        if (recordA.TradeType == 0 && recordB.TradeType == 1)
        {
            side = TradingPositionSide.Buy;
            mode = TradingOpenMode.GapBuy;
            return true;
        }

        if (recordA.TradeType == 1 && recordB.TradeType == 0)
        {
            side = TradingPositionSide.Sell;
            mode = TradingOpenMode.GapSell;
            return true;
        }

        side = TradingPositionSide.None;
        mode = TradingOpenMode.None;
        return false;
    }

    private void ApplyResyncedOpenSlots(
        IReadOnlyList<ResyncedOpenSlot> slots,
        string tradeMapNameA,
        string tradeMapNameB)
    {
        var recovered = slots.Select(slot => new RecoveredSlotData(
            SlotId: slot.SlotId,
            PairId: slot.PairId,
            Side: slot.Side,
            OpenMode: slot.OpenMode,
            TicketA: slot.RecordA.Ticket,
            TicketB: slot.RecordB.Ticket,
            OpenConfirmedAtUtc: DateTime.UtcNow,
            HoldingSeconds: Math.Max(0, _tradingFlowEngine.CurrentHoldingSeconds))).ToList();

        _portfolioCoordinator.RecoverSlotsFromPersisted(recovered);
        RegisterRecoveredSlotMappings(recovered, slots.Select(s => s.RecordA).ToList(), slots.Select(s => s.RecordB).ToList(), tradeMapNameA, tradeMapNameB);
        SetActiveAutoCycleFromRecoveredSlots(recovered);
        _autoSlot = Math.Max(_autoSlot, recovered.Max(s => s.SlotId) + 1);
        _isAutoOpenPausedByInvariant = false;
        _invariantClearStreak = 0;
        RaiseCurrentPositionTextChanged();
        OnPropertyChanged(nameof(CurrentPhaseText));
    }

    private void RegisterRecoveredSlotMappings(
        IReadOnlyList<RecoveredSlotData> slots,
        IReadOnlyList<TradeSharedRecord> recordsA,
        IReadOnlyList<TradeSharedRecord> recordsB,
        string tradeMapNameA,
        string tradeMapNameB)
    {
        var keyA = NormalizeMapName(tradeMapNameA);
        var keyB = NormalizeMapName(tradeMapNameB);
        if (!_knownTradeTicketsByMap.ContainsKey(keyA))
        {
            _knownTradeTicketsByMap[keyA] = [];
        }

        if (!_knownTradeTicketsByMap.ContainsKey(keyB))
        {
            _knownTradeTicketsByMap[keyB] = [];
        }

        foreach (var slot in slots)
        {
            _pairIdByTicket[slot.TicketA] = slot.PairId;
            _pairIdByTicket[slot.TicketB] = slot.PairId;
            _knownTradeTicketsByMap[keyA].Add(slot.TicketA);
            _knownTradeTicketsByMap[keyB].Add(slot.TicketB);

            var recordA = recordsA.FirstOrDefault(r => r.Ticket == slot.TicketA);
            var recordB = recordsB.FirstOrDefault(r => r.Ticket == slot.TicketB);
            if (recordA is not null)
            {
                _profitSnapshotByTicket.TryAdd(recordA.Ticket, recordA.Profit);
            }

            if (recordB is not null)
            {
                _profitSnapshotByTicket.TryAdd(recordB.Ticket, recordB.Profit);
            }
        }
    }

    private void SetActiveAutoCycleFromRecoveredSlots(IReadOnlyList<RecoveredSlotData> slots)
    {
        var latest = slots
            .OrderByDescending(s => s.OpenConfirmedAtUtc)
            .FirstOrDefault();
        if (latest is null)
        {
            return;
        }

        _activeAutoCycle = new ActiveAutoCycleState
        {
            Slot = latest.SlotId,
            OpenedAtLocal = DateTimeOffset.Now,
            PairIdA = latest.PairId,
            PairIdB = latest.PairId,
            TicketA = latest.TicketA,
            TicketB = latest.TicketB
        };
    }

    private async Task PersistCurrentSlotsSnapshotAsync(string reason)
    {
        var json = SlotPersistence.Serialize(_portfolioCoordinator.LiveSlots);
        try
        {
            await _configService.SaveCurrentSlotsAsync(json);
            SafeVmLog($"[PERSIST][INFO] Saved current_slots ({reason}): {json}");
        }
        catch (Exception ex)
        {
            SafeVmLog($"[PERSIST][WARN] Failed to save current_slots ({reason}): {ex.Message}");
        }
    }

    private void AddSignalLog(string message)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() => SignalLogItems.Insert(0, message));
    }

    private void SafeVmLog(string message)
    {
        try
        {
            _tradeSessionFileLogger.Log(message);
        }
        catch
        {
            // ignored by design
        }
    }

    private static TradeExecutionContext CreateExecutionContext(
        TradeExecutionReason reason,
        string source,
        string? pairId = null,
        int? slotId = null)
        => new(
            RequestId: Guid.NewGuid(),
            Reason: reason,
            Source: source,
            PairId: pairId,
            SlotId: slotId);

    private static TradeExecutionContext CreateStrategicContext(
        TradeExecutionReason reason,
        string source,
        GapSignalTriggerResult trigger,
        string? pairId,
        int? slotId)
    {
        var createdAtUtc = trigger.TriggeredAtUtc.Kind switch
        {
            DateTimeKind.Utc => trigger.TriggeredAtUtc,
            DateTimeKind.Local => trigger.TriggeredAtUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(trigger.TriggeredAtUtc, DateTimeKind.Utc)
        };
        var fingerprint = string.Join(
            "|",
            trigger.TriggerType,
            trigger.LastBuyGap?.ToString(CultureInfo.InvariantCulture) ?? "-",
            trigger.LastSellGap?.ToString(CultureInfo.InvariantCulture) ?? "-",
            trigger.PointMultiplier.ToString(CultureInfo.InvariantCulture),
            createdAtUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        var signal = new SignalAuthorization(
            SignalId: Guid.NewGuid(),
            Action: trigger.Action,
            TriggerType: trigger.TriggerType,
            Side: trigger.PrimarySide,
            CreatedAtUtc: createdAtUtc,
            ValidUntilUtc: createdAtUtc.AddMilliseconds(1000),
            SnapshotFingerprint: fingerprint,
            PairId: pairId,
            SlotId: slotId,
            CloseReason: trigger.CloseReason);
        return new TradeExecutionContext(
            RequestId: Guid.NewGuid(),
            Reason: reason,
            Source: source,
            PairId: pairId,
            SlotId: slotId,
            Signal: signal);
    }

    private static TradeExecutionContext CreateRecoveryContext(
        TradeExecutionReason reason,
        string source,
        string pairId,
        int? slotId,
        ulong ticket,
        string evidence)
        => new(
            RequestId: Guid.NewGuid(),
            Reason: reason,
            Source: source,
            PairId: pairId,
            SlotId: slotId,
            Recovery: new TradeRecoveryEvidence(
                PairId: pairId,
                SlotId: slotId,
                Ticket: ticket,
                Evidence: evidence));

    private void NotifyOpenCloseFailures(string operation, ManualTradeResult result, string? pairId)
    {
        try
        {
            var failedLegs = result.Legs.Where(x => !x.Success).ToList();
            if (failedLegs.Count == 0)
            {
                return;
            }

            var eventCode = operation.Equals("OPEN", StringComparison.OrdinalIgnoreCase)
                ? (failedLegs.Count >= 2 ? "OPEN_FAILED_BOTH" : "OPEN_FAILED_ONE_LEG")
                : (failedLegs.Count >= 2 ? "CLOSE_FAILED_BOTH" : "CLOSE_FAILED_ONE_LEG");

            var severity = operation.Equals("CLOSE", StringComparison.OrdinalIgnoreCase) && failedLegs.Count == 1
                ? "CRITICAL"
                : "ERROR";

            var detail = operation.Equals("OPEN", StringComparison.OrdinalIgnoreCase)
                ? "Không mở được lệnh"
                : "Không đóng được lệnh";

            var meta = new Dictionary<string, string?>
            {
                ["operation"] = operation,
                ["failedLegs"] = failedLegs.Count.ToString(CultureInfo.InvariantCulture),
                ["failedDetail"] = string.Join(" | ", failedLegs.Select(x => $"{x.Exchange}:{x.Detail}"))
            };

            NotifyTelegram(eventCode, severity, detail, pairId, meta);
        }
        catch
        {
            // never break trading flow
        }
    }

    private void NotifySlippageAndDelayIfNeeded(
        bool isOpen,
        string exchange,
        ulong ticket,
        string? pairId,
        double? slippagePt,
        long? executionMs)
    {
        try
        {
            if (slippagePt.HasValue && Math.Abs(slippagePt.Value) > AlertSlippageThresholdPt)
            {
                NotifyTelegram(
                    isOpen ? "SLIPPAGE_OPEN_GT_40PT" : "SLIPPAGE_CLOSE_GT_40PT",
                    "WARN",
                    isOpen
                        ? "Trượt giá mở lệnh vượt ngưỡng 40 point"
                        : "Trượt giá đóng lệnh vượt ngưỡng 40 point",
                    pairId,
                    new Dictionary<string, string?>
                    {
                        ["exchange"] = exchange,
                        ["ticket"] = ticket.ToString(CultureInfo.InvariantCulture),
                        ["slippagePt"] = slippagePt.Value.ToString("0.##", CultureInfo.InvariantCulture)
                    });
            }

            if (executionMs.HasValue && executionMs.Value > AlertExecutionThresholdMs)
            {
                NotifyTelegram(
                    isOpen ? "DELAY_OPEN_GT_1000MS" : "DELAY_CLOSE_GT_1000MS",
                    "WARN",
                    isOpen
                        ? "Delay open vượt ngưỡng 1000ms"
                        : "Delay close vượt ngưỡng 1000ms",
                    pairId,
                    new Dictionary<string, string?>
                    {
                        ["exchange"] = exchange,
                        ["ticket"] = ticket.ToString(CultureInfo.InvariantCulture),
                        ["executionMs"] = executionMs.Value.ToString(CultureInfo.InvariantCulture)
                    });
            }
        }
        catch
        {
            // never break trading flow
        }
    }

    private void NotifyTelegram(
        string eventCode,
        string severity,
        string detail,
        string? pairId = null,
        IReadOnlyDictionary<string, string?>? meta = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _telegramNotifier.NotifyAsync(
                    eventCode: eventCode,
                    severity: severity,
                    detail: detail,
                    pairId: pairId,
                    meta: meta);
            }
            catch
            {
                // swallow by design
            }
        });
    }

    private static string GetAssemblyVersion()
    {
        var assembly = typeof(DashboardViewModel).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
