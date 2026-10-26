using System.Globalization;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.CTrader;
using TradeDesktop.Application.Services.TradeLocker;

namespace TradeDesktop.Infrastructure.TradeLocker;

// Thông tin để mở socket: account (cluster ⇒ host) + account JWT. Lỗi ⇒ Error (đã che token), caller fail-closed.
// Transient = lỗi mạng (nối lại theo backoff, coi như mất kết nối) — khác lỗi đăng nhập (chờ AuthRetryMs, báo AuthRequired).
public sealed record TradeLockerConnectInfo(TradeLockerAccount? Account, string? Jwt, string? Error, bool Transient = false);

// Phiên hermes duy nhất của sàn B TradeLocker (docs/plans/tradelocker Phase 4–7): RSocket trên WebSocket, mang CẢ giá
// (ITradeLockerQuoteSession) lẫn vị thế/lệnh (ITradeLockerTradeSession) — rớt socket thì mọi thứ cùng fail-closed.
// - Vòng đời do EnsureState (luồng poll 50 ms) điều khiển: chỉ mở socket khi platform_b = tradelocker.
// - Read/ReadTrades không chặn, không throw. Các mốc thời gian (keepalive, lifetime, reconnect) kiểm trong Read().
// - JWT chỉ kiểm lúc SETUP; Phase 0 Q1: server KHÔNG cắt socket khi JWT hết hạn ⇒ chỉ mint mới khi (re)connect.
// - THUẦN BỊ ĐỘNG với vị thế (Rule E). Gửi lệnh chỉ qua RequestAsync/SendOrderAsync do executor gọi.
public sealed class TradeLockerHermesSession : ITradeLockerQuoteSession, ITradeLockerTradeSession, IDisposable
{
    public const int KeepAliveMs = 5_000;
    public const int LifetimeMs = 15_000;
    public const uint RequestN = 100;
    public const int BackoffMinMs = 500;
    public const int BackoffMaxMs = 10_000;
    public const int AuthRetryMs = 30_000;
    public const int StormWindowMs = 60_000;
    public const int StormMaxReconnects = 5;
    public const int StormPauseMs = 300_000;
    public const int RequestTimeoutMs = 10_000;
    private const int StatsIntervalMs = 60_000;
    private const int DriftLogThrottleMs = 60_000;

    private const string RouteUnits = "v3.tradable-unit-aggregates";
    private const string RoutePositions = "v3.position-aggregates";
    private const string RouteNotifications = "v3.notifications";

    private enum StreamKind { Units, Positions, Notifications, Quotes }

    private sealed record InFlightOpen(IReadOnlySet<string> IdsBefore, decimal Lots, long SentTick, string Label)
    {
        public string? CorrelationId { get; set; }
        public bool Uncertain { get; set; }
    }

    public const int OpenMatchTimeoutMs = 10_000;

    private sealed class PendingRequest(TaskCompletionSource<TradeLockerRequestResult> completion, long deadlineTick, string route)
    {
        public TaskCompletionSource<TradeLockerRequestResult> Completion { get; } = completion;
        public long DeadlineTick { get; } = deadlineTick;
        public string Route { get; } = route;
    }

    private readonly Func<TradeLockerConfig, CancellationToken, Task<TradeLockerConnectInfo>> _connectInfo;
    private readonly Func<ITradeLockerTransport> _transportFactory;
    private readonly Func<long> _tickCount;
    private readonly Action<Func<Task>> _runBackground;
    private readonly Func<string, CancellationToken, Task<string?>>? _historyJwt;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _httpGet;
    private readonly object _stateLock = new();

    // ----- trạng thái dùng chung (dưới _stateLock) -----
    private bool _desired;
    private bool _disposed;
    private TradeLockerConfig? _config;
    private string? _configProblem;
    private int _generation;
    private ITradeLockerTransport? _transport;
    private bool _socketOpen;
    private bool _connecting;
    private TradeLockerAccount? _account;
    private string? _authProblem;
    private string _status = "TradeLocker chưa bật";
    private readonly TradeLockerSymbolIndex _index = new();
    private TradeLockerSymbol? _symbol;
    private decimal? _bid;
    private decimal? _ask;
    private long _lastQuoteTick;
    private long _quoteSequence;
    private readonly TradeLockerPositionStore _positions;
    private readonly Dictionary<int, StreamKind> _streams = [];
    private readonly Dictionary<int, uint> _streamReceived = [];
    private readonly Dictionary<int, PendingRequest> _pending = [];
    private readonly RSocketFragmentAssembler _assembler = new();
    private readonly LinkedList<TradeLockerNotification> _recentNotifications = new();
    private int _nextStreamId = 1;
    private long _lastFrameTick;
    private long _lastKeepAliveTick;
    private long _reconnectDueTick;
    private int _backoffMs = BackoffMinMs;
    private readonly Queue<long> _reconnectTicks = new();
    private long _stormUntilTick;
    private int _driftCount;
    private long _lastDriftLogTick = -DriftLogThrottleMs;
    private int _reconnects;
    // Phase 6: History map B từ REST hera (gộp theo ticket, version riêng). Chỉ hợp lệ sau lần tải đầu của KẾT NỐI HIỆN TẠI.
    private readonly Dictionary<ulong, HistorySharedRecord> _history = [];
    private ulong _historyVersion;
    private bool _historySynced;
    private bool _historyInFlight;
    private long _historyDueTick;
    private long _lastHistoryWarnTick = -DriftLogThrottleMs;
    // Phase 7A (R9): lệnh mở đang chờ thấy position mới, khoá theo tradable unit + chiều. Lệnh mở thứ hai cùng khoá bị từ
    // chối ngay (không chặn chờ) cho tới khi khớp / mơ hồ / quá OpenMatchTimeoutMs.
    private readonly Dictionary<(string Unit, TradeLockerSide Side), InFlightOpen> _openInFlight = [];

    // ----- trạng thái chỉ luồng poll (Read) dùng -----
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
    private long _statsWindowStart = -1;
    private int _statsTicks;
    private decimal _statsMaxAge;
    private string? _digitsMismatchReported;

    public TradeLockerHermesSession(
        Func<TradeLockerConfig, CancellationToken, Task<TradeLockerConnectInfo>> connectInfo,
        Func<ITradeLockerTransport> transportFactory,
        Func<long>? tickCount = null,
        Action<Func<Task>>? runBackground = null,
        Func<string, CancellationToken, Task<string?>>? historyJwt = null,
        Func<string, string, CancellationToken, Task<string?>>? httpGet = null)
    {
        _connectInfo = connectInfo;
        _transportFactory = transportFactory;
        _tickCount = tickCount ?? (() => Environment.TickCount64);
        _runBackground = runBackground ?? (work => _ = Task.Run(work));
        _historyJwt = historyJwt;
        _httpGet = httpGet;
        _positions = new TradeLockerPositionStore(_tickCount);
    }

    // Đường production: auth thật (kho DPAPI + Keycloak + kerber) + ClientWebSocket.
    public static TradeLockerHermesSession CreateDefault(TradeLockerAuthSession auth)
        => new(
            async (config, ct) =>
            {
                var account = await auth.ResolveAccountAsync(config, ct).ConfigureAwait(false);
                if (account is null)
                {
                    return new TradeLockerConnectInfo(null, null, auth.LastError ?? "không resolve được account TradeLocker", auth.LastErrorIsTransient);
                }

                var jwt = await auth.GetAccountJwtAsync(account.AccId, ct).ConfigureAwait(false);
                return jwt is null
                    ? new TradeLockerConnectInfo(account, null, auth.LastError ?? "không mint được account JWT", auth.LastErrorIsTransient)
                    : new TradeLockerConnectInfo(account, jwt.Jwt, null);
            },
            () => new ClientWebSocketTradeLockerTransport(),
            historyJwt: async (accId, ct) => (await auth.GetAccountJwtAsync(accId, ct).ConfigureAwait(false))?.Jwt,
            httpGet: HeraGetAsync);

    private static readonly HttpClient HeraHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    // GET hera (Bearer = account JWT). Lỗi ⇒ null (caller thử lại theo lịch, không throw).
    private static async Task<string?> HeraGetAsync(string url, string jwt, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await HeraHttp.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    public event Action<TradeLockerSessionEvent>? EventRaised;

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

    public bool IsConnected
    {
        get
        {
            lock (_stateLock)
            {
                return _socketOpen;
            }
        }
    }

    public TradeLockerAccount? CurrentAccount
    {
        get
        {
            lock (_stateLock)
            {
                return _account;
            }
        }
    }

    public TradeLockerSymbol? CurrentSymbol
    {
        get
        {
            lock (_stateLock)
            {
                return _symbol;
            }
        }
    }

    // ---------------- vòng đời ----------------

    public void EnsureState(string platformB, TradeLockerConfig config)
    {
        try
        {
            var want = TradeLockerRoutingRules.IsTradeLockerPlatform(platformB);
            var cfg = (config ?? TradeLockerConfig.Empty).Normalize();
            ITradeLockerTransport? toStop = null;
            string? log = null;
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                if (!want)
                {
                    if (_desired)
                    {
                        _desired = false;
                        toStop = DetachTransportLocked();
                        _status = "TradeLocker đã dừng (sàn B không còn là TradeLocker)";
                        log = "stopped (sàn B không còn là TradeLocker)";
                    }
                }
                else
                {
                    var problem = string.IsNullOrWhiteSpace(cfg.Symbol) ? "Chưa cấu hình symbol TradeLocker" : null;
                    if (_desired && SameConnection(_config, cfg) && problem == _configProblem)
                    {
                        _config = cfg; // volume/latency đổi không cần nối lại
                        return;
                    }

                    var restart = _desired;
                    _desired = true;
                    _config = cfg;
                    _configProblem = problem;
                    _backoffMs = BackoffMinMs;
                    _reconnectDueTick = _tickCount();
                    _authProblem = null;
                    if (restart)
                    {
                        toStop = DetachTransportLocked();
                    }

                    _status = problem ?? "TradeLocker đang kết nối…";
                    log = restart ? "config đổi — nối lại" : "started";
                }
            }

            StopQuietly(toStop);
            if (log is not null)
            {
                Emit("INFO", log);
                if (!want)
                {
                    Raise(TradeLockerSessionEventKind.Stopped, log);
                }
            }
        }
        catch (Exception ex)
        {
            Emit("ERROR", "EnsureState lỗi: " + ex.Message);
        }
    }

    private static bool SameConnection(TradeLockerConfig? a, TradeLockerConfig b)
        => a is not null
           && string.Equals(a.AccountId, b.AccountId, StringComparison.Ordinal)
           && string.Equals(a.EffectiveBrand, b.EffectiveBrand, StringComparison.Ordinal)
           && string.Equals(a.Symbol, b.Symbol, StringComparison.Ordinal);

    private void CheckReconnectDue(long now)
    {
        TradeLockerConfig? config;
        int generation;
        lock (_stateLock)
        {
            if (!_desired || _disposed || _configProblem is not null || _socketOpen || _connecting || _transport is not null
                || now < _reconnectDueTick || now < _stormUntilTick)
            {
                return;
            }

            _connecting = true;
            generation = _generation;
            config = _config;
            _status = "TradeLocker đang kết nối…";
        }

        Raise(TradeLockerSessionEventKind.Connecting, "đang kết nối");
        _runBackground(() => ConnectAsync(generation, config!));
    }

    private async Task ConnectAsync(int generation, TradeLockerConfig config)
    {
        TradeLockerConnectInfo info;
        try
        {
            info = await _connectInfo(config, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            info = new TradeLockerConnectInfo(null, null, "lỗi xác thực: " + ex.GetType().Name);
        }

        ITradeLockerTransport transport;
        Uri uri;
        lock (_stateLock)
        {
            if (generation != _generation || !_desired || _disposed)
            {
                _connecting = false;
                return;
            }

            if (info.Account is null || info.Jwt is null)
            {
                _connecting = false;
                if (info.Transient)
                {
                    // Mất mạng: không phải vấn đề đăng nhập ⇒ backoff như rớt socket, không đặt _authProblem.
                    _reconnectDueTick = _tickCount() + _backoffMs;
                    _backoffMs = Math.Min(_backoffMs * 2, BackoffMaxMs);
                    _status = "TradeLocker mất mạng — đang thử nối lại";
                }
                else
                {
                    _authProblem = TradeLockerLogMasker.Apply(info.Error ?? "không có account/JWT");
                    _status = "TradeLocker chưa đăng nhập được: " + _authProblem;
                    _reconnectDueTick = _tickCount() + AuthRetryMs;
                }
            }
            else
            {
                _authProblem = null;
                _account = info.Account;
            }
        }

        if (info.Account is null || info.Jwt is null)
        {
            var message = TradeLockerLogMasker.Apply(info.Error ?? "không có account/JWT");
            if (info.Transient)
            {
                Emit("WARN", "không mở được phiên (mạng): " + message + " — thử lại theo backoff");
                Raise(TradeLockerSessionEventKind.Disconnected, message);
            }
            else
            {
                Emit("WARN", "không mở được phiên: " + message + $" — thử lại sau {AuthRetryMs / 1000} s");
                Raise(TradeLockerSessionEventKind.AuthRequired, message);
            }

            return;
        }

        var account = info.Account;
        Emit("INFO", $"account {account.Id} type={account.Type} engine={account.Engine} status={account.Status} cluster={account.Cluster}");
        if (!account.IsDemo)
        {
            Emit("WARN", $"account {account.Id} KHÔNG phải demo (type={account.Type})");
            Raise(TradeLockerSessionEventKind.AccountNotDemo, $"account {account.Id} type={account.Type}");
        }

        uri = new Uri($"wss://api.{account.Cluster}.tradelocker.com/hermes");
        transport = _transportFactory();
        var jwt = info.Jwt;
        transport.Connected += () => OnTransportConnected(transport, jwt);
        transport.Disconnected += reason => OnTransportDisconnected(transport, reason);
        transport.FrameReceived += frame => OnFrame(transport, frame);

        lock (_stateLock)
        {
            if (generation != _generation || !_desired || _disposed)
            {
                _connecting = false;
                transport.Dispose();
                return;
            }

            _transport = transport;
        }

        transport.Start(uri);
    }

    private void OnTransportConnected(ITradeLockerTransport transport, string jwt)
    {
        var frames = new List<byte[]>();
        lock (_stateLock)
        {
            if (!ReferenceEquals(transport, _transport))
            {
                return;
            }

            _socketOpen = true;
            _connecting = false;
            ResetConnectionStateLocked();
            var now = _tickCount();
            _lastFrameTick = now;
            _lastKeepAliveTick = now;
            _status = "TradeLocker đã kết nối — chờ danh mục symbol";
            _historySynced = false;
            _historyDueTick = now; // tải lịch sử ngay khi có kết nối

            frames.Add(RSocketFrameCodec.EncodeSetup(jwt, KeepAliveMs, LifetimeMs));
            frames.Add(OpenStreamLocked(StreamKind.Units, RouteUnits));
            frames.Add(OpenStreamLocked(StreamKind.Positions, RoutePositions));
            frames.Add(OpenStreamLocked(StreamKind.Notifications, RouteNotifications));
        }

        foreach (var f in frames)
        {
            transport.Send(f);
        }

        Emit("INFO", "socket mở, đã gửi SETUP + subscribe tradable units / positions / notifications");
        Raise(TradeLockerSessionEventKind.Connected, "socket mở");
    }

    private void OnTransportDisconnected(ITradeLockerTransport transport, string reason)
    {
        List<PendingRequest> failed;
        bool storm = false;
        int delay;
        lock (_stateLock)
        {
            if (!ReferenceEquals(transport, _transport))
            {
                return;
            }

            failed = DropTransportLocked();
            var now = _tickCount();
            delay = _backoffMs;
            _backoffMs = Math.Min(_backoffMs * 2, BackoffMaxMs);
            _reconnectDueTick = now + delay;
            _reconnectTicks.Enqueue(now);
            while (_reconnectTicks.Count > 0 && now - _reconnectTicks.Peek() > StormWindowMs)
            {
                _reconnectTicks.Dequeue();
            }

            if (_reconnectTicks.Count > StormMaxReconnects)
            {
                storm = true;
                _stormUntilTick = now + StormPauseMs;
                _reconnectTicks.Clear();
                _status = $"TradeLocker NGẮT MẠCH: rớt > {StormMaxReconnects} lần/{StormWindowMs / 1000} s — tự thử lại sau {StormPauseMs / 60_000} phút";
            }
            else
            {
                _status = _desired ? $"TradeLocker mất kết nối ({reason}) — nối lại sau {delay} ms" : _status;
            }
        }

        FailPending(failed, "socket đóng trước khi có phản hồi");
        if (!_desired)
        {
            return;
        }

        Emit("WARN", $"socket đóng: {TradeLockerLogMasker.Apply(reason)} — nối lại sau {delay} ms");
        Raise(TradeLockerSessionEventKind.Disconnected, reason);
        if (storm)
        {
            Emit("ERROR", "bão reconnect — tạm dừng nối lại");
            Raise(TradeLockerSessionEventKind.ReconnectStorm, "bão reconnect");
        }
    }

    // ---------------- nhận frame ----------------

    private void OnFrame(ITradeLockerTransport transport, byte[] raw)
    {
        var outgoing = new List<byte[]>();
        var completions = new List<(PendingRequest Request, TradeLockerRequestResult Result)>();
        string? warn = null;
        bool stopTransport = false;
        bool symbolResolved = false;
        string? drift = null;
        lock (_stateLock)
        {
            if (!ReferenceEquals(transport, _transport) || !_socketOpen)
            {
                return;
            }

            var now = _tickCount();
            _lastFrameTick = now;
            if (!RSocketFrameCodec.TryDecode(raw, out var frame))
            {
                drift = "frame RSocket không decode được";
            }
            else
            {
                switch (frame.Type)
                {
                    case RSocketFrameType.KeepAlive:
                        if (frame.Has(RSocketFlags.Respond))
                        {
                            outgoing.Add(RSocketFrameCodec.EncodeKeepAlive(respond: false, frame.Data.Span));
                        }

                        break;
                    case RSocketFrameType.Error:
                        HandleErrorFrameLocked(frame, completions, ref warn, ref stopTransport);
                        break;
                    case RSocketFrameType.Payload:
                        HandlePayloadLocked(frame, now, outgoing, completions, ref symbolResolved, ref drift, ref stopTransport);
                        break;
                }
            }

            if (drift is not null)
            {
                _driftCount++;
                if (now - _lastDriftLogTick < DriftLogThrottleMs)
                {
                    drift = null; // đã đếm, không spam log
                }
                else
                {
                    _lastDriftLogTick = now;
                }
            }
        }

        foreach (var f in outgoing)
        {
            transport.Send(f);
        }

        foreach (var (request, result) in completions)
        {
            request.Completion.TrySetResult(result);
        }

        if (symbolResolved && CurrentSymbol is { } symbol)
        {
            Emit("INFO", $"symbol {symbol.Symbol} tradableUnit={symbol.TradableUnitId} ticker={symbol.TickerId} tick={symbol.TickSize} " +
                         $"lotSize={symbol.LotSize} step={symbol.LotStep} min={symbol.MinimumLot} max={symbol.MaximumLot} status={symbol.TradingStatus}");
            Raise(TradeLockerSessionEventKind.SymbolResolved, symbol.Symbol);
        }

        if (drift is not null)
        {
            Emit("WARN", "[DRIFT] " + drift);
            Raise(TradeLockerSessionEventKind.ProtocolDrift, drift);
        }

        if (warn is not null)
        {
            Emit("WARN", warn);
        }

        if (stopTransport)
        {
            StopQuietly(transport); // ⇒ Disconnected ⇒ backoff ⇒ reconnect (fail-closed trong lúc chờ)
        }
    }

    private void HandleErrorFrameLocked(
        RSocketFrame frame,
        List<(PendingRequest, TradeLockerRequestResult)> completions,
        ref string? warn,
        ref bool stopTransport)
    {
        var error = TradeLockerMessages.DecodeErrorFrameMessage(frame.ErrorMessage);
        if (frame.StreamId == 0)
        {
            warn = $"lỗi kết nối 0x{frame.ErrorCode:X}: {TradeLockerErrorMapper.Describe(error)} — đóng socket";
            stopTransport = true;
            return;
        }

        if (_pending.Remove(frame.StreamId, out var request))
        {
            completions.Add((request, TradeLockerRequestResult.Failed(error)));
            return;
        }

        if (_streams.Remove(frame.StreamId, out var kind))
        {
            // Stream dữ liệu bắt buộc bị server huỷ ⇒ không còn tin được trạng thái ⇒ nối lại.
            warn = $"stream {kind} bị huỷ: {TradeLockerErrorMapper.Describe(error)} — nối lại";
            stopTransport = kind != StreamKind.Notifications;
        }
    }

    private void HandlePayloadLocked(
        RSocketFrame frame,
        long now,
        List<byte[]> outgoing,
        List<(PendingRequest, TradeLockerRequestResult)> completions,
        ref bool symbolResolved,
        ref string? drift,
        ref bool stopTransport)
    {
        var assembled = _assembler.Feed(frame);
        if (assembled is not { } data)
        {
            return;
        }

        if (_pending.Remove(frame.StreamId, out var request))
        {
            completions.Add((request, TradeLockerRequestResult.Ack(TradeLockerMessages.DecodeCommandAck(data))));
            return;
        }

        if (!_streams.TryGetValue(frame.StreamId, out var kind))
        {
            return;
        }

        if (frame.Has(RSocketFlags.Next))
        {
            switch (kind)
            {
                case StreamKind.Units:
                    if (TradeLockerMessages.TryDecodeTradableUnits(data, out var units))
                    {
                        _index.Apply(units);
                        if (_symbol is null && _config is not null && _index.Resolve(_config.Symbol) is { } resolved)
                        {
                            _symbol = resolved;
                            symbolResolved = true;
                            _status = $"TradeLocker đã kết nối — {resolved.Symbol}";
                            outgoing.Add(OpenStreamLocked(StreamKind.Quotes, $"v3.tickers.{resolved.TickerId}.quotes"));
                        }
                    }
                    else
                    {
                        drift = "tradable-unit-aggregates không decode được";
                    }

                    break;
                case StreamKind.Quotes:
                    if (TradeLockerMessages.TryDecodeQuote(data, out var quote))
                    {
                        _bid = quote.Bid;
                        _ask = quote.Ask;
                        _lastQuoteTick = now;
                        _quoteSequence++;
                    }
                    else
                    {
                        drift = "quote không decode được";
                    }

                    break;
                case StreamKind.Positions:
                    if (TradeLockerMessages.TryDecodePositionEvent(data, out var positionEvent))
                    {
                        _positions.Apply(positionEvent);
                        if (positionEvent.IsDeletion)
                        {
                            // Position vừa đóng ⇒ kéo lịch sử sớm (0.5 s) thay vì chờ nhịp 10 s.
                            _historyDueTick = Math.Min(_historyDueTick, now + 500);
                        }
                    }
                    else
                    {
                        _positions.MarkUnsynced(); // không biết event này đổi gì ⇒ không tin snapshot nữa
                        drift = "position-aggregates không decode được";
                        stopTransport = true;
                    }

                    break;
                case StreamKind.Notifications:
                    if (TradeLockerMessages.TryDecodeNotification(data, out var notification))
                    {
                        _recentNotifications.AddLast(notification);
                        while (_recentNotifications.Count > 50)
                        {
                            _recentNotifications.RemoveFirst();
                        }
                    }

                    break;
            }

            var received = _streamReceived.GetValueOrDefault(frame.StreamId) + 1;
            if (received >= RequestN)
            {
                received = 0;
                outgoing.Add(RSocketFrameCodec.EncodeRequestN(frame.StreamId, RequestN));
            }

            _streamReceived[frame.StreamId] = received;
        }

        if (frame.Has(RSocketFlags.Complete))
        {
            _streams.Remove(frame.StreamId);
            drift = $"server kết thúc stream {kind}";
            stopTransport = kind != StreamKind.Notifications;
        }
    }

    // ---------------- request-response (Phase 7) ----------------

    // Gửi một request-response. NotConnected ⇒ CHẮC CHẮN chưa gửi. Uncertain ⇒ đã gửi nhưng không biết kết quả
    // (socket đóng / quá RequestTimeoutMs) — caller KHÔNG được gửi lại (Phase 0 Q6b: lệnh vẫn có thể đã khớp).
    public Task<TradeLockerRequestResult> RequestAsync(string route, byte[] data)
    {
        ITradeLockerTransport? transport;
        byte[] frame;
        PendingRequest request;
        lock (_stateLock)
        {
            if (!_socketOpen || _transport is null)
            {
                return Task.FromResult(TradeLockerRequestResult.NotConnectedResult);
            }

            var streamId = NextStreamIdLocked();
            request = new PendingRequest(
                new TaskCompletionSource<TradeLockerRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                _tickCount() + RequestTimeoutMs,
                route);
            _pending[streamId] = request;
            frame = RSocketFrameCodec.EncodeRequestResponse(streamId, route, data);
            transport = _transport;
        }

        if (!transport.Send(frame))
        {
            // Hàng đợi gửi từ chối ⇒ frame không rời máy ⇒ chắc chắn chưa gửi.
            lock (_stateLock)
            {
                foreach (var key in _pending.Where(p => ReferenceEquals(p.Value, request)).Select(p => p.Key).ToList())
                {
                    _pending.Remove(key);
                }
            }

            return Task.FromResult(TradeLockerRequestResult.NotConnectedResult);
        }

        return request.Completion.Task;
    }

    public IReadOnlyList<TradeLockerNotification> NotificationsFor(string correlationId)
    {
        lock (_stateLock)
        {
            return _recentNotifications.Where(n => n.CorrelationId == correlationId).ToList();
        }
    }

    public IReadOnlySet<string> PositionIdsSnapshot()
    {
        lock (_stateLock)
        {
            return _positions.Ids.ToHashSet(StringComparer.Ordinal);
        }
    }

    public IReadOnlyList<TradeLockerPositionView> PositionsSnapshot()
    {
        lock (_stateLock)
        {
            return _positions.All();
        }
    }

    // ---------------- Read (luồng poll 50 ms) ----------------

    public ExchangeMetrics Read(ExchangeMetrics exchangeA, int point, int confirmLatencyMs)
    {
        try
        {
            var nowTick = _tickCount();
            CheckTimers(nowTick);
            CheckReconnectDue(nowTick);
            CheckHistoryDue(nowTick);
            CheckInFlightOpens(nowTick);

            bool desired;
            bool socketOpen;
            string? problem;
            string? authProblem;
            TradeLockerSymbol? symbol;
            decimal? bid;
            decimal? ask;
            long lastQuoteTick;
            long sequence;
            int generation;
            lock (_stateLock)
            {
                desired = _desired && !_disposed;
                socketOpen = _socketOpen;
                problem = _configProblem;
                authProblem = _authProblem;
                symbol = _symbol;
                bid = _bid;
                ask = _ask;
                lastQuoteTick = _lastQuoteTick;
                sequence = _quoteSequence;
                generation = _generation;
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
                reason = "TradeLocker chưa bật";
            }
            else if (problem is not null)
            {
                reason = problem;
            }
            else if (authProblem is not null)
            {
                reason = "TradeLocker chưa đăng nhập: " + authProblem;
            }
            else if (!socketOpen)
            {
                reason = "TradeLocker chưa kết nối";
            }
            else if (symbol is null)
            {
                reason = "Chưa resolve được symbol TradeLocker";
            }
            else
            {
                var check = PointDigitsConsistencyChecker.Check(point, DigitsOf(symbol.TickSize));
                if (!check.IsConsistent)
                {
                    reason = "Lệch digits: " + check.Message;
                    if (_digitsMismatchReported != check.Message)
                    {
                        _digitsMismatchReported = check.Message;
                        Emit("ERROR", reason);
                        Raise(TradeLockerSessionEventKind.DigitsMismatch, check.Message);
                    }
                }
                else if (bid is not > 0m || ask is not > 0m)
                {
                    reason = "Chưa có giá TradeLocker";
                }
                else
                {
                    _digitsMismatchReported = null;
                }
            }

            if (reason is not null)
            {
                if (desired)
                {
                    RecordStats(now, 0, newTicks);
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

            RecordStats(now, age, newTicks);
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
            return Disconnected(null, "TradeLocker Read lỗi: " + ex.Message);
        }
    }

    // ---------------- ITradeLockerTradeSession ----------------

    public SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName)
    {
        try
        {
            lock (_stateLock)
            {
                if (!_desired || _disposed || !_socketOpen || _authProblem is not null || _symbol is null || !_positions.Synced)
                {
                    return SharedMapReadResult<TradeSharedRecord>.MapNotFound(mapName);
                }

                var records = _positions.ToTradeRecords(unit => _index.SymbolOfTradableUnit(unit));
                return SharedMapReadResult<TradeSharedRecord>.Success(_positions.Version, records, records.Count, connected: 1);
            }
        }
        catch (Exception ex)
        {
            Emit("ERROR", "ReadTrades lỗi: " + ex.Message);
            return SharedMapReadResult<TradeSharedRecord>.MapNotFound(mapName);
        }
    }

    public TradeLockerPositionView? TryGetOpenPosition(string positionId)
    {
        lock (_stateLock)
        {
            return _positions.Synced && _positions.Get(positionId) is { Quantity: > 0m } view ? view : null;
        }
    }

    public string? OpenBlockReason
    {
        get
        {
            lock (_stateLock)
            {
                if (!_desired || _disposed)
                {
                    return "TradeLocker chưa bật";
                }

                if (_authProblem is not null)
                {
                    return "TradeLocker chưa đăng nhập: " + _authProblem;
                }

                if (!_socketOpen)
                {
                    return "TradeLocker chưa kết nối";
                }

                if (_symbol is null)
                {
                    return "Chưa resolve được symbol TradeLocker";
                }

                return _symbol.IsTradingActive ? null : $"Symbol {_symbol.Symbol} không ACTIVE (trading_status={_symbol.TradingStatus})";
            }
        }
    }

    // ---------------- gửi lệnh (Phase 7A) ----------------

    // ĐƯỜNG DUY NHẤT gửi lệnh TradeLocker; chỉ TradeLockerTradeExecutor gọi khi router ra lệnh (Rule E).
    // Success = server ack (KHÔNG có nghĩa đã có vị thế — xác nhận qua Trades map như mọi platform). Lỗi transport sau khi
    // gửi ⇒ IsUncertain, KHÔNG BAO GIỜ gửi lại (Phase 0 Q6b: lệnh vẫn khớp dù client thấy lỗi); session đối soát và chỉ
    // báo cáo. Close nhận NOT_FOUND ⇒ coi là đã đóng chỉ khi PositionStore cũng không còn id đó.
    public async Task<TradeLockerOrderOutcome> SendOrderAsync(TradeLockerOrderPlan plan, CancellationToken cancellationToken = default)
    {
        var allowedRoute = plan.Kind switch
        {
            TradeLockerOrderKind.OpenMarket => plan.Route == TradeLockerOrderPlanner.RouteOpen
                                               && plan.TradableUnitId is { Length: > 0 }
                                               && plan.Side is TradeLockerSide.Buy or TradeLockerSide.Sell,
            TradeLockerOrderKind.ClosePosition => plan.PositionId is { Length: > 0 } id && plan.Route == TradeLockerOrderPlanner.CloseRoute(id),
            _ => false
        };
        if (!allowedRoute)
        {
            Emit("ERROR", $"[ORDER] từ chối route không được phép: {plan.Route}");
            return new TradeLockerOrderOutcome(false, $"Route không được phép: {plan.Route}");
        }

        var label = plan.Kind == TradeLockerOrderKind.OpenMarket
            ? $"OPEN {plan.Side} {plan.Quantity} unit={plan.TradableUnitId}"
            : $"CLOSE position={plan.PositionId}";
        (string, TradeLockerSide) key = (plan.TradableUnitId ?? string.Empty, plan.Side);
        InFlightOpen? inFlight = null;
        lock (_stateLock)
        {
            if (!_socketOpen || _transport is null || _symbol is null)
            {
                return new TradeLockerOrderOutcome(false, TradeLockerOrderOutcome.NotConnected);
            }

            if (plan.Kind == TradeLockerOrderKind.OpenMarket)
            {
                if (_openInFlight.TryGetValue(key, out var existing))
                {
                    return new TradeLockerOrderOutcome(false,
                        $"OPEN_IN_FLIGHT: lệnh {existing.Label} gửi {_tickCount() - existing.SentTick} ms trước chưa thấy position — không gửi chồng");
                }

                inFlight = new InFlightOpen(_positions.Ids.ToHashSet(StringComparer.Ordinal), plan.Lots, _tickCount(), label);
                _openInFlight[key] = inFlight;
            }
        }

        Emit("INFO", $"[ORDER] SEND {label} route={plan.Route}");
        var sentTick = _tickCount();
        var result = await RequestAsync(plan.Route, plan.Data).ConfigureAwait(false);
        var elapsed = _tickCount() - sentTick;

        switch (result.Status)
        {
            case TradeLockerRequestStatus.Ack:
                if (inFlight is not null)
                {
                    lock (_stateLock)
                    {
                        inFlight.CorrelationId = result.CorrelationId;
                    }
                }

                Emit("INFO", $"[ORDER] ACK {label} {elapsed} ms correlation={result.CorrelationId}");
                return new TradeLockerOrderOutcome(true, $"ack {elapsed} ms", result.CorrelationId);

            case TradeLockerRequestStatus.Failed:
            {
                var error = result.Error!;
                var category = TradeLockerErrorMapper.Classify(error);
                ClearInFlight(key, inFlight);
                if (plan.Kind == TradeLockerOrderKind.ClosePosition && category == TradeLockerErrorCategory.NotFound
                    && TryGetOpenPosition(plan.PositionId!) is null)
                {
                    Emit("INFO", $"[ORDER] {label}: NOT_FOUND và không còn trong danh sách vị thế ⇒ đã đóng");
                    return new TradeLockerOrderOutcome(true, "ALREADY_CLOSED (NOT_FOUND)", Error: error);
                }

                Emit("WARN", $"[ORDER] REJECT {label} {elapsed} ms — {TradeLockerErrorMapper.Describe(error)}");
                return new TradeLockerOrderOutcome(false, "REJECTED " + TradeLockerErrorMapper.Describe(error), Error: error);
            }

            case TradeLockerRequestStatus.Uncertain:
            {
                if (inFlight is not null)
                {
                    lock (_stateLock)
                    {
                        inFlight.Uncertain = true; // giữ in-flight cho tới khi đối soát xong
                    }
                }

                var message = $"[ORDER] {TradeLockerOrderOutcome.TimeoutUncertain} {label} ({result.Detail}) — KHÔNG gửi lại; " +
                              (inFlight is not null ? $"đối soát vị thế trong {OpenMatchTimeoutMs / 1000} s" : "xác nhận qua danh sách vị thế");
                Emit("WARN", message);
                Raise(TradeLockerSessionEventKind.OrderUncertain, message);
                return new TradeLockerOrderOutcome(false, $"{TradeLockerOrderOutcome.TimeoutUncertain} ({result.Detail})", IsUncertain: true);
            }

            default:
                ClearInFlight(key, inFlight);
                Emit("WARN", $"[ORDER] không gửi được {label} (socket chưa sẵn sàng)");
                return new TradeLockerOrderOutcome(false, TradeLockerOrderOutcome.NotConnected);
        }
    }

    private void ClearInFlight((string, TradeLockerSide) key, InFlightOpen? inFlight)
    {
        if (inFlight is null)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_openInFlight.TryGetValue(key, out var current) && ReferenceEquals(current, inFlight))
            {
                _openInFlight.Remove(key);
            }
        }
    }

    // Gọi từ Read (50 ms): giải phóng lệnh mở in-flight khi thấy đúng một position mới (FILLED), khi mơ hồ (≥ 2, R9 —
    // KHÔNG đoán) hoặc quá OpenMatchTimeoutMs. Chỉ ghi log/sự kiện — Rule E: không tạo lệnh nào ở đây.
    private void CheckInFlightOpens(long now)
    {
        List<(string Level, string Message, bool Alert)>? reports = null;
        lock (_stateLock)
        {
            if (_openInFlight.Count == 0)
            {
                return;
            }

            var current = _positions.All();
            foreach (var (key, open) in _openInFlight.ToList())
            {
                var match = TradeLockerOrderMatcher.Match(open.IdsBefore, current, key.Unit, key.Side, open.Lots);
                (string, string, bool)? report = match.Status switch
                {
                    TradeLockerMatchStatus.Matched => ("INFO",
                        $"[ORDER] FILLED {open.Label} position={match.PositionId} sau {now - open.SentTick} ms" +
                        (open.Uncertain ? " (lệnh từng TIMEOUT_UNCERTAIN — vị thế CÓ khớp)" : string.Empty),
                        open.Uncertain),
                    TradeLockerMatchStatus.Ambiguous => ("ERROR",
                        $"[ORDER] AMBIGUOUS {open.Label}: {match.CandidateCount} position mới cùng unit + chiều — không đoán, kiểm tay",
                        true),
                    _ when now - open.SentTick >= OpenMatchTimeoutMs => (open.Uncertain ? "ERROR" : "WARN",
                        $"[ORDER] RECONCILE {open.Label}: không thấy position mới sau {OpenMatchTimeoutMs / 1000} s" +
                        (open.CorrelationId is { } corr && _recentNotifications.FirstOrDefault(n => n.CorrelationId == corr && n.IsOrderRejected) is { } rejected
                            ? $" — server reject: {rejected.RejectDescription}"
                            : string.Empty),
                        open.Uncertain),
                    _ => null
                };

                if (report is { } r)
                {
                    _openInFlight.Remove(key);
                    (reports ??= []).Add(r);
                }
            }
        }

        foreach (var (level, message, alert) in reports ?? [])
        {
            Emit(level, message);
            if (alert)
            {
                Raise(TradeLockerSessionEventKind.OrderUncertain, message);
            }
        }
    }

    // ---------------- History (Phase 6) ----------------

    public const int HistoryIntervalMs = 10_000;

    public SharedMapReadResult<HistorySharedRecord> ReadHistory(string mapName)
    {
        lock (_stateLock)
        {
            if (!_desired || _disposed || !_socketOpen || !_historySynced)
            {
                return SharedMapReadResult<HistorySharedRecord>.MapNotFound(mapName);
            }

            var records = _history.Values.OrderBy(r => r.CloseTimeMsc).ToList();
            return SharedMapReadResult<HistorySharedRecord>.Success(_historyVersion, records, records.Count, connected: 1);
        }
    }

    private void CheckHistoryDue(long now)
    {
        if (_historyJwt is null || _httpGet is null)
        {
            return;
        }

        int generation;
        TradeLockerAccount account;
        lock (_stateLock)
        {
            if (!_socketOpen || _account is null || _historyInFlight || now < _historyDueTick)
            {
                return;
            }

            _historyInFlight = true;
            generation = _generation;
            account = _account;
        }

        _runBackground(() => FetchHistoryAsync(generation, account));
    }

    private async Task FetchHistoryAsync(int generation, TradeLockerAccount account)
    {
        string? json = null;
        try
        {
            var jwt = await _historyJwt!(account.AccId, CancellationToken.None).ConfigureAwait(false);
            if (jwt is not null)
            {
                // Trang đầu = mới nhất trước (20 dòng, kiểm 2026-10-26) — đủ cho các lần đóng gần đây của app.
                json = await _httpGet!(
                    $"https://api.{account.Cluster}.tradelocker.com/hera/api/v1/reports/closed-positions-history",
                    jwt,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            json = null;
        }

        string? warn = null;
        lock (_stateLock)
        {
            _historyInFlight = false;
            var now = _tickCount();
            _historyDueTick = now + HistoryIntervalMs;
            if (generation != _generation)
            {
                return;
            }

            if (json is null)
            {
                if (now - _lastHistoryWarnTick >= DriftLogThrottleMs)
                {
                    _lastHistoryWarnTick = now;
                    warn = "tải lịch sử hera thất bại — thử lại sau " + (HistoryIntervalMs / 1000) + " s";
                }

                return;
            }

            var changed = false;
            foreach (var record in TradeLockerHistoryProjector.Project(TradeLockerHistoryProjector.ParsePage(json).Items))
            {
                if (!_history.TryGetValue(record.Ticket, out var old) || old != record)
                {
                    _history[record.Ticket] = record;
                    changed = true;
                }
            }

            if (changed || !_historySynced)
            {
                _historyVersion++;
            }

            _historySynced = true;
        }

        if (warn is not null)
        {
            Emit("WARN", warn);
        }
    }

    // ---------------- timers ----------------

    private void CheckTimers(long now)
    {
        ITradeLockerTransport? transport = null;
        ITradeLockerTransport? toStop = null;
        List<PendingRequest>? expired = null;
        string? warn = null;
        lock (_stateLock)
        {
            if (_pending.Count > 0)
            {
                foreach (var (key, request) in _pending.Where(p => now >= p.Value.DeadlineTick).ToList())
                {
                    _pending.Remove(key);
                    (expired ??= []).Add(request);
                }
            }

            if (_socketOpen && _transport is not null)
            {
                if (now - _lastFrameTick > LifetimeMs)
                {
                    warn = $"không nhận frame nào trong {LifetimeMs} ms — giá đã xoá, đóng socket để nối lại";
                    _bid = null;
                    _ask = null;
                    toStop = _transport;
                }
                else if (now - _lastKeepAliveTick >= KeepAliveMs)
                {
                    _lastKeepAliveTick = now;
                    transport = _transport;
                }
            }
        }

        transport?.Send(RSocketFrameCodec.EncodeKeepAlive(respond: true));
        if (expired is not null)
        {
            FailPending(expired, $"quá {RequestTimeoutMs} ms không có phản hồi");
        }

        if (warn is not null)
        {
            Emit("WARN", warn);
            StopQuietly(toStop);
        }
    }

    // ---------------- helpers ----------------

    private byte[] OpenStreamLocked(StreamKind kind, string route)
    {
        var streamId = NextStreamIdLocked();
        _streams[streamId] = kind;
        _streamReceived[streamId] = 0;
        return RSocketFrameCodec.EncodeRequestStream(streamId, route, RequestN);
    }

    private int NextStreamIdLocked()
    {
        var id = _nextStreamId;
        _nextStreamId += 2; // client dùng stream id lẻ
        return id;
    }

    private void ResetConnectionStateLocked()
    {
        _streams.Clear();
        _streamReceived.Clear();
        _index.Clear();
        _symbol = null;
        _bid = null;
        _ask = null;
        _lastQuoteTick = 0;
        _positions.ResetForNewConnection();
        _nextStreamId = 1;
    }

    // Tách transport khỏi session (đổi config / dừng): generation tăng ⇒ event muộn của transport cũ bị bỏ qua.
    private ITradeLockerTransport? DetachTransportLocked()
    {
        var transport = _transport;
        var failed = DropTransportLocked();
        if (failed.Count > 0)
        {
            var copy = failed;
            _runBackground(() =>
            {
                FailPending(copy, "phiên dừng trước khi có phản hồi");
                return Task.CompletedTask;
            });
        }

        return transport;
    }

    private List<PendingRequest> DropTransportLocked()
    {
        _generation++;
        _transport = null;
        _socketOpen = false;
        _connecting = false;
        _bid = null;
        _ask = null;
        _symbol = null;
        _positions.MarkUnsynced();
        _historySynced = false;
        _reconnects++;
        var failed = _pending.Values.ToList();
        _pending.Clear();
        return failed;
    }

    private static void FailPending(IEnumerable<PendingRequest> requests, string reason)
    {
        foreach (var request in requests)
        {
            request.Completion.TrySetResult(TradeLockerRequestResult.UncertainResult(reason));
        }
    }

    private static void StopQuietly(ITradeLockerTransport? transport)
    {
        if (transport is null)
        {
            return;
        }

        try
        {
            transport.Stop();
            transport.Dispose();
        }
        catch
        {
            // best effort
        }
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

        // Giống hệt PrimeXbtFwsSession.UpdateTps để hai sàn hiển thị TPS cùng quy ước.
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

    // [STATS] mỗi phút: số tick, tuổi tick lớn nhất, số lần nối lại, số drift — để chốt confirmLatencyB ở Phase 4.
    private void RecordStats(long now, decimal age, int newTicks)
    {
        if (_statsWindowStart < 0)
        {
            _statsWindowStart = now;
        }

        _statsTicks += newTicks;
        _statsMaxAge = Math.Max(_statsMaxAge, age);
        if (now - _statsWindowStart < StatsIntervalMs)
        {
            return;
        }

        int reconnects, drift, positions;
        bool open;
        lock (_stateLock)
        {
            reconnects = _reconnects;
            drift = _driftCount;
            positions = _positions.Open().Count;
            open = _socketOpen;
        }

        Emit("INFO", $"[STATS] window_s={(now - _statsWindowStart) / 1000} connected={open} ticks={_statsTicks} " +
                     $"max_tick_age_ms={_statsMaxAge} avg_interval_ms={(_latCount > 0 ? Math.Round(_latSum / _latCount) : 0)} " +
                     $"reconnects_total={reconnects} drift_total={drift} open_positions={positions}");
        _statsWindowStart = now;
        _statsTicks = 0;
        _statsMaxAge = 0;
    }

    // tick_size 0.01 ⇒ 2 chữ số; 0.5 ⇒ 1; 1 ⇒ 0. tick_size ≤ 0 ⇒ -1 (checker báo không hợp lệ ⇒ fail-closed).
    public static int DigitsOf(decimal tickSize)
    {
        if (tickSize <= 0m)
        {
            return -1;
        }

        var normalized = tickSize / 1.000000000000000000000000000000000m; // bỏ số 0 thừa
        var bits = decimal.GetBits(normalized);
        return (bits[3] >> 16) & 0xFF;
    }

    private static ExchangeMetrics Disconnected(string? symbol, string error) => new(
        Symbol: string.IsNullOrWhiteSpace(symbol) ? "SanB" : symbol,
        Bid: null,
        Ask: null,
        Spread: null,
        LatencyMs: null,
        Tps: null,
        Time: null,
        MaxLatMs: null,
        AvgLatMs: null,
        IsConnected: false,
        Error: error);

    private void Emit(string level, string message)
    {
        try
        {
            LogLine?.Invoke($"[TRADELOCKER][{level}] {TradeLockerLogMasker.Apply(message)}");
        }
        catch
        {
            // handler log lỗi không được làm hỏng phiên
        }
    }

    private void Raise(TradeLockerSessionEventKind kind, string message)
    {
        try
        {
            EventRaised?.Invoke(new TradeLockerSessionEvent(kind, TradeLockerLogMasker.Apply(message)));
        }
        catch
        {
            // ignored by design
        }
    }

    public void Dispose()
    {
        ITradeLockerTransport? transport;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _desired = false;
            transport = DetachTransportLocked();
        }

        StopQuietly(transport);
    }
}

public enum TradeLockerRequestStatus
{
    Ack,
    Failed,
    Uncertain,
    NotConnected
}

public sealed record TradeLockerRequestResult(TradeLockerRequestStatus Status, string CorrelationId, TradeLockerError? Error, string Detail)
{
    public static TradeLockerRequestResult NotConnectedResult { get; } = new(TradeLockerRequestStatus.NotConnected, string.Empty, null, "chưa kết nối");

    public static TradeLockerRequestResult Ack(string correlationId) => new(TradeLockerRequestStatus.Ack, correlationId, null, "ack");

    public static TradeLockerRequestResult Failed(TradeLockerError error) => new(TradeLockerRequestStatus.Failed, string.Empty, error, TradeLockerErrorMapper.Describe(error));

    public static TradeLockerRequestResult UncertainResult(string reason) => new(TradeLockerRequestStatus.Uncertain, string.Empty, null, reason);
}
