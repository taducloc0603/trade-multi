using System.Globalization;
using QuickFix;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.CTrader;

namespace TradeDesktop.Infrastructure.CTrader;

// Phase 5 — FIX TRADE session, CHỈ ĐỌC. Transport riêng, chỉ start role TRADE (topology hai initiator của Spotware).
// Gửi đúng hai loại request: SecurityListRequest (Phase 0 câu 3: TRADE cũng trả lời) và RequestForPositions. KHÔNG có
// NewOrderSingle ở bất kỳ đâu — adapter thuần bị động (Rule E): không flatten, không retry lệnh, không reconcile bằng lệnh.
//
// Vòng đời do decorator CTraderAwareTradesReader điều khiển (EnsureState mỗi lần đọc trades, 500 ms) → revert đúng dòng
// đăng ký decorator là tắt luôn TRADE session. Luồng thread giống CTraderQuoteSession: start/stop trên chuỗi task nền,
// callback QuickFIX/n trên thread của nó, cache/catalog chỉ chạm dưới _stateLock, event transport cũ bỏ qua nhờ _generation.
public sealed class CTraderTradeSession : ICTraderTradeSession, IDisposable
{
    // Phase 5 câu 2 (ĐÃ QUYẾT): RequestForPositions lúc logon + mỗi 60 s.
    public const long ReconcileIntervalMs = 60_000;

    // Phase 5 câu 1 (ĐÃ QUYẾT): WARN ngay lúc logon; Telegram CTRADER_POSITIONS_NOT_SYNCED nếu sau 60 s vẫn chưa sync.
    public const long NotSyncedAlertMs = 60_000;

    private const long StatsWindowMs = 60_000;

    private readonly Func<CTraderFixConfig, ICTraderFixTransport> _transportFactory;
    private readonly Func<long> _tickCount;
    private readonly Func<long> _unixMs;
    private readonly Timer? _timer;

    private readonly object _desiredLock = new();
    private bool _desiredActive;
    private CTraderFixConfig? _desiredConfig;
    private string? _configProblem;
    private bool _disposed;
    private Task _lifecycle = Task.CompletedTask;

    // Chỉ chuỗi _lifecycle chạm.
    private ICTraderFixTransport? _transport;
    private CTraderFixConfig? _activeConfig;

    private readonly object _stateLock = new();
    private readonly CTraderSecurityCatalog _catalog = new();
    private ICTraderFixTransport? _liveTransport;
    private int _generation;
    private bool _tradeLoggedOn;
    private CTraderPositionCache? _cache;
    // Phase 6: lịch sử sống theo session (qua relogon), tạo mới khi start transport (đổi config / bật lại cTrader).
    private CTraderHistoryProjector _history = new();
    private readonly List<CTraderClosedPosition> _closedToLog = [];
    private bool? _lastHistoryAvailable;
    private int _symbolId;
    private decimal _contractSizeB;
    private long _loggedOnTick;
    private long _lastPosReqTick;
    private string? _lastPosReqId;
    private bool _notSyncedAlerted;
    private int _failedReconnects;
    private bool? _lastMapAvailable;
    private string _status = "Chưa bật (sàn B không phải cTrader)";

    private long _windowStartTick = -1;
    private int _windowReads;
    private int _windowAvailableReads;
    private int _windowPosReqs;
    private int _windowApReports;

    public CTraderTradeSession(
        Func<CTraderFixConfig, ICTraderFixTransport> transportFactory,
        Func<long>? tickCount = null,
        Func<long>? unixMs = null,
        bool startTimer = true)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _tickCount = tickCount ?? (() => Environment.TickCount64);
        _unixMs = unixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (startTimer)
        {
            _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    public static CTraderTradeSession CreateDefault()
    {
        CTraderTradeSession? self = null;
        var raw = CTraderQuoteSession.IsRawLogEnabled();
        self = new CTraderTradeSession(config => new QuickFixCTraderTransport(
            config,
            Path.Combine(AppContext.BaseDirectory, CTraderQuoteSession.DictionaryFileName),
            raw ? line => self?.Emit("DEBUG", line) : null));
        return self;
    }

    public event Action<CTraderTradeSessionEvent>? EventRaised;
    public event Action<string>? LogLine;

    public string StatusText
    {
        get
        {
            lock (_stateLock)
            {
                return _status;
            }
        }
    }

    public Task WaitForLifecycleAsync()
    {
        lock (_desiredLock)
        {
            return _lifecycle;
        }
    }

    public void EnsureState(string platformB, CTraderFixConfig config)
    {
        try
        {
            var isCTrader = string.Equals(platformB?.Trim(), CTraderQuoteSession.PlatformCTrader, StringComparison.OrdinalIgnoreCase);
            var normalized = (config ?? CTraderFixConfig.Empty).Normalize();
            string? problem = null;
            if (isCTrader)
            {
                var missing = normalized.GetMissingRequiredFields(normalized.HasPassword);
                if (missing.Count > 0)
                {
                    problem = "Cấu hình cTrader thiếu: " + string.Join(", ", missing);
                }
            }

            var want = isCTrader && problem is null;
            var target = want ? normalized : null;

            lock (_desiredLock)
            {
                if (_disposed)
                {
                    return;
                }

                if (!string.Equals(problem, _configProblem, StringComparison.Ordinal))
                {
                    _configProblem = problem;
                    if (problem is not null)
                    {
                        SetStatus(problem);
                        // Config rỗng hoàn toàn = chưa nạp xong (platform_b nạp trước ctraderFix lúc khởi động) → không phải lỗi cấu hình.
                        Emit(normalized == CTraderFixConfig.Empty.Normalize() ? "INFO" : "ERROR",
                            $"{problem} — không mở TRADE session");
                    }
                }

                if (want == _desiredActive && Equals(target, _desiredConfig))
                {
                    return;
                }

                _desiredActive = want;
                _desiredConfig = target;
                _lifecycle = _lifecycle.ContinueWith(_ => Reconcile(), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            Emit("ERROR", $"EnsureState lỗi: {ex.Message}");
        }
    }

    public SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName, bool quoteLoggedOn)
    {
        try
        {
            SharedMapReadResult<TradeSharedRecord> result;
            string? transition = null;
            lock (_stateLock)
            {
                var symbolResolved = _catalog.TryGet(_symbolId, out var info);
                var synced = _cache?.PositionsSynced ?? false;
                var health = new CTraderSessionHealth(quoteLoggedOn, _tradeLoggedOn, symbolResolved, synced, false, 0);

                result = _cache is null
                    ? SharedMapReadResult<TradeSharedRecord>.MapNotFound(mapName)
                    : _cache.ReadAsMapResult(health, mapName, info?.SymbolName ?? string.Empty, _contractSizeB);

                _windowReads++;
                if (result.IsMapAvailable)
                {
                    _windowAvailableReads++;
                }

                if (_lastMapAvailable != result.IsMapAvailable)
                {
                    transition = result.IsMapAvailable
                        ? $"trades map AVAILABLE count={result.Count} version={result.Timestamp} connected={result.Connected}"
                        : $"trades map MapNotFound (trade_logged_on={_tradeLoggedOn} symbol_resolved={symbolResolved} " +
                          $"positions_synced={synced} cache={(_cache is null ? "none" : "ok")})";
                    _lastMapAvailable = result.IsMapAvailable;
                }
            }

            if (transition is not null)
            {
                Emit("INFO", transition);
            }

            return result;
        }
        catch (Exception)
        {
            return SharedMapReadResult<TradeSharedRecord>.MapNotFound(mapName);
        }
    }

    // Phase 6 — R2 y hệt trades (cùng bảng sức khoẻ); R3: Timestamp = version RIÊNG của history (không dùng chung trades);
    // R10: Commission=0, Profit tính lại (Close−Open)×point — không phải số broker.
    public SharedMapReadResult<HistorySharedRecord> ReadHistory(string mapName, bool quoteLoggedOn, int point)
    {
        try
        {
            SharedMapReadResult<HistorySharedRecord> result;
            string? transition = null;
            lock (_stateLock)
            {
                var symbolResolved = _catalog.TryGet(_symbolId, out var info);
                var synced = _cache?.PositionsSynced ?? false;
                var health = new CTraderSessionHealth(quoteLoggedOn, _tradeLoggedOn, symbolResolved, synced, false, 0);

                result = _cache is null
                    ? SharedMapReadResult<HistorySharedRecord>.MapNotFound(mapName)
                    : _history.ReadAsMapResult(health, mapName, info?.SymbolName ?? string.Empty, _contractSizeB, point);

                if (_lastHistoryAvailable != result.IsMapAvailable)
                {
                    transition = result.IsMapAvailable
                        ? $"history map AVAILABLE count={result.Count} version={result.Timestamp} connected={result.Connected}"
                        : $"history map MapNotFound (trade_logged_on={_tradeLoggedOn} symbol_resolved={symbolResolved} positions_synced={synced})";
                    _lastHistoryAvailable = result.IsMapAvailable;
                }
            }

            if (transition is not null)
            {
                Emit("INFO", transition);
            }

            return result;
        }
        catch (Exception)
        {
            return SharedMapReadResult<HistorySharedRecord>.MapNotFound(mapName);
        }
    }

    // Gọi mỗi giây bởi timer (test gọi tay): reconciliation 60 s, cảnh báo chưa sync, dòng STATS.
    public void Tick()
    {
        try
        {
            var now = _tickCount();
            ICTraderFixTransport? transport = null;
            string? posReqId = null;
            string? notSyncedMessage = null;
            string? stats = null;

            lock (_stateLock)
            {
                if (_tradeLoggedOn && _liveTransport is not null)
                {
                    if (now - _lastPosReqTick >= ReconcileIntervalMs)
                    {
                        transport = _liveTransport;
                        posReqId = NextPosReqId();
                        _lastPosReqTick = now;
                        _lastPosReqId = posReqId;
                        _windowPosReqs++;
                    }

                    var synced = _cache?.PositionsSynced ?? false;
                    if (!synced && !_notSyncedAlerted && now - _loggedOnTick >= NotSyncedAlertMs)
                    {
                        _notSyncedAlerted = true;
                        notSyncedMessage = $"PositionsSynced=false sau {now - _loggedOnTick} ms kể từ logon TRADE " +
                                           $"(710 gần nhất={_lastPosReqId ?? "-"}) — trades map B vẫn MapNotFound";
                    }
                }

                if (_liveTransport is not null)
                {
                    if (_windowStartTick < 0)
                    {
                        _windowStartTick = now;
                    }
                    else if (now - _windowStartTick >= StatsWindowMs)
                    {
                        stats = $"[STATS] window_ms={now - _windowStartTick} trade_logged_on={_tradeLoggedOn} " +
                                $"positions_synced={_cache?.PositionsSynced ?? false} version={_cache?.Version ?? 0} " +
                                $"count={_cache?.Count ?? 0} history_version={_history.Version} history_count={_history.Count} reads={_windowReads} reads_available={_windowAvailableReads} " +
                                $"posreqs_sent={_windowPosReqs} ap_reports={_windowApReports} last_710={_lastPosReqId ?? "-"}";
                        _windowStartTick = now;
                        _windowReads = 0;
                        _windowAvailableReads = 0;
                        _windowPosReqs = 0;
                        _windowApReports = 0;
                    }
                }
            }

            if (notSyncedMessage is not null)
            {
                Emit("WARN", notSyncedMessage);
                Raise(CTraderTradeEventKind.PositionsNotSynced, notSyncedMessage);
            }

            if (transport is not null && posReqId is not null)
            {
                SendPositionsRequest(transport, posReqId, "reconciliation");
            }

            if (stats is not null)
            {
                Emit("INFO", stats);
            }
        }
        catch (Exception ex)
        {
            Emit("ERROR", $"Tick lỗi: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Task lifecycle;
        lock (_desiredLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _desiredActive = false;
            _desiredConfig = null;
            _lifecycle = _lifecycle.ContinueWith(_ => Reconcile(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            lifecycle = _lifecycle;
        }

        _timer?.Dispose();
        try
        {
            lifecycle.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Thoát app không được treo vì logout.
        }
    }

    private void Reconcile()
    {
        try
        {
            bool want;
            CTraderFixConfig? config;
            lock (_desiredLock)
            {
                want = _desiredActive;
                config = _desiredConfig;
            }

            if (_transport is not null && (!want || !Equals(config, _activeConfig)))
            {
                StopTransport(want ? "cấu hình FIX đổi → khởi động lại session" : "sàn B không còn là cTrader / app dừng");
            }

            if (want && config is not null && _transport is null)
            {
                StartTransport(config);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Lỗi: {ex.Message}");
            Emit("ERROR", $"Reconcile lỗi: {ex.Message}");
            Raise(CTraderTradeEventKind.Error, ex.Message);
        }
    }

    private void StartTransport(CTraderFixConfig config)
    {
        var transport = _transportFactory(config);
        int generation;
        lock (_stateLock)
        {
            generation = ++_generation;
            _liveTransport = transport;
            _tradeLoggedOn = false;
            _cache = new CTraderPositionCache(_tickCount);
            // Phase 6 câu 2: record history CHỈ khi position đóng hẳn = biến mất khỏi cache qua fill (ER 150=F).
            // PositionClosed phát bên trong ApplyExecutionReport, tức đang giữ _stateLock.
            _history = new CTraderHistoryProjector();
            _lastHistoryAvailable = null;
            _closedToLog.Clear();
            _cache.PositionClosed += closed =>
            {
                _history.OnPositionClosed(closed);
                _closedToLog.Add(closed);
            };
            _catalog.Clear();
            _symbolId = config.SymbolId;
            _contractSizeB = config.ContractSizeB;
            _failedReconnects = 0;
            _lastPosReqId = null;
            _windowStartTick = -1;
            _status = "Đang kết nối TRADE…";
        }

        transport.LoggedOn += role => OnLoggedOn(transport, generation, role);
        transport.LoggedOut += role => OnLoggedOut(generation, role);
        transport.MessageReceived += (role, message) => OnMessage(generation, role, message);

        _transport = transport;
        _activeConfig = config;

        Emit("INFO",
            $"TRADE connecting host={config.Trade.Host} port={config.ActivePort(CTraderSessionRole.Trade)} " +
            $"ssl={(config.UseSsl ? "Y" : "N")} sender={config.SenderCompId} symbolId={config.SymbolId} (chỉ đọc positions)");
        Raise(CTraderTradeEventKind.Connecting, config.Trade.Host);
        transport.Start(CTraderSessionRole.Trade);
    }

    private void StopTransport(string reason)
    {
        var transport = _transport!;
        _transport = null;
        _activeConfig = null;

        lock (_stateLock)
        {
            // Tăng generation TRƯỚC khi logout: LoggedOut do chính mình gây ra không bị coi là mất session.
            _generation++;
            _liveTransport = null;
            _tradeLoggedOn = false;
            _cache?.OnLoggedOut();
            _cache = null;
            _catalog.Clear();
            _status = $"Đã dừng: {reason}";
        }

        try
        {
            transport.Stop(CTraderSessionRole.Trade);
        }
        catch (Exception ex)
        {
            Emit("WARN", $"TRADE stop lỗi: {ex.Message}");
        }

        try
        {
            (transport as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Emit("WARN", $"TRADE dispose lỗi: {ex.Message}");
        }

        Emit("INFO", $"TRADE stopped ({reason})");
        Raise(CTraderTradeEventKind.Stopped, reason);
    }

    private void OnLoggedOn(ICTraderFixTransport transport, int generation, CTraderSessionRole role)
    {
        if (role != CTraderSessionRole.Trade)
        {
            return;
        }

        string posReqId;
        int failedBefore;
        lock (_stateLock)
        {
            if (generation != _generation)
            {
                return;
            }

            _tradeLoggedOn = true;
            // R2: logon lại → PHẢI sync lại. OnLoggedOut đã hạ PositionsSynced; catalog cũng xin lại.
            _cache?.OnLoggedOut();
            _catalog.Clear();
            var now = _tickCount();
            _loggedOnTick = now;
            _lastPosReqTick = now;
            posReqId = NextPosReqId();
            _lastPosReqId = posReqId;
            _windowPosReqs++;
            _notSyncedAlerted = false;
            failedBefore = _failedReconnects;
            _failedReconnects = 0;
            _status = "TRADE đã logon — chờ SecurityList + PositionReport";
        }

        Emit("INFO", failedBefore > 0 ? $"TRADE logged on (sau {failedBefore} lần reconnect hỏng)" : "TRADE logged on");
        Emit("WARN", "PositionsSynced=false — trades map B là MapNotFound cho tới khi batch PositionReport hoàn tất (R2)");
        Raise(CTraderTradeEventKind.LoggedOn, "TRADE logged on");

        try
        {
            var securityReqId = "sec-trade-" + _unixMs().ToString(CultureInfo.InvariantCulture);
            var securitySent = transport.Send(CTraderSessionRole.Trade, CTraderMessageFactory.SecurityListRequest(securityReqId));
            Emit("INFO", $"TRADE SecurityListRequest sent={securitySent}");
        }
        catch (Exception ex)
        {
            Emit("ERROR", $"TRADE gửi SecurityListRequest lỗi: {ex.Message}");
        }

        SendPositionsRequest(transport, posReqId, "logon");
    }

    private void OnLoggedOut(int generation, CTraderSessionRole role)
    {
        if (role != CTraderSessionRole.Trade)
        {
            return;
        }

        bool wasLoggedOn;
        int failed;
        lock (_stateLock)
        {
            if (generation != _generation)
            {
                return;
            }

            // R2: về MapNotFound NGAY — lần đọc kế tiếp thấy PositionsSynced=false.
            wasLoggedOn = _tradeLoggedOn;
            _tradeLoggedOn = false;
            _cache?.OnLoggedOut();
            _catalog.Clear();
            _status = "Mất TRADE session — QuickFIX/n đang tự reconnect";
            failed = wasLoggedOn ? 0 : ++_failedReconnects;
        }

        if (wasLoggedOn)
        {
            Emit("WARN", "TRADE logged out — trades map B về MapNotFound, chờ reconnect + sync lại");
            Raise(CTraderTradeEventKind.LoggedOut, "TRADE logged out");
        }
        else if (failed == 1)
        {
            Emit("INFO", "TRADE reconnect chưa logon được — QuickFIX/n thử lại mỗi 2 s");
        }
    }

    private void OnMessage(int generation, CTraderSessionRole role, Message message)
    {
        if (role != CTraderSessionRole.Trade)
        {
            return;
        }

        var msgType = FixFieldReader.MsgType(message);
        switch (msgType)
        {
            case "y":
                HandleSecurityList(generation, message);
                return;

            case "AP":
                HandlePositionReport(generation, message);
                return;

            case "8":
                HandleExecutionReport(generation, message);
                return;

            case "3":
            case "j":
                Emit("WARN", $"TRADE nhận 35={msgType} text={FixFieldReader.String(message, 58) ?? "(không có 58)"}");
                return;

            case "5":
                Emit("INFO", $"TRADE nhận 35=5 text={FixFieldReader.String(message, 58) ?? "(không có 58)"}");
                return;
        }
    }

    private void HandleSecurityList(int generation, Message message)
    {
        int count;
        int symbolId;
        CTraderSymbolInfo? info = null;
        lock (_stateLock)
        {
            if (generation != _generation)
            {
                return;
            }

            count = _catalog.Apply(message);
            symbolId = _symbolId;
            if (_catalog.TryGet(symbolId, out var resolved))
            {
                info = resolved;
            }
        }

        if (info is null)
        {
            Emit("ERROR", $"TRADE SecurityList ({count} symbol) không có symbolId={symbolId} — trades map B MapNotFound");
            return;
        }

        Emit("INFO", $"TRADE symbolName={info.SymbolName} symbolId={info.SymbolId} digits={info.Digits} (SecurityList {count} symbol)");
    }

    private void HandlePositionReport(int generation, Message message)
    {
        if (!CTraderPositionReportParser.TryParse(message, out var report))
        {
            return;
        }

        bool changed;
        bool becameSynced;
        long version;
        int count;
        List<long> vanished = [];
        lock (_stateLock)
        {
            if (generation != _generation || _cache is null)
            {
                return;
            }

            var wasSynced = _cache.PositionsSynced;
            var before = _cache.Positions.Select(p => p.PositionId).ToHashSet();
            changed = _cache.ApplyPositionReport(report);
            if (changed)
            {
                var now = _cache.Positions.Select(p => p.PositionId).ToHashSet();
                vanished = before.Where(id => !now.Contains(id)).ToList();
            }
            becameSynced = !wasSynced && _cache.PositionsSynced;
            _windowApReports++;
            if (becameSynced)
            {
                _status = $"TRADE đang chạy — positions synced (count={_cache.Count})";
            }

            version = (long)_cache.Version;
            count = _cache.Count;
        }

        if (report.PosReqResult != CTraderPositionReport.ResultValid
            && report.PosReqResult != CTraderPositionReport.ResultNoPositions)
        {
            Emit("WARN", $"PositionReport 710={report.PosReqId} 728={report.PosReqResult} (không hợp lệ/bị từ chối) — giữ trạng thái sync hiện tại");
            return;
        }

        // Phase 6 câu 4 (ĐÃ QUYẾT): position mất qua AP mà không có fill đóng → KHÔNG bịa record history, chỉ cảnh báo.
        foreach (var positionId in vanished)
        {
            Emit("WARN", $"Position {positionId} biến mất qua PositionReport 710={report.PosReqId} mà không có fill đóng — history thiếu record này");
        }

        if (becameSynced)
        {
            Emit("INFO", $"PositionsSynced=true 710={report.PosReqId} 727={report.TotalNumPosReports} 728={report.PosReqResult} count={count} version={version}");
            Raise(CTraderTradeEventKind.PositionsSynced, $"count={count}");
        }
        else if (changed)
        {
            Emit("INFO", $"Reconciliation đổi tập position 710={report.PosReqId} count={count} version={version}");
        }
    }

    private void HandleExecutionReport(int generation, Message message)
    {
        bool changed;
        long version;
        int count;
        List<CTraderClosedPosition> closed;
        ulong historyVersion;
        lock (_stateLock)
        {
            if (generation != _generation || _cache is null)
            {
                return;
            }

            changed = _cache.ApplyExecutionReport(message);
            version = (long)_cache.Version;
            count = _cache.Count;
            closed = [.. _closedToLog];
            _closedToLog.Clear();
            historyVersion = _history.Version;
        }

        if (changed)
        {
            Emit("INFO",
                $"ExecutionReport fill 721={FixFieldReader.String(message, 721) ?? "-"} 54={FixFieldReader.String(message, 54) ?? "-"} " +
                $"32={FixFieldReader.String(message, 32) ?? "-"} 31={FixFieldReader.String(message, 31) ?? "-"} count={count} version={version}");
        }

        foreach (var c in closed)
        {
            var move = c.Position.IsBuy ? c.ClosePrice - c.Position.EntryPrice : c.Position.EntryPrice - c.ClosePrice;
            Emit("INFO",
                $"History record positionId={c.Position.PositionId} side={(c.Position.IsBuy ? "Buy" : "Sell")} " +
                $"open={c.Position.EntryPrice.ToString(CultureInfo.InvariantCulture)} close={c.ClosePrice.ToString(CultureInfo.InvariantCulture)} " +
                $"move={move.ToString(CultureInfo.InvariantCulture)} (Profit/Commission là số TÍNH LẠI, không phải số broker — R10) " +
                $"history_version={historyVersion}");
        }
    }

    private void SendPositionsRequest(ICTraderFixTransport transport, string posReqId, string reason)
    {
        try
        {
            var sent = transport.Send(CTraderSessionRole.Trade, CTraderMessageFactory.RequestForPositions(posReqId));
            Emit("INFO", $"RequestForPositions 710={posReqId} ({reason}) sent={sent}");
        }
        catch (Exception ex)
        {
            Emit("ERROR", $"RequestForPositions lỗi: {ex.Message}");
        }
    }

    // Phase 5 câu 2: mỗi lần một chuỗi mới. Gọi dưới _stateLock.
    private string NextPosReqId()
    {
        var id = "pos-" + _unixMs().ToString(CultureInfo.InvariantCulture);
        return string.Equals(id, _lastPosReqId, StringComparison.Ordinal) ? id + "-" + _generation : id;
    }

    private void SetStatus(string status)
    {
        lock (_stateLock)
        {
            _status = status;
        }
    }

    private void Emit(string level, string message)
    {
        var handler = LogLine;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(CTraderFixLogMasker.Apply($"[CTRADER][TRADE][{level}] {message}"));
        }
        catch
        {
            // Log không được làm hỏng session.
        }
    }

    private void Raise(CTraderTradeEventKind kind, string message)
    {
        try
        {
            EventRaised?.Invoke(new CTraderTradeSessionEvent(kind, message));
        }
        catch
        {
            // Subscriber lỗi không được làm hỏng session.
        }
    }
}
