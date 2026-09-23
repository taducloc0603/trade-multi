using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using TradeDesktop.App.Commands;
using TradeDesktop.App.Helpers;
using TradeDesktop.App.Services;
using TradeDesktop.App.State;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.CTrader;
using System.Globalization;

namespace TradeDesktop.App.ViewModels;

public sealed class ConfigViewModel : ObservableObject
{
    private readonly RuntimeConfigState _runtimeConfigState;
    private readonly IConfigService _configService;
    private readonly ITradeSessionFileLogger _tradeSessionFileLogger;
    private readonly IHwndHealthChecker _hwndHealthChecker;
    private readonly IPortfolioCoordinator _portfolioCoordinator;
    private readonly ICTraderQuoteSession? _ctraderQuoteSession;
    private string _ctraderSessionStatus = "Chưa kiểm tra";
    private string _machineHostName = string.Empty;

    // Form cTrader (mode cTrader của khối Sàn B). Giữ dạng chuỗi để TextBox bind hai chiều; parse khi Save.
    private string _ctraderQuoteHost = string.Empty;
    private string _ctraderQuotePortSsl = string.Empty;
    private string _ctraderQuotePortPlain = string.Empty;
    private string _ctraderTradeHost = string.Empty;
    private string _ctraderTradePortSsl = string.Empty;
    private string _ctraderTradePortPlain = string.Empty;
    private bool _ctraderUseSsl = true;
    private string _ctraderSenderCompId = string.Empty;
    private string _ctraderTargetCompId = CTraderFixConfig.DefaultTargetCompId;
    private string _ctraderUsernameOverride = string.Empty;
    private string _ctraderSymbolId = string.Empty;
    private string _ctraderSymbolName = string.Empty;
    private string _ctraderVolumeBUnits = string.Empty;
    private string _ctraderContractSizeB = string.Empty;
    private string _ctraderVolumeALots = string.Empty;
    // Mật khẩu KHÔNG bind hai chiều: _storedCTraderPassword lấy từ config đã lưu, _pendingCTraderPassword
    // do PasswordBox đẩy vào. Ô trống khi Save = giữ mật khẩu cũ.
    private string _storedCTraderPassword = string.Empty;
    private string _pendingCTraderPassword = string.Empty;

    private string _mapName1 = string.Empty;
    private string _mapName2 = string.Empty;
    private string _tradeHwndA = string.Empty;
    private string _tradeHwndB = string.Empty;
    private string _platformA = "mt5";
    private string _platformB = "mt5";

    private string _loadStatus = "Đang tải theo host name máy...";
    private string _map1CheckStatus = "Chưa kiểm tra";
    private string _map2CheckStatus = "Chưa kiểm tra";
    private string _errorMessage = string.Empty;

    private bool _isMap1Valid;
    private bool _isMap2Valid;
    private bool _isExistingRecordLoaded;
    private bool _areMapNamesEnabled;
    private bool _canSave;

    public ConfigViewModel(
        RuntimeConfigState runtimeConfigState,
        IConfigService configService,
        ITradeSessionFileLogger tradeSessionFileLogger,
        IHwndHealthChecker hwndHealthChecker,
        IPortfolioCoordinator portfolioCoordinator,
        ICTraderQuoteSession? ctraderQuoteSession = null)
    {
        _runtimeConfigState = runtimeConfigState;
        _configService = configService;
        _tradeSessionFileLogger = tradeSessionFileLogger;
        _hwndHealthChecker = hwndHealthChecker;
        _portfolioCoordinator = portfolioCoordinator;
        _ctraderQuoteSession = ctraderQuoteSession;

        CheckMap1Command = new AsyncRelayCommand(CheckMap1Async, CanCheckMap1);
        CheckMap2Command = new AsyncRelayCommand(CheckMap2Async, CanCheckMap2);
        CheckCTraderSessionCommand = new AsyncRelayCommand(CheckCTraderSessionAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSaveCommand);
        CancelCommand = new AsyncRelayCommand(CancelAsync);
        AddHwndColumnCommand = new AsyncRelayCommand(AddHwndColumnAsync);
        DeleteHwndColumnCommand = new AsyncRelayCommand(DeleteHwndColumnAsync, CanDeleteHwndColumn);

        MachineHostName = runtimeConfigState.CurrentMachineHostName;
        MapName1 = runtimeConfigState.CurrentMapName1;
        // StoredMapName2, KHÔNG phải CurrentMapName2: khi platform_b = ctrader giá trị hiệu lực là CTRADER_B,
        // Save ghi nó xuống DB sẽ đè mất map MT của sàn B.
        MapName2 = runtimeConfigState.StoredMapName2;
        PlatformA = runtimeConfigState.CurrentPlatformA;
        PlatformB = runtimeConfigState.CurrentPlatformB;
        ApplyCTraderFixToForm(runtimeConfigState.CurrentCTraderFixConfig);
        CTraderSessionStatus = _ctraderQuoteSession?.StatusText ?? "Không có FIX session";

        var hasRuntimeState =
            !string.IsNullOrWhiteSpace(MachineHostName) ||
            !string.IsNullOrWhiteSpace(MapName1) ||
            !string.IsNullOrWhiteSpace(MapName2);

        IsExistingRecordLoaded = hasRuntimeState;
        AreMapNamesEnabled = hasRuntimeState;
        LoadStatus = hasRuntimeState
            ? "✔ Đã nạp dữ liệu runtime"
            : "Đang tải theo host name máy...";

        ManualHwndColumns.CollectionChanged += OnManualHwndColumnsChanged;
        InitializeColumns(runtimeConfigState.CurrentManualHwndColumns);

        RefreshDerivedState();
        _ = LoadByMachineHostNameAsync();
    }

    public event Action<bool?>? RequestClose;

    // Dùng cho CHART HWND nhiều cột.
    public ObservableCollection<ManualHwndColumnItemViewModel> ManualHwndColumns { get; } = [];

    public string MachineHostName
    {
        get => _machineHostName;
        private set => SetProperty(ref _machineHostName, value);
    }

    public string MapName1
    {
        get => _mapName1;
        set
        {
            if (!SetProperty(ref _mapName1, value))
            {
                return;
            }

            IsMapName1Valid = false;
            Map1CheckStatus = "Chưa kiểm tra";
            RefreshDerivedState();
            RefreshButtons();
        }
    }

    public string MapName2
    {
        get => _mapName2;
        set
        {
            if (!SetProperty(ref _mapName2, value))
            {
                return;
            }

            IsMapName2Valid = false;
            Map2CheckStatus = "Chưa kiểm tra";
            RefreshDerivedState();
            RefreshButtons();
        }
    }

    // Backward-compatible properties trỏ về cột CHART đầu tiên.
    public string ChartHwndA
    {
        get => ManualHwndColumns.Count > 0 ? ManualHwndColumns[0].ChartHwndA : string.Empty;
        set
        {
            EnsureAtLeastOneColumn();
            ManualHwndColumns[0].ChartHwndA = value;
        }
    }

    public string ChartHwndB
    {
        get => ManualHwndColumns.Count > 0 ? ManualHwndColumns[0].ChartHwndB : string.Empty;
        set
        {
            EnsureAtLeastOneColumn();
            ManualHwndColumns[0].ChartHwndB = value;
        }
    }

    // TRADE HWND luôn chỉ có 1 mỗi sàn.
    public string TradeHwndA
    {
        get => _tradeHwndA;
        set => SetProperty(ref _tradeHwndA, value);
    }

    public string TradeHwndB
    {
        get => _tradeHwndB;
        set => SetProperty(ref _tradeHwndB, value);
    }

    public string LoadStatus
    {
        get => _loadStatus;
        private set => SetProperty(ref _loadStatus, value);
    }

    public string Map1CheckStatus
    {
        get => _map1CheckStatus;
        private set => SetProperty(ref _map1CheckStatus, value);
    }

    public string Map2CheckStatus
    {
        get => _map2CheckStatus;
        private set => SetProperty(ref _map2CheckStatus, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (!SetProperty(ref _errorMessage, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string PlatformA
    {
        get => _platformA;
        set
        {
            var normalized = NormalizePlatform(value);
            if (!SetProperty(ref _platformA, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(IsPlatformAMt4));
            OnPropertyChanged(nameof(IsPlatformAMt5));
        }
    }

    public string PlatformB
    {
        get => _platformB;
        set
        {
            var normalized = NormalizePlatform(value);
            if (!SetProperty(ref _platformB, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(IsPlatformBMt4));
            OnPropertyChanged(nameof(IsPlatformBMt5));
            OnPropertyChanged(nameof(IsPlatformBCTrader));
            OnPropertyChanged(nameof(IsPlatformBMtMode));
            RefreshButtons();
        }
    }

    public bool IsPlatformAMt4
    {
        get => string.Equals(PlatformA, "mt4", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (!value)
            {
                return;
            }

            PlatformA = "mt4";
        }
    }

    public bool IsPlatformAMt5
    {
        get => string.Equals(PlatformA, "mt5", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (!value)
            {
                return;
            }

            PlatformA = "mt5";
        }
    }

    public bool IsPlatformBMt4
    {
        get => string.Equals(PlatformB, "mt4", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (!value)
            {
                return;
            }

            PlatformB = "mt4";
        }
    }

    public bool IsPlatformBMt5
    {
        get => string.Equals(PlatformB, "mt5", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (!value)
            {
                return;
            }

            PlatformB = "mt5";
        }
    }

    // UI 2 mode của khối Sàn B: suy ra từ PlatformB, không lưu cờ riêng.
    public bool IsPlatformBCTrader
    {
        get => string.Equals(PlatformB, "ctrader", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (!value)
            {
                return;
            }

            PlatformB = "ctrader";
        }
    }

    public bool IsPlatformBMtMode => !IsPlatformBCTrader;

    public string CTraderChannelMapName => CTraderFixConfig.ChannelMapName;

    // Phase 4: mode cTrader thay "Check Map 2" bằng trạng thái FIX QUOTE session (chỉ đọc, phản ánh config ĐANG CHẠY,
    // không phải form chưa Save).
    public string CTraderSessionStatus
    {
        get => _ctraderSessionStatus;
        private set => SetProperty(ref _ctraderSessionStatus, value);
    }

    public string CTraderQuoteHost
    {
        get => _ctraderQuoteHost;
        set => SetCTraderField(ref _ctraderQuoteHost, value);
    }

    public string CTraderQuotePortSsl
    {
        get => _ctraderQuotePortSsl;
        set => SetCTraderField(ref _ctraderQuotePortSsl, value);
    }

    public string CTraderQuotePortPlain
    {
        get => _ctraderQuotePortPlain;
        set => SetCTraderField(ref _ctraderQuotePortPlain, value);
    }

    public string CTraderTradeHost
    {
        get => _ctraderTradeHost;
        set => SetCTraderField(ref _ctraderTradeHost, value);
    }

    public string CTraderTradePortSsl
    {
        get => _ctraderTradePortSsl;
        set => SetCTraderField(ref _ctraderTradePortSsl, value);
    }

    public string CTraderTradePortPlain
    {
        get => _ctraderTradePortPlain;
        set => SetCTraderField(ref _ctraderTradePortPlain, value);
    }

    public bool CTraderUseSsl
    {
        get => _ctraderUseSsl;
        set
        {
            if (SetProperty(ref _ctraderUseSsl, value))
            {
                RefreshButtons();
            }
        }
    }

    public string CTraderSenderCompId
    {
        get => _ctraderSenderCompId;
        set
        {
            if (!SetCTraderField(ref _ctraderSenderCompId, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CTraderUsername));
            OnPropertyChanged(nameof(IsCTraderLiveSender));
        }
    }

    public string CTraderTargetCompId
    {
        get => _ctraderTargetCompId;
        set => SetCTraderField(ref _ctraderTargetCompId, value);
    }

    // Tag 553 read-only: tự suy từ SenderCompID (hoặc giá trị ghi đè đã lưu).
    public string CTraderUsername => BuildCTraderFixFromForm().ResolveUsername();

    // Chỉ cảnh báo, KHÔNG tham gia CanSave: cùng login FxPro có cả tài khoản live.
    public bool IsCTraderLiveSender =>
        !string.IsNullOrWhiteSpace(CTraderSenderCompId) && !BuildCTraderFixFromForm().IsDemoSender;

    public string CTraderSymbolId
    {
        get => _ctraderSymbolId;
        set => SetCTraderField(ref _ctraderSymbolId, value);
    }

    public string CTraderSymbolName => _ctraderSymbolName;

    public string CTraderVolumeBUnits
    {
        get => _ctraderVolumeBUnits;
        set
        {
            if (SetCTraderField(ref _ctraderVolumeBUnits, value))
            {
                OnPropertyChanged(nameof(CTraderVolumeLotHint));
            }
        }
    }

    public string CTraderContractSizeB
    {
        get => _ctraderContractSizeB;
        set
        {
            if (SetCTraderField(ref _ctraderContractSizeB, value))
            {
                OnPropertyChanged(nameof(CTraderVolumeLotHint));
            }
        }
    }

    public string CTraderVolumeALots
    {
        get => _ctraderVolumeALots;
        set => SetCTraderField(ref _ctraderVolumeALots, value);
    }

    public string CTraderVolumeLotHint
    {
        get
        {
            var units = ParseDecimal(CTraderVolumeBUnits);
            var contract = ParseDecimal(CTraderContractSizeB);
            return units > 0m && contract > 0m
                ? $"= {(units / contract).ToString("0.####", CultureInfo.InvariantCulture)} lot"
                : string.Empty;
        }
    }

    public string CTraderPasswordStatus =>
        !string.IsNullOrEmpty(_pendingCTraderPassword)
            ? "Mật khẩu mới sẽ được lưu"
            : !string.IsNullOrEmpty(_storedCTraderPassword)
                ? "Đã lưu (để trống = giữ nguyên)"
                : "Chưa có mật khẩu";

    // Gọi từ PasswordBox.PasswordChanged ở code-behind. Không có getter trả mật khẩu.
    public void SetCTraderPassword(string? password)
    {
        _pendingCTraderPassword = password ?? string.Empty;
        OnPropertyChanged(nameof(CTraderPasswordStatus));
        RefreshButtons();
    }

    public bool IsMapName1Valid
    {
        get => _isMap1Valid;
        private set => SetProperty(ref _isMap1Valid, value);
    }

    public bool IsMapName2Valid
    {
        get => _isMap2Valid;
        private set => SetProperty(ref _isMap2Valid, value);
    }

    public bool IsExistingRecordLoaded
    {
        get => _isExistingRecordLoaded;
        private set => SetProperty(ref _isExistingRecordLoaded, value);
    }

    public bool AreMapNamesEnabled
    {
        get => _areMapNamesEnabled;
        private set => SetProperty(ref _areMapNamesEnabled, value);
    }

    public bool CanSave
    {
        get => _canSave;
        private set => SetProperty(ref _canSave, value);
    }

    public AsyncRelayCommand CheckMap1Command { get; }
    public AsyncRelayCommand CheckMap2Command { get; }
    public AsyncRelayCommand CheckCTraderSessionCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand AddHwndColumnCommand { get; }
    public AsyncRelayCommand DeleteHwndColumnCommand { get; }

    private bool CanCheckMap1() => AreMapNamesEnabled && !string.IsNullOrWhiteSpace(MapName1);
    private bool CanCheckMap2() => AreMapNamesEnabled && !string.IsNullOrWhiteSpace(MapName2);
    private bool CanDeleteHwndColumn() => ManualHwndColumns.Count > 1;

    // Mode MT giữ nguyên điều kiện cũ. Mode cTrader không đòi MapName2 (kênh B là hằng CTRADER_B).
    private bool CanSaveCommand() =>
        CanSave &&
        !string.IsNullOrWhiteSpace(MapName1) &&
        (IsPlatformBCTrader || !string.IsNullOrWhiteSpace(MapName2));

    // Add/Delete chỉ tác động số cột CHART.
    private Task AddHwndColumnAsync()
    {
        var item = CreateColumnItem(new ManualHwndColumnConfig(string.Empty, TradeHwndA, string.Empty, TradeHwndB), ManualHwndColumns.Count + 1);
        ManualHwndColumns.Add(item);
        return Task.CompletedTask;
    }

    private Task DeleteHwndColumnAsync()
    {
        if (ManualHwndColumns.Count <= 1)
        {
            return Task.CompletedTask;
        }

        var lastIndex = ManualHwndColumns.Count - 1;
        var last = ManualHwndColumns[lastIndex];
        last.PropertyChanged -= OnColumnPropertyChanged;
        ManualHwndColumns.RemoveAt(lastIndex);
        ReindexColumns();
        RefreshButtons();
        return Task.CompletedTask;
    }

    private async Task LoadByMachineHostNameAsync()
    {
        try
        {
            ClearError();
            var loadResult = await _configService.LoadByMachineHostNameAsync();
            MachineHostName = loadResult.MachineHostName;

            if (!loadResult.Exists)
            {
                IsExistingRecordLoaded = false;
                AreMapNamesEnabled = false;
                MapName1 = string.Empty;
                MapName2 = string.Empty;
                InitializeColumns([ManualHwndColumnConfig.Empty]);
                LoadStatus = $"✖ Không có config cho host name: {MachineHostName}";
                ErrorMessage = "Không tìm thấy record config theo host name máy hiện tại.";
                RefreshDerivedState();
                return;
            }

            if (!loadResult.IsSuccess)
            {
                IsExistingRecordLoaded = false;
                AreMapNamesEnabled = false;
                InitializeColumns([ManualHwndColumnConfig.Empty]);
                LoadStatus = "✖ Không tải được config";
                if (!string.IsNullOrWhiteSpace(loadResult.Error))
                {
                    ErrorMessage = loadResult.Error;
                }
                RefreshDerivedState();
                return;
            }

            MapName1 = loadResult.MapName1;
            MapName2 = loadResult.MapName2;
            PlatformA = loadResult.PlatformA;
            PlatformB = loadResult.PlatformB;
            InitializeColumns(loadResult.ManualHwndColumns);
            ApplyCTraderFixToForm(loadResult.CTraderFix);

            _runtimeConfigState.Update(
                loadResult.MachineHostName,
                loadResult.MapName1,
                loadResult.MapName2,
                loadResult.PlatformA,
                loadResult.PlatformB,
                loadResult.Point,
                loadResult.OpenPts,
                loadResult.ConfirmGapPts,
                loadResult.HoldConfirmMs,
                loadResult.OpenPriceFreezeMs,
                loadResult.ClosePts,
                loadResult.CloseConfirmGapPts,
                loadResult.CloseTpProfit,
                loadResult.CloseConfirmTpProfit,
                loadResult.CloseHoldConfirmMs,
                loadResult.ClosePriceFreezeMs,
                loadResult.StartTimeHold,
                loadResult.EndTimeHold,
                loadResult.ConfirmLatencyMs,
                loadResult.MaxGap,
                loadResult.LimitMaxGap,
                loadResult.MaxSpread,
                loadResult.OpenMaxTimesTick,
                loadResult.CloseMaxTimesTick,
                loadResult.OpenPendingTimeMs,
                loadResult.ClosePendingTimeMs,
                loadResult.DelayOpenAMs,
                loadResult.DelayOpenBMs,
                loadResult.DelayCloseAMs,
                loadResult.DelayCloseBMs,
                loadResult.OpenNumberOfQualifyingTimes,
                loadResult.CloseNumberOfQualifyingTimes,
                closeMaxTpProfit: loadResult.CloseMaxTpProfit,
                limitMaxTp: loadResult.LimitMaxTp,
                oppositeOpenMinDistancePts: loadResult.OppositeOpenMinDistancePts,
                rdStartSameActionLockSeconds: loadResult.RdStartSameActionLockSeconds ?? -1,
                rdEndSameActionLockSeconds: loadResult.RdEndSameActionLockSeconds ?? -1,
                rdStartPostCloseLockSeconds: loadResult.RdStartPostCloseLockSeconds,
                rdEndPostCloseLockSeconds: loadResult.RdEndPostCloseLockSeconds,
                rdStartPostOpenLockSeconds: loadResult.RdStartPostOpenLockSeconds,
                rdEndPostOpenLockSeconds: loadResult.RdEndPostOpenLockSeconds,
                // Hai tham so nay TUNG BI BO SOT o day. Vi ctor cua ConfigViewModel tu goi
                // LoadByMachineHostNameAsync, chi can MO cua so Config la Update chay ma khong mang
                // theo min_profit_to_close -> gate min_profit bi tat am tham, khong dau vet log,
                // va ton tai den khi bam Reconnect hoac khoi dong lai app.
                // Day la duong NAP CONFIG nen phai truyen gia tri DB tuong minh, khong duoc dua vao
                // sentinel (sentinel chi giu gia tri cu, tuc se bo qua gia tri moi tu DB).
                minProfitToClose: loadResult.MinProfitToClose,
                maxLifeTimeBySecond: loadResult.MaxLifeTimeBySecond,
                openMaxLastGapPts: loadResult.OpenMaxLastGapPts,
                // Task R8-B: cung lop loi tren. Thieu tham so nay thi sua ctrader_confirm_latency_b trong DB
                // roi mo cua so Config se KHONG an, phai Reconnect/khoi dong lai app moi thay. `null` tu DB di
                // nguyen (null != -1) va co nghia la chan B dung chung nguong cua A.
                ctraderConfirmLatencyB: loadResult.CTraderConfirmLatencyB,
                closeRdStartSameActionLockSeconds: loadResult.CloseRdStartSameActionLockSeconds ?? -1,
                closeRdEndSameActionLockSeconds: loadResult.CloseRdEndSameActionLockSeconds ?? -1,
                // start HOẶC end null trong DB → tắt nhóm đó (không chờ same-action).
                sameActionLockEnabled: loadResult.RdStartSameActionLockSeconds.HasValue && loadResult.RdEndSameActionLockSeconds.HasValue,
                closeSameActionLockEnabled: loadResult.CloseRdStartSameActionLockSeconds.HasValue && loadResult.CloseRdEndSameActionLockSeconds.HasValue);
            _runtimeConfigState.UpdateSignalCycleSize(loadResult.SignalCycleSize);
            _runtimeConfigState.UpdateGapStability(
                loadResult.OpenGapStability!,
                loadResult.CloseGapStability!);
            _runtimeConfigState.UpdateScheduleSleeping(loadResult.ScheduleSleepingJson);
            _runtimeConfigState.UpdateManualTradeHwnd(BuildManualHwndColumns());
            _runtimeConfigState.UpdateCTraderFix(loadResult.CTraderFix);

            IsExistingRecordLoaded = true;
            AreMapNamesEnabled = true;

            IsMapName1Valid = false;
            IsMapName2Valid = false;
            Map1CheckStatus = "Chưa kiểm tra";
            Map2CheckStatus = "Chưa kiểm tra";
            LoadStatus = "✔ Đã tải config theo host name";
            RefreshDerivedState();
        }
        catch (Exception ex)
        {
            IsExistingRecordLoaded = false;
            AreMapNamesEnabled = false;
            LoadStatus = "✖ Không tải được config";
            ErrorMessage = $"Lỗi load config theo host name: {GetErrorMessage(ex)}";
            RefreshDerivedState();
        }

        RefreshButtons();
    }

    private Task CheckMap1Async()
    {
        ClearError();
        IsMapName1Valid = SharedMemoryChecker.MapExists(MapName1.Trim());
        Map1CheckStatus = IsMapName1Valid ? "✔ Map tồn tại" : "✖ Map không tồn tại";
        RefreshDerivedState();
        RefreshButtons();
        return Task.CompletedTask;
    }

    private Task CheckCTraderSessionAsync()
    {
        CTraderSessionStatus = _ctraderQuoteSession?.StatusText ?? "Không có FIX session";
        return Task.CompletedTask;
    }

    private Task CheckMap2Async()
    {
        ClearError();
        IsMapName2Valid = SharedMemoryChecker.MapExists(MapName2.Trim());
        Map2CheckStatus = IsMapName2Valid ? "✔ Map tồn tại" : "✖ Map không tồn tại";
        RefreshDerivedState();
        RefreshButtons();
        return Task.CompletedTask;
    }

    private async Task SaveAsync()
    {
        if (!CanSaveCommand() || !IsExistingRecordLoaded)
        {
            ErrorMessage = "Không thể lưu: dữ liệu chưa hợp lệ hoặc chưa load record.";
            return;
        }

        try
        {
            ClearError();
            var columns = BuildManualHwndColumns();

            // Re-validate HWND trước khi persist: chặn lưu nếu có handle sai định dạng /
            // trống / cửa sổ không tồn tại (tránh lưu cấu hình hỏng rồi vẫn skip).
            // Sàn B là cTrader thì HWND B không bắt buộc.
            var hwndIssues = _hwndHealthChecker.Check(columns, requiresExchangeBHwnd: !IsPlatformBCTrader);
            if (hwndIssues.Count > 0)
            {
                LoadStatus = "✖ Save thất bại";
                ErrorMessage = "HWND không hợp lệ:" + Environment.NewLine +
                    string.Join(Environment.NewLine,
                        hwndIssues.Select(i => $"• {i.Label}: {(string.IsNullOrEmpty(i.Value) ? "(trống)" : i.Value)} — {ReasonText(i.Kind)}"));
                return;
            }

            // Câu 4: đổi nền tảng sàn B có liên quan cTrader khi còn slot → chặn (luật + test ở PlatformBSwitchGuard).
            var switchDecision = PlatformBSwitchGuard.Evaluate(
                _runtimeConfigState.CurrentPlatformB,
                PlatformB,
                _portfolioCoordinator.LiveAndPendingTotalCount);
            if (!switchDecision.Allowed)
            {
                LoadStatus = "✖ Save thất bại";
                ErrorMessage = switchDecision.Message ?? string.Empty;
                return;
            }

            var ctraderFix = BuildCTraderFixFromForm();
            if (IsPlatformBCTrader)
            {
                var missing = ctraderFix.GetMissingRequiredFields(ctraderFix.HasPassword);
                if (missing.Count > 0)
                {
                    LoadStatus = "✖ Save thất bại";
                    ErrorMessage = "Thiếu thông số cTrader: " + string.Join(", ", missing);
                    return;
                }
            }

            // Save ghi CẢ mapNames[1] MT lẫn khối ctraderFix (giữ dữ liệu mode đang ẩn).
            var saveResult = await _configService.SaveByMachineHostNameAsync(
                MapName1, MapName2, PlatformA, PlatformB, columns, ctraderFix: ctraderFix);
            if (!saveResult.IsSuccess)
            {
                LoadStatus = "✖ Save thất bại";
                ErrorMessage = string.IsNullOrWhiteSpace(saveResult.Error)
                    ? "Lưu thất bại: không có bản ghi nào được cập nhật."
                    : saveResult.Error;
                return;
            }

            if (!string.IsNullOrWhiteSpace(saveResult.MachineHostName))
            {
                MachineHostName = saveResult.MachineHostName;
            }

            LoadStatus = "✔ Lưu thành công";
            _runtimeConfigState.Update(MachineHostName, MapName1, MapName2, _runtimeConfigState.CurrentPoint);
            _runtimeConfigState.UpdatePlatform(PlatformA, PlatformB);
            _runtimeConfigState.UpdateManualTradeHwnd(columns);
            _runtimeConfigState.UpdateCTraderFix(ctraderFix);
            _storedCTraderPassword = ctraderFix.Password;
            _pendingCTraderPassword = string.Empty;
            SafeConfigLog(
                $"[CONFIG][INFO] Runtime config updated: host={MachineHostName} " +
                $"map1={MapName1} map2={MapName2} platformA={PlatformA} platformB={PlatformB} " +
                $"manualColumns={columns.Count}");
            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            LoadStatus = "✖ Save thất bại";
            ErrorMessage = $"Lỗi khi save: {GetErrorMessage(ex)}";
        }
    }

    private Task CancelAsync()
    {
        RequestClose?.Invoke(false);
        return Task.CompletedTask;
    }

    private static string ReasonText(HwndIssueKind kind) => kind switch
    {
        HwndIssueKind.Empty => "để trống",
        HwndIssueKind.BadFormat => "sai định dạng (cần 0x... hoặc số thập phân)",
        HwndIssueKind.WindowMissing => "cửa sổ không tồn tại",
        _ => "không hợp lệ"
    };

    private void RefreshDerivedState()
    {
        CanSave =
            IsExistingRecordLoaded &&
            !string.IsNullOrWhiteSpace(MapName1) &&
            (IsPlatformBCTrader
                ? BuildCTraderFixFromForm() is var fix && fix.GetMissingRequiredFields(fix.HasPassword).Count == 0
                : !string.IsNullOrWhiteSpace(MapName2)) &&
            ManualHwndColumns.Count > 0;
    }

    private bool SetCTraderField(ref string field, string? value)
    {
        if (!SetProperty(ref field, value ?? string.Empty))
        {
            return false;
        }

        RefreshButtons();
        return true;
    }

    private void ApplyCTraderFixToForm(CTraderFixConfig? source)
    {
        var fix = (source ?? CTraderFixConfig.Empty).Normalize();
        CTraderQuoteHost = fix.Quote.Host;
        CTraderQuotePortSsl = FormatInt(fix.Quote.PortSsl);
        CTraderQuotePortPlain = FormatInt(fix.Quote.PortPlain);
        CTraderTradeHost = fix.Trade.Host;
        CTraderTradePortSsl = FormatInt(fix.Trade.PortSsl);
        CTraderTradePortPlain = FormatInt(fix.Trade.PortPlain);
        CTraderUseSsl = fix.UseSsl;
        CTraderSenderCompId = fix.SenderCompId;
        CTraderTargetCompId = fix.TargetCompId;
        _ctraderUsernameOverride = fix.Username;
        CTraderSymbolId = FormatInt(fix.SymbolId);
        _ctraderSymbolName = fix.SymbolName;
        CTraderVolumeBUnits = FormatDecimal(fix.VolumeBUnits);
        CTraderContractSizeB = FormatDecimal(fix.ContractSizeB);
        CTraderVolumeALots = FormatDecimal(fix.VolumeALots);
        // Không đổ mật khẩu ngược vào PasswordBox; chỉ nhớ để Save với ô trống giữ nguyên.
        _storedCTraderPassword = fix.Password;
        _pendingCTraderPassword = string.Empty;
        OnPropertyChanged(nameof(CTraderUsername));
        OnPropertyChanged(nameof(CTraderSymbolName));
        OnPropertyChanged(nameof(CTraderPasswordStatus));
    }

    private CTraderFixConfig BuildCTraderFixFromForm()
        => new CTraderFixConfig(
            new CTraderEndpoint(CTraderQuoteHost, ParseInt(CTraderQuotePortSsl), ParseInt(CTraderQuotePortPlain)),
            new CTraderEndpoint(CTraderTradeHost, ParseInt(CTraderTradePortSsl), ParseInt(CTraderTradePortPlain)),
            CTraderUseSsl,
            CTraderSenderCompId,
            CTraderTargetCompId,
            string.IsNullOrEmpty(_pendingCTraderPassword) ? _storedCTraderPassword : _pendingCTraderPassword,
            _ctraderUsernameOverride,
            ParseInt(CTraderSymbolId),
            _ctraderSymbolName,
            ParseDecimal(CTraderVolumeBUnits),
            ParseDecimal(CTraderContractSizeB),
            ParseDecimal(CTraderVolumeALots)).Normalize();

    private static int ParseInt(string? text)
        => int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static decimal ParseDecimal(string? text)
        => decimal.TryParse((text ?? string.Empty).Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;

    private static string FormatInt(int value) => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static string FormatDecimal(decimal value)
        => value > 0m ? value.ToString("0.########", CultureInfo.InvariantCulture) : string.Empty;

    private void RefreshButtons()
    {
        RefreshDerivedState();
        CheckMap1Command?.RaiseCanExecuteChanged();
        CheckMap2Command?.RaiseCanExecuteChanged();
        SaveCommand?.RaiseCanExecuteChanged();
        DeleteHwndColumnCommand?.RaiseCanExecuteChanged();
    }

    private void InitializeColumns(IReadOnlyList<ManualHwndColumnConfig>? columns)
    {
        foreach (var column in ManualHwndColumns)
        {
            column.PropertyChanged -= OnColumnPropertyChanged;
        }

        ManualHwndColumns.Clear();

        var normalizedColumns = (columns ?? [ManualHwndColumnConfig.Empty])
            .Select(x => (x ?? ManualHwndColumnConfig.Empty).Normalize())
            .ToList();

        if (normalizedColumns.Count == 0)
        {
            normalizedColumns.Add(ManualHwndColumnConfig.Empty);
        }

        var first = normalizedColumns[0];
        TradeHwndA = first.TradeHwndA;
        TradeHwndB = first.TradeHwndB;

        for (var i = 0; i < normalizedColumns.Count; i++)
        {
            ManualHwndColumns.Add(CreateColumnItem(new ManualHwndColumnConfig(
                normalizedColumns[i].ChartHwndA,
                TradeHwndA,
                normalizedColumns[i].ChartHwndB,
                TradeHwndB), i + 1));
        }

        ReindexColumns();
        OnPropertyChanged(nameof(ChartHwndA));
        OnPropertyChanged(nameof(ChartHwndB));
        RefreshButtons();
    }

    private ManualHwndColumnItemViewModel CreateColumnItem(ManualHwndColumnConfig source, int displayIndex)
    {
        var item = new ManualHwndColumnItemViewModel
        {
            DisplayIndex = displayIndex,
            ChartHwndA = source.ChartHwndA,
            TradeHwndA = source.TradeHwndA,
            ChartHwndB = source.ChartHwndB,
            TradeHwndB = source.TradeHwndB
        };

        item.PropertyChanged += OnColumnPropertyChanged;
        return item;
    }

    private IReadOnlyList<ManualHwndColumnConfig> BuildManualHwndColumns()
    {
        if (ManualHwndColumns.Count == 0)
        {
            return [new ManualHwndColumnConfig(string.Empty, TradeHwndA, string.Empty, TradeHwndB)];
        }

        return ManualHwndColumns
            .Select(x => new ManualHwndColumnConfig(x.ChartHwndA, TradeHwndA, x.ChartHwndB, TradeHwndB).Normalize())
            .ToList();
    }

    private void EnsureAtLeastOneColumn()
    {
        if (ManualHwndColumns.Count > 0)
        {
            return;
        }

        ManualHwndColumns.Add(CreateColumnItem(new ManualHwndColumnConfig(string.Empty, TradeHwndA, string.Empty, TradeHwndB), 1));
    }

    private void ReindexColumns()
    {
        for (var i = 0; i < ManualHwndColumns.Count; i++)
        {
            ManualHwndColumns[i].DisplayIndex = i + 1;
        }
    }

    private void OnManualHwndColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var oldItem in e.OldItems.OfType<ManualHwndColumnItemViewModel>())
            {
                oldItem.PropertyChanged -= OnColumnPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var newItem in e.NewItems.OfType<ManualHwndColumnItemViewModel>())
            {
                newItem.PropertyChanged -= OnColumnPropertyChanged;
                newItem.PropertyChanged += OnColumnPropertyChanged;
                newItem.TradeHwndA = TradeHwndA;
                newItem.TradeHwndB = TradeHwndB;
            }
        }

        ReindexColumns();
        OnPropertyChanged(nameof(ChartHwndA));
        OnPropertyChanged(nameof(ChartHwndB));
        RefreshButtons();
    }

    private void OnColumnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshButtons();
    }

    private void ClearError() => ErrorMessage = string.Empty;

    private static string GetErrorMessage(Exception ex)
    {
        var message = ex.Message;
        if (ex.InnerException is not null)
        {
            message = $"{message} | Inner: {ex.InnerException.Message}";
        }

        return message;
    }

    private static string NormalizePlatform(string? platform)
    {
        var normalized = (platform ?? string.Empty).Trim().ToLower();
        return normalized is "mt4" or "mt5" or "ctrader" ? normalized : "mt5";
    }

    private void SafeConfigLog(string message)
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
}
