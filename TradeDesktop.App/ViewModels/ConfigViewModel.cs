using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using TradeDesktop.App.Commands;
using TradeDesktop.App.Helpers;
using TradeDesktop.App.Services;
using TradeDesktop.App.State;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;

namespace TradeDesktop.App.ViewModels;

public sealed class ConfigViewModel : ObservableObject
{
    private readonly RuntimeConfigState _runtimeConfigState;
    private readonly IConfigService _configService;
    private readonly ITradeSessionFileLogger _tradeSessionFileLogger;
    private string _machineHostName = string.Empty;

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
        ITradeSessionFileLogger tradeSessionFileLogger)
    {
        _runtimeConfigState = runtimeConfigState;
        _configService = configService;
        _tradeSessionFileLogger = tradeSessionFileLogger;

        CheckMap1Command = new AsyncRelayCommand(CheckMap1Async, CanCheckMap1);
        CheckMap2Command = new AsyncRelayCommand(CheckMap2Async, CanCheckMap2);
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSaveCommand);
        CancelCommand = new AsyncRelayCommand(CancelAsync);
        AddHwndColumnCommand = new AsyncRelayCommand(AddHwndColumnAsync);
        DeleteHwndColumnCommand = new AsyncRelayCommand(DeleteHwndColumnAsync, CanDeleteHwndColumn);

        MachineHostName = runtimeConfigState.CurrentMachineHostName;
        MapName1 = runtimeConfigState.CurrentMapName1;
        MapName2 = runtimeConfigState.CurrentMapName2;
        PlatformA = runtimeConfigState.CurrentPlatformA;
        PlatformB = runtimeConfigState.CurrentPlatformB;

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
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand AddHwndColumnCommand { get; }
    public AsyncRelayCommand DeleteHwndColumnCommand { get; }

    private bool CanCheckMap1() => AreMapNamesEnabled && !string.IsNullOrWhiteSpace(MapName1);
    private bool CanCheckMap2() => AreMapNamesEnabled && !string.IsNullOrWhiteSpace(MapName2);
    private bool CanDeleteHwndColumn() => ManualHwndColumns.Count > 1;

    private bool CanSaveCommand() =>
        CanSave &&
        !string.IsNullOrWhiteSpace(MapName1) &&
        !string.IsNullOrWhiteSpace(MapName2);

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
                loadResult.CloseHoldConfirmMs,
                loadResult.ClosePriceFreezeMs,
                loadResult.StartTimeHold,
                loadResult.EndTimeHold,
                loadResult.StartWaitTime,
                loadResult.EndWaitTime,
                loadResult.ConfirmLatencyMs,
                loadResult.MaxGap,
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
                loadResult.OpenGapTick,
                loadResult.CloseGapTick,
                loadResult.CoolDownGapTick);
            _runtimeConfigState.UpdateManualTradeHwnd(BuildManualHwndColumns());

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
            var saveResult = await _configService.SaveByMachineHostNameAsync(MapName1, MapName2, PlatformA, PlatformB, columns);
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

    private void RefreshDerivedState()
    {
        CanSave =
            IsExistingRecordLoaded &&
            !string.IsNullOrWhiteSpace(MapName1) &&
            !string.IsNullOrWhiteSpace(MapName2) &&
            ManualHwndColumns.Count > 0;
    }

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
        return normalized is "mt4" or "mt5" ? normalized : "mt5";
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