using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.CTrader;
using TradeDesktop.Application.Services.PrimeXbt;

namespace TradeDesktop.Infrastructure.PrimeXbt;

// Phiên WebSocket `fws` của PrimeXBT cho sàn B (docs/plans/primexbt Phase 4 — CHỈ ĐỌC giá).
// Mô hình luồng soi gương CTraderQuoteSession:
//   - EnsureState/Read chạy trên luồng poll 50 ms; không chặn, không throw.
//   - Start/Stop chạy tuần tự trên chuỗi _lifecycle (nền).
//   - Callback transport chạy trên luồng mạng; mỗi transport gắn `generation`, sự kiện của transport cũ bị bỏ qua.
// Fail-closed: thiếu cấu hình/đăng nhập, chưa kết nối, heartbeat quá hạn, chưa resolve symbol, lệch digits, tài khoản bị
// khoá, chưa có giá ⇒ IsConnected=false và KHÔNG giữ giá cũ (R9).
public sealed class PrimeXbtFwsSession : IPrimeXbtQuoteSession, IPrimeXbtTradeSession, IDisposable
{
    public const string FwsUrl = "wss://api.primexbt.com/v2/fws/";
    public const int HeartbeatIntervalMs = 20_000;       // web app: request `time` mỗi 20 s
    public const int HeartbeatTimeoutMs = 16_000;        // web app: quá 16 s không trả ⇒ đóng socket
    public const int StatsWindowMs = 60_000;
    public const int StormWindowMs = 60_000;
    public const int StormThreshold = 5;
    public const int StormRetryCooldownMs = 5 * 60_000;
    public const int StormMaxRetries = 3;
    public const int DeadAlertIntervalMs = 15 * 60_000;
    public const int AuthRetryMs = 30_000;               // chưa đăng nhập: thử nạp lại token định kỳ
    public const int TokenCheckIntervalMs = 10 * 60_000;
    public static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromHours(24);
    public static readonly int[] ReconnectBackoffMs = [500, 1_000, 2_000, 4_000, 8_000, 10_000];
    // Phase 6: `report/orders2` lúc đồng bộ, ~0.5 s sau khi một vị thế biến mất, và định kỳ 10 s (6-A2: ≤ 1 lần / 5 s khi yên).
    public const int HistoryPollIntervalMs = 10_000;
    public const int HistoryAfterCloseDelayMs = 500;
    public const int HistoryMinGapMs = 2_000;
    public const int RefreshFailAlertIntervalMs = 60 * 60_000;
    public const int HistoryResponseTimeoutMs = 15_000;   // > chu kỳ poll: request chưa trả lời không bị gửi chồng

    private readonly Func<IPrimeXbtTransport> _transportFactory;
    private readonly IPrimeXbtTokenStore _tokenStore;
    private readonly Func<PrimeXbtSession, CancellationToken, Task<PrimeXbtSession?>> _refresh;
    private readonly Func<long> _tickCount;
    private readonly Func<DateTime> _utcNow;

    // --- desired state (_desiredLock) ---
    private readonly object _desiredLock = new();
    private bool _desiredActive;
    private PrimeXbtConfig? _desiredConfig;
    private string? _configProblem;
    private bool _disposed;
    private Task _lifecycle = Task.CompletedTask;

    // --- connection / book state (_stateLock) ---
    private readonly object _stateLock = new();
    private IPrimeXbtTransport? _transport;
    private PrimeXbtConfig? _activeConfig;
    private int _generation;
    private bool _connected;
    private long _connectedTick;
    private string _status = "Chưa bật";
    private int _rid;
    private int _markets2Rid;
    private PrimeXbtSymbolInfo? _symbol;
    private PrimeXbtTradeSettings? _tradeSettings;
    private bool _accountBlocked;
    private string _positionMode = string.Empty;
    private bool _accountChecked;
    private decimal? _bid;
    private decimal? _ask;
    private long _lastQuoteTick;
    private long _quoteSequence;
    private long _lastInboundTick;
    private long _lastHeartbeatTick;
    private long _heartbeatSentTick;
    private int _heartbeatRid;
    private bool _stale;
    private int _staleEvents;
    private int _heartbeatsAnswered;
    private long _heartbeatRttMax;
    private string? _authProblem;
    private long _reconnectDueTick;
    private int _backoffIndex;
    private readonly Queue<long> _disconnects = new();
    private bool _stormTripped;
    private long _stormTripTick;
    private int _stormRetries;
    private long _lastDeadAlertTick;
    private long _lastTokenCheckTick;
    private string? _mismatchReported;
    private readonly PrimeXbtPositionCache _positions;
    private bool _positionsProblemReported;
    private readonly PrimeXbtHistoryBook _history;
    private int _historyRid;
    private long _historySentTick;
    private long _lastHistoryRequestTick;
    private long _historyDueTick;
    private bool _historyLoaded;
    private bool _historyErrorReported;
    private int _historyRequests;
    private readonly Dictionary<int, PendingOrder> _pendingOrders = new();
    private DateTime? _activeJwtExpiresUtc;
    private bool _refreshFailing;
    private long _lastRefreshFailAlertTick;
    // Phase 8.2 / 8.3
    private static readonly HashSet<string> KnownActions = new(StringComparer.Ordinal)
    {
        "markets2", "fx/market", "metrics", "trade-settings", "positions", "time", "report/orders2", "market/detail",
        "orders/market/place", "positions/id/close", "trading/activity"
    };
    public const int QuoteDriftThreshold = 20;
    private readonly HashSet<string> _driftReported = new(StringComparer.Ordinal);
    private int _consecutiveBadQuotes;
    private bool? _marketOpen;
    private bool? _hmrActive;
    private int? _hmrLeverage;
    private bool _tradeSettingsLogged;

    // Phase 7: chờ ack 5 s (như cTrader OrderReportTimeoutMs); đối soát sau 3 s (vị thế hiện trên snapshot p95 1.7 s).
    public TimeSpan OrderAckTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReconcileDelay { get; set; } = TimeSpan.FromSeconds(3);

    private sealed record PendingOrder(TaskCompletionSource<PrimeXbtOrderOutcome> Tcs, PrimeXbtOrderPlan Plan, long SentTick);

    // --- read-side (chỉ luồng poll) ---
    private int _readGeneration = -1;
    private long _lastSequence;
    private long _prevQuoteTick;
    private decimal? _lastInterval;
    private decimal _latSum;
    private int _latCount;
    private decimal _latMax;
    private long _tpsSecond = -1;
    private int _tpsCount;
    private float? _tpsLast;
    private long _windowStartMs;
    private int _windowTicks;
    private int _windowDisconnectedReads;
    private int _windowWouldSkip;
    private readonly List<long> _windowAges = new();

    public PrimeXbtFwsSession(
        Func<IPrimeXbtTransport> transportFactory,
        IPrimeXbtTokenStore tokenStore,
        Func<PrimeXbtSession, CancellationToken, Task<PrimeXbtSession?>> refresh,
        Func<long>? tickCount = null,
        Func<DateTime>? utcNow = null)
    {
        _transportFactory = transportFactory;
        _tokenStore = tokenStore;
        _refresh = refresh;
        _tickCount = tickCount ?? (() => Environment.TickCount64);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _positions = new PrimeXbtPositionCache(_tickCount);
        _history = new PrimeXbtHistoryBook(_tickCount);
    }

    public static PrimeXbtFwsSession CreateDefault(IPrimeXbtTokenStore tokenStore)
        => new(() => new ClientWebSocketPrimeXbtTransport(), tokenStore, PrimeXbtAuthClient.RefreshAsync);

    public event Action<PrimeXbtSessionEvent>? EventRaised;
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

    public bool IsLoggedOn
    {
        get
        {
            lock (_stateLock)
            {
                return _connected && !_stale;
            }
        }
    }

    // Cho test: chờ chuỗi lifecycle hiện tại chạy xong.
    public Task WaitForLifecycleAsync()
    {
        lock (_desiredLock)
        {
            return _lifecycle;
        }
    }

    public void EnsureState(string platformB, PrimeXbtConfig config)
    {
        try
        {
            var isPrimeXbt = PrimeXbtRoutingRules.IsPrimeXbtPlatform(platformB);
            var normalized = (config ?? PrimeXbtConfig.Empty).Normalize();
            string? problem = null;
            if (isPrimeXbt)
            {
                var missing = normalized.GetMissingRequiredFields();
                if (missing.Count > 0)
                {
                    problem = "Cấu hình PrimeXBT thiếu: " + string.Join(", ", missing);
                }
            }

            var want = isPrimeXbt && problem is null;
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
                        Emit(normalized == PrimeXbtConfig.Empty ? "INFO" : "ERROR", problem + " — không mở kết nối PrimeXBT");
                    }
                }

                if (want == _desiredActive && Equals(target, _desiredConfig))
                {
                    return;
                }

                _desiredActive = want;
                _desiredConfig = target;
                ResetStorm();
                EnqueueReconcile();
            }
        }
        catch (Exception ex)
        {
            Emit("ERROR", "EnsureState lỗi: " + ex.Message);
        }
    }

    private void EnqueueReconcile()
    {
        lock (_desiredLock)
        {
            _lifecycle = _lifecycle.ContinueWith(_ => Reconcile(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void Reconcile()
    {
        try
        {
            bool want;
            PrimeXbtConfig? config;
            lock (_desiredLock)
            {
                want = _desiredActive && !_disposed;
                config = _desiredConfig;
            }

            IPrimeXbtTransport? current;
            PrimeXbtConfig? active;
            bool tripped;
            lock (_stateLock)
            {
                current = _transport;
                active = _activeConfig;
                tripped = _stormTripped;
            }

            if (current is not null && (!want || !Equals(config, active)))
            {
                StopTransport(want ? "cấu hình PrimeXBT đổi → khởi động lại" : "sàn B không còn là PrimeXBT / app dừng", raiseStopped: true);
                current = null;
            }

            if (want && config is not null && current is null && !tripped)
            {
                StartTransport(config);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi: " + ex.Message);
            Emit("ERROR", "Reconcile lỗi: " + ex.Message);
            Raise(PrimeXbtSessionEventKind.Error, ex.Message);
        }
    }

    private void StartTransport(PrimeXbtConfig config)
    {
        var session = LoadUsableSession();
        if (session is null)
        {
            return;
        }

        var transport = _transportFactory();
        int generation;
        lock (_stateLock)
        {
            generation = ++_generation;
            _quoteSequence = 0; // Read() reset _lastSequence theo generation — hai bộ đếm phải về 0 cùng nhau
            _transport = transport;
            _activeConfig = config;
            _connected = false;
            ClearMarketState();
            _authProblem = null;
            _reconnectDueTick = 0;
            _activeJwtExpiresUtc = session.JwtExpiresUtc;
            _status = "Đang kết nối PrimeXBT…";
        }

        transport.Connected += () => OnConnected(generation);
        transport.Disconnected += reason => OnDisconnected(generation, reason);
        transport.MessageReceived += text => OnMessage(generation, text);
        Emit("INFO", $"connecting account={config.AccountId} symbol={config.Symbol} jwtExp={session.JwtExpiresUtc:yyyy-MM-dd HH:mm}Z");
        Raise(PrimeXbtSessionEventKind.Connecting, config.AccountId);
        var uri = new Uri($"{FwsUrl}?accountId={Uri.EscapeDataString(config.AccountId.ToLowerInvariant())}&jwt={Uri.EscapeDataString(session.Jwt)}");
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PrimeXbtSession.FwsCookie] = session.ApiCookies[PrimeXbtSession.FwsCookie]
        };
        transport.Start(uri, cookies);
    }

    // Token: thiếu/hết hạn ⇒ thử refresh; vẫn không dùng được ⇒ fail-closed, nhắc đăng nhập, thử nạp lại sau 30 s
    // (user đăng nhập lại trong Config là tự nhận). Sắp hết hạn (< 24 h) ⇒ refresh trước khi kết nối.
    private PrimeXbtSession? LoadUsableSession()
    {
        var now = _utcNow();
        var session = _tokenStore.Load();
        if (session is not null && session.HasRequiredCookies &&
            (session.JwtExpiresUtc is not { } exp || exp - now < RefreshBeforeExpiry))
        {
            var refreshed = TryRefresh(session);
            if (refreshed is not null)
            {
                session = refreshed;
            }
        }

        if (session is not null && session.IsUsableAt(now))
        {
            return session;
        }

        var problem = session is null
            ? "Chưa đăng nhập PrimeXBT — mở Config → Đăng nhập"
            : "Phiên PrimeXBT hết hạn/thiếu cookie — mở Config → Đăng nhập lại";
        bool first;
        lock (_stateLock)
        {
            first = !string.Equals(_authProblem, problem, StringComparison.Ordinal);
            _authProblem = problem;
            _status = problem;
            _reconnectDueTick = _tickCount() + AuthRetryMs;
        }

        if (first)
        {
            Emit("ERROR", problem + " — sàn B disconnected");
            Raise(PrimeXbtSessionEventKind.AuthRequired, problem);
        }

        return null;
    }

    private PrimeXbtSession? TryRefresh(PrimeXbtSession session)
    {
        string failure;
        try
        {
            var refreshed = _refresh(session, CancellationToken.None).GetAwaiter().GetResult();
            if (refreshed is not null)
            {
                _tokenStore.Save(refreshed);
                lock (_stateLock)
                {
                    _refreshFailing = false;
                    _activeJwtExpiresUtc = refreshed.JwtExpiresUtc;
                }

                Emit("INFO", $"refresh token OK — jwtExp={refreshed.JwtExpiresUtc:yyyy-MM-dd HH:mm}Z");
                return refreshed;
            }

            failure = "refresh token thất bại";
        }
        catch (Exception ex)
        {
            failure = "refresh token lỗi: " + ex.GetType().Name;
        }

        // Phase 8.1: refresh chỉ chạy khi JWT còn < RefreshBeforeExpiry ⇒ thất bại là phải đăng nhập lại sớm. Chặn Open mới
        // (OpenBlockReason) + Telegram tối đa 1 lần / giờ; Close vẫn đi nếu socket còn.
        var now = _tickCount();
        bool alert;
        lock (_stateLock)
        {
            _refreshFailing = true;
            _activeJwtExpiresUtc ??= session.JwtExpiresUtc;
            alert = _lastRefreshFailAlertTick == 0 || now - _lastRefreshFailAlertTick >= RefreshFailAlertIntervalMs;
            if (alert)
            {
                _lastRefreshFailAlertTick = now;
            }
        }

        var left = session.JwtExpiresUtc is { } exp ? $"{Math.Max(0, (exp - _utcNow()).TotalHours):0.#} h" : "?";
        var message = $"{failure} — JWT còn {left}; CHẶN Open mới tới khi refresh / đăng nhập lại (Config → Đăng nhập)";
        Emit("WARN", message);
        if (alert)
        {
            Raise(PrimeXbtSessionEventKind.TokenRefreshFailed, message);
        }

        return null;
    }

    public string? OpenBlockReason
    {
        get
        {
            lock (_stateLock)
            {
                if (_activeJwtExpiresUtc is { } exp && exp <= _utcNow())
                {
                    return $"JWT PrimeXBT đã hết hạn lúc {exp:yyyy-MM-dd HH:mm}Z — đăng nhập lại";
                }

                return _refreshFailing
                    ? $"refresh JWT PrimeXBT đang thất bại (JWT hết hạn {(_activeJwtExpiresUtc is { } e ? e.ToString("yyyy-MM-dd HH:mm") + "Z" : "?")})"
                    : null;
            }
        }
    }

    private void StopTransport(string reason, bool raiseStopped)
    {
        IPrimeXbtTransport? transport;
        lock (_stateLock)
        {
            transport = _transport;
            _transport = null;
            _activeConfig = null;
            ++_generation; // tự đóng ⇒ Disconnected của transport này bị bỏ qua, không tính là mất phiên
            _quoteSequence = 0;
            _connected = false;
            ClearMarketState();
            if (!_stormTripped)
            {
                _status = "Đã dừng: " + reason; // ngắt mạch: giữ nguyên status "NGẮT MẠCH" cho UI
            }
        }

        FailPendingOrders("phiên dừng khi lệnh còn chờ ack");

        if (transport is null)
        {
            return;
        }

        try
        {
            transport.Stop();
        }
        catch (Exception ex)
        {
            Emit("WARN", "stop transport lỗi: " + ex.GetType().Name);
        }

        try
        {
            transport.Dispose();
        }
        catch
        {
            // best effort
        }

        Emit("INFO", $"stopped ({reason})");
        if (raiseStopped)
        {
            Raise(PrimeXbtSessionEventKind.Stopped, reason);
        }
    }

    private void OnConnected(int generation)
    {
        IPrimeXbtTransport? transport;
        PrimeXbtConfig? config;
        lock (_stateLock)
        {
            if (generation != _generation)
            {
                return;
            }

            var now = _tickCount();
            _connected = true;
            _connectedTick = now;
            _lastInboundTick = now;
            _lastHeartbeatTick = now;
            _heartbeatSentTick = 0;
            _stale = false;
            _status = "Đã kết nối — chờ danh sách symbol";
            transport = _transport;
            config = _activeConfig;
        }

        Emit("INFO", "connected");
        Raise(PrimeXbtSessionEventKind.Connected, "fws");
        if (transport is null || config is null)
        {
            return;
        }

        int markets2Rid;
        lock (_stateLock)
        {
            markets2Rid = _markets2Rid = ++_rid;
        }

        SendFrame(transport, PrimeXbtEnvelope.TypeSubscription, markets2Rid, "markets2", null);
        SendFrame(transport, PrimeXbtEnvelope.TypeSubscription, NextRid(), "metrics", null);
        SendFrame(transport, PrimeXbtEnvelope.TypeSubscription, NextRid(), "trade-settings", new JsonObject { ["symbol"] = config.Symbol });
        // Phase 5: RESPONSE trả snapshot ngay (kể cả tài khoản trống); sau đó EVENT snapshot đầy đủ ~1 s khi có vị thế.
        SendFrame(transport, PrimeXbtEnvelope.TypeSubscription, NextRid(), "positions", new JsonObject());
    }

    private void OnDisconnected(int generation, string reason)
    {
        bool wasConnected;
        long now;
        int backoff;
        lock (_stateLock)
        {
            if (generation != _generation)
            {
                return;
            }

            now = _tickCount();
            wasConnected = _connected;
            _connected = false;
            ClearMarketState();
            backoff = ReconnectBackoffMs[Math.Min(_backoffIndex, ReconnectBackoffMs.Length - 1)];
            _backoffIndex = Math.Min(_backoffIndex + 1, ReconnectBackoffMs.Length - 1);
            _reconnectDueTick = now + backoff;
            _status = $"Mất kết nối PrimeXBT — thử lại sau {backoff} ms";
        }

        // Transport chết không dùng lại: dọn trên lifecycle; Read() sẽ đặt lại Reconcile khi tới hạn backoff.
        lock (_desiredLock)
        {
            _lifecycle = _lifecycle.ContinueWith(_ => DropDeadTransport(generation), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        FailPendingOrders("socket rớt sau khi gửi");

        var safeReason = PrimeXbtLogMasker.Apply(reason);
        if (wasConnected)
        {
            Emit("WARN", $"disconnected ({safeReason}) — giá đã xoá, reconnect sau {backoff} ms");
            Raise(PrimeXbtSessionEventKind.Disconnected, safeReason);
            TrackDisconnectForStorm(now);
        }
        else
        {
            Emit("WARN", $"kết nối thất bại ({safeReason}) — thử lại sau {backoff} ms");
        }
    }

    private void DropDeadTransport(int generation)
    {
        IPrimeXbtTransport? transport = null;
        lock (_stateLock)
        {
            if (generation == _generation && _transport is not null && !_connected)
            {
                transport = _transport;
                _transport = null;
                _activeConfig = null;
            }
        }

        try
        {
            transport?.Dispose();
        }
        catch
        {
            // best effort
        }
    }

    private void OnMessage(int generation, string text)
    {
        if (!PrimeXbtEnvelope.TryParse(text, out var frame))
        {
            return;
        }

        var notes = new List<(string Level, string Message)>();
        var events = new List<PrimeXbtSessionEvent>();
        IPrimeXbtTransport? transport = null;
        var subscribeSymbolId = 0;
        (PendingOrder Order, PrimeXbtOrderOutcome Outcome)? orderAck = null;
        lock (_stateLock)
        {
            if (generation != _generation)
            {
                return;
            }

            if (frame.Type == PrimeXbtFrameType.Response && frame.Rid is { } orderRid &&
                _pendingOrders.Remove(orderRid, out var pendingOrder))
            {
                orderAck = (pendingOrder, BuildOrderOutcome(frame));
            }

            if (!KnownActions.Contains(frame.Action))
            {
                AddDrift(notes, events, "action:" + frame.Action, $"action lạ từ server: {frame.Action} ({frame.Type}) — bỏ qua");
            }

            var now = _tickCount();
            _lastInboundTick = now;
            if (_stale)
            {
                _stale = false;
                notes.Add(("INFO", "phản hồi trở lại — hết fail-closed, chờ giá mới"));
            }

            if (frame.Error is { } error)
            {
                notes.Add(("WARN", $"{frame.Action} lỗi {error.Code}: {error.Description}"));
            }

            switch (frame.Action)
            {
                case "markets2" when frame.Type == PrimeXbtFrameType.Response && frame.Rid == _markets2Rid && frame.Body is { } body:
                    var resolved = PrimeXbtSymbolResolver.Resolve(body, _activeConfig?.Symbol);
                    if (resolved.Status == PrimeXbtSymbolResolveStatus.Resolved)
                    {
                        _symbol = resolved.Info;
                        _backoffIndex = 0;
                        _status = $"Đang chạy — {_symbol!.Symbol} symbolId={_symbol.SymbolId} digits={_symbol.Digits}";
                        notes.Add(("INFO", $"symbol={_symbol.Symbol} symbolId={_symbol.SymbolId} digits={_symbol.Digits} marketOpen={_symbol.IsMarketOpen}"));
                        events.Add(new PrimeXbtSessionEvent(PrimeXbtSessionEventKind.SymbolResolved, _symbol.Symbol));
                        transport = _transport;
                        subscribeSymbolId = _symbol.SymbolId;
                    }
                    else
                    {
                        _status = $"Không resolve được symbol '{_activeConfig?.Symbol}' ({resolved.Status}) — sàn B disconnected";
                        notes.Add(("ERROR", _status));
                    }

                    break;

                case "fx/market" when frame.Body is { } body && _symbol is not null:
                    if (PrimeXbtQuoteParser.TryParse(body, _symbol.SymbolId, out var quote))
                    {
                        _bid = quote.Bid;
                        _ask = quote.Ask;
                        _lastQuoteTick = now;
                        _quoteSequence++;
                        _consecutiveBadQuotes = 0;
                    }
                    else if (IsForSymbol(body, _symbol.SymbolId) && ++_consecutiveBadQuotes == QuoteDriftThreshold)
                    {
                        // Giá đã fail-closed (không cập nhật ⇒ tuổi tick tăng ⇒ guard chặn); đây chỉ là cảnh báo nguyên nhân.
                        AddDrift(notes, events, "fx/market-shape", $"{QuoteDriftThreshold} frame giá liên tiếp của symbolId {_symbol.SymbolId} không đọc được a/b — có thể PrimeXBT đổi giao thức");
                    }

                    break;

                case "trade-settings" when frame.Body is { } body:
                    var settings = PrimeXbtTradeSettings.TryParse(body);
                    if (settings is null)
                    {
                        AddDrift(notes, events, "trade-settings-shape", "trade-settings thiếu min/step/max — planner sẽ fail-closed mọi lệnh");
                    }

                    _tradeSettings = settings ?? _tradeSettings;
                    if (body.ValueKind == JsonValueKind.Object)
                    {
                        _hmrLeverage = body.TryGetProperty("hmrLeverage", out var hl) && hl.ValueKind == JsonValueKind.Number ? hl.GetInt32() : _hmrLeverage;
                        if (!_tradeSettingsLogged && settings is not null)
                        {
                            _tradeSettingsLogged = true;
                            notes.Add(("INFO", $"trade-settings min={settings.MinOrderSize} step={settings.OrderStep} max={settings.MaxOrderSize} " +
                                               $"tradingHours=\"{ReadString(body, "tradingHours")}\" hmrPeriod=\"{ReadString(body, "hmrPeriod")}\" hmrLeverage={_hmrLeverage}"));
                        }
                    }

                    break;

                case "market/detail" when frame.Body is { ValueKind: JsonValueKind.Object } body:
                    ApplyMarketDetail(body, notes, events);
                    break;

                case "metrics" when frame.Body is { ValueKind: JsonValueKind.Object } body:
                    ApplyMetrics(body, notes);
                    break;

                case "positions" when frame.Body is { } body && _activeConfig is { } positionsConfig:
                    ApplyPositions(body, positionsConfig, notes, events);
                    break;

                case "report/orders2" when frame.Type == PrimeXbtFrameType.Response && frame.Rid is { } historyRid &&
                                           historyRid == _historyRid && _activeConfig is { } historyConfig:
                    _historyRid = 0;
                    if (frame.Error is not null || frame.Body is not { ValueKind: JsonValueKind.Object } historyBody)
                    {
                        if (!_historyErrorReported)
                        {
                            _historyErrorReported = true;
                            notes.Add(("WARN", "history: phản hồi không dùng được (lỗi/thiếu body) — History map B giữ trạng thái cũ, thử lại theo lịch"));
                        }

                        break;
                    }

                    _historyErrorReported = false;
                    var addedTrades = _history.ApplyAndReturnAdded(PrimeXbtOrderReportParser.Parse(historyBody), historyConfig.Symbol);
                    var added = addedTrades.Count;
                    var estimated = addedTrades.Count(c => c.ProfitIsEstimated);
                    if (!_historyLoaded)
                    {
                        _historyLoaded = true;
                        notes.Add(("INFO", $"history synced v={_history.Version} count={_history.Count} (+{added}, ước lượng {estimated})"));
                    }
                    else if (added > 0)
                    {
                        var detail = string.Join(", ", addedTrades.Select(c =>
                            $"{c.PositionId}:{c.PositionSide}:{c.Qty.ToString(CultureInfo.InvariantCulture)} " +
                            $"{(c.OpenPrice is { } op ? op.ToString(CultureInfo.InvariantCulture) : "?")}→{c.ClosePrice.ToString(CultureInfo.InvariantCulture)} " +
                            $"profit={c.Profit.ToString(CultureInfo.InvariantCulture)}{(c.ProfitIsEstimated ? "(rpl)" : string.Empty)} " +
                            $"rpl={(c.BrokerRpl?.ToString(CultureInfo.InvariantCulture) ?? "-")}"));
                        notes.Add(("INFO", $"history +{added} v={_history.Version} count={_history.Count} [{detail}]" +
                                           (estimated > 0 ? $" — {estimated} record profit ƯỚC LƯỢNG theo rpl (không biết giá mở)" : string.Empty)));
                    }

                    break;

                case "time" when frame.Rid is { } rid && rid == _heartbeatRid && _heartbeatSentTick > 0:
                    var rtt = now - _heartbeatSentTick;
                    _heartbeatSentTick = 0;
                    _heartbeatsAnswered++;
                    _heartbeatRttMax = Math.Max(_heartbeatRttMax, rtt);
                    break;
            }
        }

        orderAck?.Order.Tcs.TrySetResult(orderAck.Value.Outcome);

        if (transport is not null && subscribeSymbolId > 0)
        {
            SendFrame(transport, PrimeXbtEnvelope.TypeSubscription, NextRid(), "fx/market", new JsonObject { ["symbolId"] = subscribeSymbolId });
            // Phase 8.3: trạng thái thị trường / HMR (chỉ thông báo).
            // Khác fx/market: route này đòi `symId` (khớp frame web app trong fixtures/ws-session.json).
            SendFrame(transport, PrimeXbtEnvelope.TypeSubscription, NextRid(), "market/detail", new JsonObject { ["symId"] = subscribeSymbolId });
        }

        foreach (var (level, message) in notes)
        {
            Emit(level, message);
        }

        foreach (var e in events)
        {
            Raise(e.Kind, e.Message);
        }
    }

    // Gọi trong _stateLock. Phase 8.2: mỗi loại drift báo MỘT lần trong đời session (không spam).
    private void AddDrift(List<(string Level, string Message)> notes, List<PrimeXbtSessionEvent> events, string key, string message)
    {
        if (!_driftReported.Add(key))
        {
            return;
        }

        notes.Add(("WARN", "[DRIFT] " + message));
        events.Add(new PrimeXbtSessionEvent(PrimeXbtSessionEventKind.ProtocolDrift, message));
    }

    private static bool IsForSymbol(JsonElement body, int symbolId)
        => body.ValueKind == JsonValueKind.Object && body.TryGetProperty("symId", out var id) &&
           id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var value) && value == symbolId;

    private static string ReadString(JsonElement body, string name)
        => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string FormatUnixLocal(JsonElement body, string name)
        => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "?";

    // Gọi trong _stateLock. Phase 8.3 — CHỈ thông báo; không chặn/đóng gì (Rule E). Thị trường đóng ⇒ không có tick ⇒ guard
    // latency B tự chặn như mọi lúc tick cũ.
    private void ApplyMarketDetail(JsonElement body, List<(string Level, string Message)> notes, List<PrimeXbtSessionEvent> events)
    {
        bool? open = body.TryGetProperty("isMarketOpen", out var mo) && mo.ValueKind is JsonValueKind.True or JsonValueKind.False ? mo.GetBoolean() : null;
        bool? hmr = body.TryGetProperty("isHmrActive", out var ha) && ha.ValueKind is JsonValueKind.True or JsonValueKind.False ? ha.GetBoolean() : null;
        var nextOpen = FormatUnixLocal(body, "nextOpenTime");
        var nextClose = FormatUnixLocal(body, "nextCloseTime");

        if (_marketOpen is null && _hmrActive is null)
        {
            notes.Add(("INFO", $"market open={open?.ToString().ToLowerInvariant() ?? "?"} hmr={hmr?.ToString().ToLowerInvariant() ?? "?"} nextOpen={nextOpen} nextClose={nextClose}"));
        }

        if (open is { } isOpen && _marketOpen is { } wasOpen && isOpen != wasOpen)
        {
            var message = isOpen ? "thị trường XAU/USD đã MỞ lại" : $"thị trường XAU/USD ĐÓNG — mở lại lúc {nextOpen} (không có tick ⇒ guard latency B tự chặn)";
            notes.Add((isOpen ? "INFO" : "WARN", message));
            events.Add(new PrimeXbtSessionEvent(PrimeXbtSessionEventKind.MarketNotice, message));
        }

        if (hmr is { } isHmr && _hmrActive is { } wasHmr && isHmr != wasHmr)
        {
            var message = isHmr
                ? $"VÀO khung HMR: đòn bẩy còn {_hmrLeverage?.ToString(CultureInfo.InvariantCulture) ?? "?"} (margin tăng) — app KHÔNG tự chặn/đóng"
                : "HẾT khung HMR";
            notes.Add((isHmr ? "WARN" : "INFO", message));
            events.Add(new PrimeXbtSessionEvent(PrimeXbtSessionEventKind.MarketNotice, message));
        }

        _marketOpen = open ?? _marketOpen;
        _hmrActive = hmr ?? _hmrActive;
    }

    // Gọi trong _stateLock.
    private void ApplyMetrics(JsonElement body, List<(string Level, string Message)> notes)
    {
        var blocked = body.TryGetProperty("blocked", out var b) && b.ValueKind == JsonValueKind.True;
        var closed = body.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True;
        _accountBlocked = blocked || closed;
        _positionMode = body.TryGetProperty("positionMode", out var pm) && pm.ValueKind == JsonValueKind.String ? pm.GetString() ?? string.Empty : _positionMode;
        if (_accountChecked)
        {
            return;
        }

        _accountChecked = true;
        var demo = body.TryGetProperty("demo", out var d) && d.ValueKind == JsonValueKind.True;
        notes.Add(("INFO", $"account demo={demo.ToString().ToLowerInvariant()} positionMode={_positionMode} blocked={_accountBlocked.ToString().ToLowerInvariant()}"));
        if (_activeConfig is { } config && config.IsDemoAccount != demo)
        {
            notes.Add(("WARN", $"Account {config.AccountId} cấu hình là {(config.IsDemoAccount ? "demo" : "thật")} nhưng server báo demo={demo.ToString().ToLowerInvariant()}"));
        }

        if (!string.Equals(_positionMode, "HEDGE", StringComparison.Ordinal))
        {
            notes.Add(("WARN", $"positionMode={_positionMode} — app cần HEDGE (Phase 5 sẽ fail-closed vị thế)"));
        }
    }

    // Gọi trong _stateLock. Log chỉ khi đổi trạng thái/nội dung (snapshot về ~1 s/lần khi có vị thế).
    private void ApplyPositions(
        JsonElement body,
        PrimeXbtConfig config,
        List<(string Level, string Message)> notes,
        List<PrimeXbtSessionEvent> events)
    {
        var wasSynced = _positions.Synced;
        var idsBefore = _positions.Positions.Select(p => p.Id).ToHashSet();
        var snapshot = PrimeXbtPositionsParser.Parse(body, config.Symbol);
        if (!snapshot.IsValid)
        {
            _positions.Apply(snapshot);
            if (!_positionsProblemReported)
            {
                _positionsProblemReported = true;
                notes.Add(("ERROR", $"snapshot positions không hợp lệ ({snapshot.Error}) — Trades map B fail-closed tới snapshot hợp lệ kế tiếp"));
            }

            return;
        }

        var changed = _positions.Apply(snapshot);
        if (!snapshot.IsHedgeMode)
        {
            if (!_positionsProblemReported)
            {
                _positionsProblemReported = true;
                var message = $"positionMode={snapshot.PositionMode} — app chỉ chạy HEDGE, Trades map B fail-closed";
                notes.Add(("ERROR", message));
                events.Add(new PrimeXbtSessionEvent(PrimeXbtSessionEventKind.PositionModeInvalid, message));
            }

            return;
        }

        if (_positionsProblemReported)
        {
            _positionsProblemReported = false;
            notes.Add(("INFO", "snapshot positions hợp lệ trở lại"));
        }

        // Phase 6: nhớ giá mở cho history; đồng bộ lần đầu / có vị thế biến mất ⇒ hẹn lấy `report/orders2`.
        _history.RememberOpen(_positions.Positions);
        var now = _tickCount();
        if (!wasSynced)
        {
            _historyDueTick = now;
        }
        else if (changed && idsBefore.Any(id => _positions.TryGet(id) is null))
        {
            _historyDueTick = now + HistoryAfterCloseDelayMs;
        }

        if (!wasSynced || changed)
        {
            var list = string.Join(", ", _positions.Positions.OrderBy(p => p.Id)
                .Select(p => $"{p.Id}:{p.Side}:{p.Qty.ToString(CultureInfo.InvariantCulture)}@{p.OpenPrice.ToString(CultureInfo.InvariantCulture)}"));
            notes.Add(("INFO", $"positions {(wasSynced ? "changed" : "synced")} v={_positions.Version} count={_positions.Count} [{list}]"));
        }
    }

    public SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName)
    {
        try
        {
            bool desired;
            lock (_desiredLock)
            {
                desired = _desiredActive && !_disposed;
            }

            lock (_stateLock)
            {
                if (!desired || !_connected || _stale || _authProblem is not null || _symbol is null ||
                    _activeConfig is null || !_positions.Synced ||
                    !string.Equals(_positions.PositionMode, "HEDGE", StringComparison.Ordinal))
                {
                    return SharedMapReadResult<TradeSharedRecord>.MapNotFound(mapName);
                }

                var records = _positions.ToTradeRecords(_symbol.Symbol, _activeConfig.ContractSizeB);
                return SharedMapReadResult<TradeSharedRecord>.Success(_positions.Version, records, records.Count, connected: 1);
            }
        }
        catch (Exception ex)
        {
            Emit("ERROR", "ReadTrades lỗi: " + ex.Message);
            return SharedMapReadResult<TradeSharedRecord>.MapNotFound(mapName);
        }
    }

    public SharedMapReadResult<HistorySharedRecord> ReadHistory(string mapName)
    {
        try
        {
            bool desired;
            lock (_desiredLock)
            {
                desired = _desiredActive && !_disposed;
            }

            lock (_stateLock)
            {
                if (!desired || !_connected || _stale || _authProblem is not null || _symbol is null ||
                    _activeConfig is null || !_positions.Synced || !_historyLoaded ||
                    !string.Equals(_positions.PositionMode, "HEDGE", StringComparison.Ordinal))
                {
                    return SharedMapReadResult<HistorySharedRecord>.MapNotFound(mapName);
                }

                var records = _history.ToHistoryRecords(_symbol.Symbol, _activeConfig.ContractSizeB);
                return SharedMapReadResult<HistorySharedRecord>.Success(_history.Version, records, records.Count, connected: 1);
            }
        }
        catch (Exception ex)
        {
            Emit("ERROR", "ReadHistory lỗi: " + ex.Message);
            return SharedMapReadResult<HistorySharedRecord>.MapNotFound(mapName);
        }
    }

    // Gọi từ luồng poll (Read). Một request treo tối đa HistoryResponseTimeoutMs; hai request cách nhau ≥ HistoryMinGapMs.
    private void CheckHistoryDue(long now)
    {
        IPrimeXbtTransport? transport = null;
        int rid;
        lock (_stateLock)
        {
            if (!_connected || _stale || _transport is null || _symbol is null || !_positions.Synced)
            {
                return;
            }

            if (_historyRid != 0 && now - _historySentTick < HistoryResponseTimeoutMs)
            {
                return;
            }

            var due = _historyDueTick > 0 ? _historyDueTick : _lastHistoryRequestTick + HistoryPollIntervalMs;
            if (now < due || now - _lastHistoryRequestTick < HistoryMinGapMs && _lastHistoryRequestTick > 0)
            {
                return;
            }

            rid = _historyRid = ++_rid;
            _historySentTick = now;
            _lastHistoryRequestTick = now;
            _historyDueTick = 0;
            _historyRequests++;
            transport = _transport;
        }

        SendFrame(transport, PrimeXbtEnvelope.TypeRequest, rid, "report/orders2", new JsonObject
        {
            ["offset"] = 0,
            ["limit"] = 50,
            ["protective"] = "",
            ["orderBy"] = "id",
            ["orderDir"] = "desc"
        });
    }

    public PrimeXbtTradeSettings? CurrentTradeSettings
    {
        get
        {
            lock (_stateLock)
            {
                return _tradeSettings;
            }
        }
    }

    // Rule E: ĐƯỜNG DUY NHẤT gửi lệnh ra PrimeXBT. Session KHÔNG tự gọi hàm này ở bất kỳ nhánh nào (không flatten, không
    // retry, không reconcile bằng lệnh). Không gửi lại sau timeout — lệnh có thể đã khớp (Phase 0 Q5).
    public async Task<PrimeXbtOrderOutcome> SendOrderAsync(PrimeXbtOrderPlan plan, CancellationToken cancellationToken = default)
    {
        if (plan.Action is not (PrimeXbtOrderPlanner.ActionMarketPlace or PrimeXbtOrderPlanner.ActionClosePosition))
        {
            Emit("ERROR", $"[ORDER] từ chối route không được phép: {plan.Action}");
            return new PrimeXbtOrderOutcome(false, $"Route không được phép: {plan.Action}");
        }

        var tcs = new TaskCompletionSource<PrimeXbtOrderOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        IPrimeXbtTransport transport;
        int rid;
        HashSet<long> idsBefore;
        long sentTick;
        lock (_stateLock)
        {
            if (!_connected || _stale || _transport is null || _symbol is null)
            {
                return new PrimeXbtOrderOutcome(false, PrimeXbtOrderOutcome.NotConnected);
            }

            rid = ++_rid;
            transport = _transport;
            idsBefore = _positions.Positions.Select(p => p.Id).ToHashSet();
            sentTick = _tickCount();
            _pendingOrders[rid] = new PendingOrder(tcs, plan, sentTick);
        }

        var body = plan.Body.ToJsonString();
        bool sent;
        try
        {
            sent = transport.Send(PrimeXbtEnvelope.Build(PrimeXbtEnvelope.TypeRequest, rid, plan.Action, plan.Body));
        }
        catch
        {
            sent = false;
        }

        if (!sent)
        {
            lock (_stateLock)
            {
                _pendingOrders.Remove(rid);
            }

            Emit("WARN", $"[ORDER] không gửi được rid={rid} {plan.Action} {body} (socket chưa sẵn sàng)");
            return new PrimeXbtOrderOutcome(false, PrimeXbtOrderOutcome.NotConnected);
        }

        Emit("INFO", $"[ORDER] SEND rid={rid} {plan.Action} {body}");
        var timeout = Task.Delay(OrderAckTimeout, CancellationToken.None);
        var winner = await Task.WhenAny(tcs.Task, timeout).ConfigureAwait(false);
        PrimeXbtOrderOutcome outcome;
        if (winner == tcs.Task)
        {
            outcome = await tcs.Task.ConfigureAwait(false);
        }
        else
        {
            lock (_stateLock)
            {
                _pendingOrders.Remove(rid);
            }

            outcome = new PrimeXbtOrderOutcome(false, PrimeXbtOrderOutcome.TimeoutUncertain + $" (không có ack sau {(int)OrderAckTimeout.TotalMilliseconds} ms)", IsUncertain: true);
        }

        var elapsed = _tickCount() - sentTick;
        if (outcome.IsUncertain)
        {
            var message = $"[ORDER] {outcome.Detail} rid={rid} {plan.Action} {body} — KHÔNG gửi lại; đối soát sau {(int)ReconcileDelay.TotalMilliseconds} ms";
            Emit("WARN", message);
            Raise(PrimeXbtSessionEventKind.OrderUncertain, message);
            StartReconcile(plan, idsBefore, rid);
        }
        else
        {
            Emit(outcome.Success ? "INFO" : "WARN", $"[ORDER] {(outcome.Success ? "ACK" : "REJECT")} rid={rid} {plan.Action} {elapsed} ms — {outcome.Detail}");
        }

        return outcome;
    }

    // Gọi trong _stateLock.
    private static PrimeXbtOrderOutcome BuildOrderOutcome(PrimeXbtFrame frame)
    {
        if (frame.Error is { } error)
        {
            return new PrimeXbtOrderOutcome(false, $"{error.Code}: {error.Description}");
        }

        long? orderId = frame.Body is { ValueKind: JsonValueKind.Object } body &&
                        body.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number &&
                        id.TryGetInt64(out var value)
            ? value
            : null;
        return new PrimeXbtOrderOutcome(true, orderId is { } o ? $"ack orderId={o}" : "ack", orderId);
    }

    // Rớt/dừng socket khi lệnh còn chờ ack ⇒ không biết lệnh có khớp không (Q5) ⇒ uncertain, KHÔNG gửi lại.
    private void FailPendingOrders(string reason)
    {
        List<PendingOrder> pending;
        lock (_stateLock)
        {
            if (_pendingOrders.Count == 0)
            {
                return;
            }

            pending = _pendingOrders.Values.ToList();
            _pendingOrders.Clear();
        }

        foreach (var order in pending)
        {
            order.Tcs.TrySetResult(new PrimeXbtOrderOutcome(false, $"{PrimeXbtOrderOutcome.TimeoutUncertain} ({reason})", IsUncertain: true));
        }
    }

    // Chỉ BÁO CÁO: đối chiếu snapshot vị thế sau ReconcileDelay với trạng thái lúc gửi. Router tự xử lý theo đường
    // partial-open/rollback/pending-close sẵn có (ticket khớp vẫn được phát hiện qua Trades map).
    private void StartReconcile(PrimeXbtOrderPlan plan, HashSet<long> idsBefore, int rid)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ReconcileDelay).ConfigureAwait(false);
                var conclusion = ConcludeReconcile(plan, idsBefore);
                var message = $"[ORDER] đối soát rid={rid} {plan.Action} {plan.Body.ToJsonString()} ⇒ {conclusion}";
                Emit(conclusion.StartsWith("UNKNOWN", StringComparison.Ordinal) ? "ERROR" : "WARN", message);
                Raise(PrimeXbtSessionEventKind.OrderReconciled, message);
            }
            catch (Exception ex)
            {
                Emit("ERROR", "[ORDER] đối soát lỗi: " + ex.Message);
            }
        });
    }

    private string ConcludeReconcile(PrimeXbtOrderPlan plan, HashSet<long> idsBefore)
    {
        lock (_stateLock)
        {
            if (!_connected || !_positions.Synced)
            {
                return "UNKNOWN (chưa có snapshot vị thế của kết nối hiện tại — kiểm tay trên web)";
            }

            if (plan.Action == PrimeXbtOrderPlanner.ActionClosePosition)
            {
                var positionId = plan.Body["positionId"]!.GetValue<long>();
                return _positions.TryGet(positionId) is null
                    ? $"CLOSED (sub {positionId} không còn mở)"
                    : $"NOT_CLOSED (sub {positionId} vẫn mở)";
            }

            var side = string.Equals(plan.Body["side"]!.GetValue<string>(), "BUY", StringComparison.Ordinal) ? PrimeXbtSide.Buy : PrimeXbtSide.Sell;
            var qty = plan.Body["qty"]!.GetValue<decimal>();
            var candidates = _positions.Positions.Where(p => !idsBefore.Contains(p.Id) && p.Side == side && p.Qty == qty).ToList();
            return candidates.Count switch
            {
                1 => $"FILLED (sub {candidates[0].Id}) — lệnh ĐÃ khớp dù không có ack",
                0 => "NOT_PLACED (không thấy vị thế mới khớp chiều/qty)",
                _ => $"UNKNOWN ({candidates.Count} vị thế mới cùng chiều/qty — không đoán)"
            };
        }
    }

    public (PrimeXbtSide Side, decimal Qty)? TryGetOpenPosition(long positionId)
    {
        lock (_stateLock)
        {
            return _positions.Synced && _positions.TryGet(positionId) is { } p ? (p.Side, p.Qty) : null;
        }
    }

    public ExchangeMetrics Read(ExchangeMetrics exchangeA, int point, int confirmLatencyMs)
    {
        try
        {
            bool desired;
            string? problem;
            lock (_desiredLock)
            {
                desired = _desiredActive && !_disposed;
                problem = _configProblem;
            }

            var nowTick = _tickCount();
            if (desired)
            {
                CheckHeartbeat(nowTick);
                CheckReconnectDue(nowTick);
                CheckStormRecovery(nowTick);
                CheckTokenRefreshDue(nowTick);
                CheckHistoryDue(nowTick);
            }

            int generation;
            bool connected;
            bool stale;
            bool blocked;
            string? authProblem;
            PrimeXbtSymbolInfo? symbol;
            decimal? bid;
            decimal? ask;
            long lastQuoteTick;
            long sequence;
            lock (_stateLock)
            {
                generation = _generation;
                connected = _connected;
                stale = _stale;
                blocked = _accountBlocked;
                authProblem = _authProblem;
                symbol = _symbol;
                bid = _bid;
                ask = _ask;
                lastQuoteTick = _lastQuoteTick;
                sequence = _quoteSequence;
            }

            var now = _tickCount();
            if (generation != _readGeneration)
            {
                ResetReadState(generation);
            }

            var newTicks = (int)Math.Max(0, sequence - _lastSequence);
            _lastSequence = sequence;

            string? reason = null;
            if (!desired)
            {
                reason = problem ?? "PrimeXBT chưa bật";
            }
            else if (authProblem is not null)
            {
                reason = authProblem;
            }
            else if (!connected)
            {
                reason = "PrimeXBT chưa kết nối";
            }
            else if (stale)
            {
                reason = "PrimeXBT không trả heartbeat — fail-closed";
            }
            else if (symbol is null)
            {
                reason = "Chưa resolve được symbol PrimeXBT";
            }
            else if (blocked)
            {
                reason = "Tài khoản PrimeXBT bị khoá/đóng";
            }
            else
            {
                var check = PointDigitsConsistencyChecker.Check(point, symbol.Digits);
                if (!check.IsConsistent)
                {
                    reason = "Lệch digits: " + check.Message;
                    ReportDigitsMismatch(symbol, check.Message);
                }
                else
                {
                    if (_mismatchReported is not null)
                    {
                        _mismatchReported = null;
                        Emit("INFO", "Hết lệch digits: " + check.Message);
                    }

                    if (bid is not > 0m || ask is not > 0m)
                    {
                        reason = "Chưa có giá PrimeXBT";
                    }
                }
            }

            if (reason is not null)
            {
                if (desired)
                {
                    RecordWindow(now, connected: false, 0, newTicks, confirmLatencyMs, exchangeA, null, null, point);
                }

                return Disconnected(symbol?.Symbol, reason);
            }

            var age = Math.Max(0, now - lastQuoteTick);
            var tps = UpdateTps(now, newTicks);
            if (newTicks > 0)
            {
                if (_prevQuoteTick > 0)
                {
                    var interval = Math.Max(0, lastQuoteTick - _prevQuoteTick);
                    _lastInterval = interval;
                    _latSum += interval;
                    _latCount++;
                    if (_latCount == 1 || interval > _latMax)
                    {
                        _latMax = interval;
                    }
                }

                _prevQuoteTick = lastQuoteTick;
            }

            RecordWindow(now, connected: true, age, newTicks, confirmLatencyMs, exchangeA, bid, ask, point);
            return new ExchangeMetrics(
                Symbol: symbol!.Symbol,
                Bid: bid,
                Ask: ask,
                Spread: ask - bid,
                LatencyMs: age,
                Tps: tps,
                Time: DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                MaxLatMs: _latCount > 0 ? _latMax : null,
                AvgLatMs: _latCount > 0 ? _latSum / _latCount : null,
                IsConnected: true,
                Error: null,
                TickIntervalMs: _lastInterval);
        }
        catch (Exception ex)
        {
            return Disconnected(null, "PrimeXBT Read lỗi: " + ex.Message);
        }
    }

    private void CheckHeartbeat(long now)
    {
        IPrimeXbtTransport? transport = null;
        IPrimeXbtTransport? toStop = null;
        int rid = 0;
        string? warn = null;
        lock (_stateLock)
        {
            if (!_connected || _transport is null)
            {
                return;
            }

            if (_heartbeatSentTick > 0 && now - _heartbeatSentTick >= HeartbeatTimeoutMs)
            {
                _stale = true;
                _staleEvents++;
                _bid = null;
                _ask = null;
                _heartbeatSentTick = 0;
                _status = "PrimeXBT không trả heartbeat — đóng socket, reconnect";
                warn = $"heartbeat quá {HeartbeatTimeoutMs} ms không trả — giá đã xoá, đóng socket để reconnect";
                toStop = _transport;
            }
            else if (_heartbeatSentTick == 0 && now - _lastHeartbeatTick >= HeartbeatIntervalMs)
            {
                rid = _heartbeatRid = ++_rid;
                _heartbeatSentTick = now;
                _lastHeartbeatTick = now;
                transport = _transport;
            }
        }

        if (warn is not null)
        {
            Emit("WARN", warn);
            try
            {
                toStop!.Stop(); // ⇒ Disconnected ⇒ backoff ⇒ reconnect
            }
            catch
            {
                // best effort
            }

            return;
        }

        if (transport is not null)
        {
            SendFrame(transport, PrimeXbtEnvelope.TypeRequest, rid, "time", null);
        }
    }

    private void CheckReconnectDue(long now)
    {
        lock (_stateLock)
        {
            if (_transport is not null || _stormTripped || _reconnectDueTick == 0 || now < _reconnectDueTick)
            {
                return;
            }

            _reconnectDueTick = 0;
        }

        EnqueueReconcile();
    }

    private void CheckTokenRefreshDue(long now)
    {
        lock (_stateLock)
        {
            if (!_connected || now - _lastTokenCheckTick < TokenCheckIntervalMs)
            {
                return;
            }

            _lastTokenCheckTick = now;
        }

        lock (_desiredLock)
        {
            _lifecycle = _lifecycle.ContinueWith(_ => RefreshIfNearExpiry(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    // Refresh khi đang kết nối không làm rớt socket (Phase 0: socket cũ sống tiếp); JWT mới dùng cho lần kết nối sau.
    private void RefreshIfNearExpiry()
    {
        var session = _tokenStore.Load();
        if (session?.JwtExpiresUtc is { } exp && exp - _utcNow() < RefreshBeforeExpiry && session.HasRequiredCookies)
        {
            TryRefresh(session);
        }
        else if (session?.JwtExpiresUtc is { } fresh && fresh - _utcNow() >= RefreshBeforeExpiry)
        {
            // Phase 8.1: người dùng đã đăng nhập lại (Config) ⇒ kho có JWT mới ⇒ nhả chặn Open.
            bool released;
            lock (_stateLock)
            {
                released = _refreshFailing;
                _refreshFailing = false;
                _activeJwtExpiresUtc = fresh;
            }

            if (released)
            {
                Emit("INFO", $"đã có JWT mới (hết hạn {fresh:yyyy-MM-dd HH:mm}Z) — bỏ chặn Open");
            }
        }
    }

    private void TrackDisconnectForStorm(long now)
    {
        string? message = null;
        lock (_stateLock)
        {
            if (_stormTripped)
            {
                return;
            }

            _disconnects.Enqueue(now);
            while (_disconnects.Count > 0 && now - _disconnects.Peek() > StormWindowMs)
            {
                _disconnects.Dequeue();
            }

            if (_disconnects.Count <= StormThreshold)
            {
                return;
            }

            _stormTripped = true;
            _stormTripTick = now;
            _lastDeadAlertTick = now;
            var retriesLeft = Math.Max(0, StormMaxRetries - _stormRetries);
            _status = $"NGẮT MẠCH: PrimeXBT mất kết nối {_disconnects.Count} lần trong 60 s — đã dừng, " +
                      (retriesLeft > 0 ? "tự thử lại sau 5 phút" : "cần bật lại tay");
            message = $"mất kết nối {_disconnects.Count} lần trong 60 s — NGẮT MẠCH: dừng session (còn {retriesLeft} lần tự thử)";
        }

        Emit("ERROR", message);
        Raise(PrimeXbtSessionEventKind.ReconnectStorm, message);
        lock (_desiredLock)
        {
            _lifecycle = _lifecycle.ContinueWith(_ => StopTransport("ngắt mạch: quá nhiều lần mất kết nối trong một phút", raiseStopped: false), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void ResetStorm()
    {
        lock (_stateLock)
        {
            _stormTripped = false;
            _disconnects.Clear();
            _stormRetries = 0;
            _backoffIndex = 0;
            _reconnectDueTick = 0;
        }
    }

    private void CheckStormRecovery(long now)
    {
        string? log = null;
        string? deadAlert = null;
        var retry = false;
        lock (_stateLock)
        {
            if (!_stormTripped)
            {
                if (_connected && _connectedTick > 0 && now - _connectedTick >= StormRetryCooldownMs)
                {
                    _stormRetries = 0;
                }

                return;
            }

            if (_stormRetries < StormMaxRetries && now - _stormTripTick >= StormRetryCooldownMs)
            {
                _stormTripped = false;
                _disconnects.Clear();
                _stormRetries++;
                _backoffIndex = 0;
                retry = true;
                log = $"thử nối lại sau ngắt mạch (lần {_stormRetries}/{StormMaxRetries})";
            }
            else if (_stormRetries >= StormMaxRetries && now - _lastDeadAlertTick >= DeadAlertIntervalMs)
            {
                _lastDeadAlertTick = now;
                deadAlert = $"PrimeXBT vẫn NGẮT MẠCH sau {StormMaxRetries} lần thử — sàn B fail-closed, cần can thiệp tay.";
            }
        }

        if (log is not null)
        {
            Emit("WARN", log);
        }

        if (deadAlert is not null)
        {
            Emit("ERROR", deadAlert);
            Raise(PrimeXbtSessionEventKind.ReconnectStorm, deadAlert);
        }

        if (retry)
        {
            EnqueueReconcile();
        }
    }

    private void ReportDigitsMismatch(PrimeXbtSymbolInfo symbol, string message)
    {
        var key = symbol.Symbol + "|" + message;
        if (string.Equals(_mismatchReported, key, StringComparison.Ordinal))
        {
            return;
        }

        _mismatchReported = key;
        SetStatus("FAIL-CLOSED lệch digits: " + message);
        Emit("ERROR", $"PRIMEXBT_DIGITS_MISMATCH symbol={symbol.Symbol} {message} — sàn B disconnected");
        Raise(PrimeXbtSessionEventKind.DigitsMismatch, $"{symbol.Symbol}: {message}");
    }

    // Gọi trong _stateLock.
    private void ClearMarketState()
    {
        _symbol = null;
        _bid = null;
        _ask = null;
        _lastQuoteTick = 0;
        _tradeSettings = null;
        _accountBlocked = false;
        _accountChecked = false;
        _positionMode = string.Empty;
        _heartbeatSentTick = 0;
        _stale = false;
        _markets2Rid = 0;
        // P4: rớt/dừng ⇒ Trades map B về MapNotFound ngay; nội dung giữ lại để stamp lần-đầu-thấy không bị reset.
        _positions.MarkUnsynced();
        _positionsProblemReported = false;
        // Phase 6: History map B cũng chờ `report/orders2` của kết nối mới (record cũ giữ lại, dedupe theo order id).
        _historyLoaded = false;
        _historyRid = 0;
        _historyDueTick = 0;
        // Phase 8.2 / 8.3: trạng thái của kết nối hiện tại.
        _consecutiveBadQuotes = 0;
        _marketOpen = null;
        _hmrActive = null;
        _tradeSettingsLogged = false;
    }

    private void ResetReadState(int generation)
    {
        _readGeneration = generation;
        _lastSequence = 0;
        _prevQuoteTick = 0;
        _lastInterval = null;
        _latSum = 0;
        _latCount = 0;
        _latMax = 0;
        _tpsSecond = -1;
        _tpsCount = 0;
        _tpsLast = null;
    }

    private float? UpdateTps(long nowMs, int newTicks)
    {
        if (newTicks <= 0)
        {
            return _tpsLast;
        }

        var second = nowMs / 1000;
        if (_tpsSecond < 0)
        {
            _tpsSecond = second;
            _tpsCount = newTicks;
            _tpsLast = _tpsCount;
        }
        else if (second == _tpsSecond)
        {
            _tpsCount += newTicks;
            _tpsLast = _tpsCount;
        }
        else
        {
            _tpsLast = _tpsCount;
            _tpsSecond = second;
            _tpsCount = newTicks;
        }

        return _tpsLast;
    }

    private void RecordWindow(long now, bool connected, long ageMs, int newTicks, int confirmLatencyMs,
        ExchangeMetrics exchangeA, decimal? bidB, decimal? askB, int point)
    {
        if (_windowStartMs == 0)
        {
            _windowStartMs = now;
        }

        _windowTicks += newTicks;
        if (connected)
        {
            _windowAges.Add(ageMs);
            if (confirmLatencyMs > 0 && ageMs > confirmLatencyMs)
            {
                _windowWouldSkip++;
            }
        }
        else
        {
            _windowDisconnectedReads++;
        }

        if (now - _windowStartMs < StatsWindowMs)
        {
            return;
        }

        int staleEvents;
        int heartbeats;
        long rttMax;
        int historyRequests;
        lock (_stateLock)
        {
            historyRequests = _historyRequests;
            _historyRequests = 0;
            staleEvents = _staleEvents;
            heartbeats = _heartbeatsAnswered;
            rttMax = _heartbeatRttMax;
            _staleEvents = 0;
            _heartbeatsAnswered = 0;
            _heartbeatRttMax = 0;
        }

        var line = $"[STATS] window_ms={now - _windowStartMs} reads_connected={_windowAges.Count} reads_disconnected={_windowDisconnectedReads} " +
                   $"ticks={_windowTicks} stale_events={staleEvents} heartbeats_answered={heartbeats} heartbeat_rtt_max_ms={rttMax} " +
                   // Chẩn đoán `4003 No pongs`: pong do vòng nhận trả lời — thread pool nghẽn ⇒ pong trễ (F4-4).
                   $"tp_threads={ThreadPool.ThreadCount} tp_pending={ThreadPool.PendingWorkItemCount} history_requests={historyRequests}";
        if (_windowAges.Count > 0)
        {
            var sorted = _windowAges.OrderBy(x => x).ToList();
            var pct = 100.0 * _windowWouldSkip / sorted.Count;
            line += $" tick_age_ms min={sorted[0]} p50={Percentile(sorted, 0.5)} p95={Percentile(sorted, 0.95)} max={sorted[^1]} " +
                    $"confirm_latency_ms={confirmLatencyMs} would_skip_latency_b={_windowWouldSkip} ({pct.ToString("0.0", CultureInfo.InvariantCulture)}%)";
        }

        if (bidB is > 0m && askB is > 0m)
        {
            line += $" b_bid={bidB} b_ask={askB} b_spread_pts={((askB - bidB) * point)!.Value.ToString("0.##", CultureInfo.InvariantCulture)}";
            if (exchangeA.Ask is > 0m)
            {
                line += $" a_ask={exchangeA.Ask} gap_buy_pts={((bidB - exchangeA.Ask) * point)!.Value.ToString("0.##", CultureInfo.InvariantCulture)} point={point}";
            }
        }

        Emit("INFO", line);
        _windowStartMs = now;
        _windowTicks = 0;
        _windowDisconnectedReads = 0;
        _windowWouldSkip = 0;
        _windowAges.Clear();
    }

    private static long Percentile(List<long> sorted, double p)
    {
        var index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private int NextRid()
    {
        lock (_stateLock)
        {
            return ++_rid;
        }
    }

    private void SendFrame(IPrimeXbtTransport transport, string type, int rid, string action, JsonObject? body)
    {
        try
        {
            if (!transport.Send(PrimeXbtEnvelope.Build(type, rid, action, body)))
            {
                Emit("DEBUG", $"không gửi được {action} (chưa kết nối)");
            }
        }
        catch (Exception ex)
        {
            Emit("WARN", $"gửi {action} lỗi: {ex.GetType().Name}");
        }
    }

    private void SetStatus(string status)
    {
        lock (_stateLock)
        {
            _status = status;
        }
    }

    private static ExchangeMetrics Disconnected(string? symbol, string error) => new(
        Symbol: string.IsNullOrWhiteSpace(symbol) ? "SanB" : symbol,
        Bid: null,
        Ask: null,
        Spread: null,
        LatencyMs: null,
        Tps: null,
        Time: DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        MaxLatMs: null,
        AvgLatMs: null,
        IsConnected: false,
        Error: error);

    private void Emit(string level, string message)
    {
        try
        {
            LogLine?.Invoke(PrimeXbtLogMasker.Apply($"[PRIMEXBT][{level}] {message}"));
        }
        catch
        {
            // handler lỗi không ảnh hưởng session
        }
    }

    private void Raise(PrimeXbtSessionEventKind kind, string message)
    {
        try
        {
            EventRaised?.Invoke(new PrimeXbtSessionEvent(kind, PrimeXbtLogMasker.Apply(message)));
        }
        catch
        {
            // handler lỗi không ảnh hưởng session
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
            _lifecycle = _lifecycle.ContinueWith(_ => Reconcile(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            lifecycle = _lifecycle;
        }

        try
        {
            lifecycle.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // shutdown best effort
        }
    }
}
