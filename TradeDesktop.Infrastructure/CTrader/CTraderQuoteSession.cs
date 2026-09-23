using System.Globalization;
using QuickFix;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.CTrader;

namespace TradeDesktop.Infrastructure.CTrader;

// Phase 4 — luồng GIÁ sàn B qua FIX QUOTE session. Singleton đăng ký không điều kiện; chỉ mở socket khi
// platform_b = ctrader (G4). KHÔNG mở TRADE session: Phase 0 câu 10 đo trên live — SecurityList được trả lời
// trên QUOTE. KHÔNG có đường gửi lệnh (Rule E): chỉ SecurityListRequest + MarketDataRequest subscribe/unsubscribe.
//
// Luồng thread:
//   - EnsureState/Read: vòng poll 50 ms của SharedMemoryMarketDataReader — không throw, không chặn.
//   - Start/Stop transport: chuỗi task nền tuần tự (_lifecycle) — logout có thể chờ LogoutTimeout.
//   - LoggedOn/LoggedOut/MessageReceived: thread của QuickFIX/n.
//   Book + catalog chỉ được chạm dưới _stateLock. Event của transport cũ bị bỏ qua nhờ _generation.
public sealed class CTraderQuoteSession : ICTraderQuoteSession, IDisposable
{
    public const string PlatformCTrader = "ctrader";
    public const string DictionaryFileName = "FIX44-CSERVER.xml";

    private const long StatsWindowMs = 60_000;
    private const decimal CrossCheckMaxRatio = 2m;

    // Kiểm tra sống (Phase 4, chủ dự án duyệt phương án A 2026-09-17). Đo trên live: tắt Wi-Fi → Windows KHÔNG đóng
    // socket, QuickFIX/n chỉ phát hiện nhờ heartbeat sau 46.8 s, suốt thời gian đó book giữ giá cũ. Im lặng
    // ProbeSilenceMs → gửi TestRequest (server trả Heartbeat kể cả khi thị trường yên); không có message nào về trong
    // ProbeTimeoutMs → xoá book (fail-closed). Session vẫn giữ nguyên để QuickFIX/n tự xử lý; message về lại → hồi phục.
    // 3 s/2 s ban đầu ngắt oan trên live (16:47:49, mạng tốt, hồi phục nhờ W chứ không nhờ Heartbeat) → nới 5 s/5 s
    // (chủ dự án duyệt) và ĐO xem cServer QUOTE có trả lời TestRequest không (probes_sent/probes_answered trong STATS).
    private const long ProbeSilenceMs = 5_000;
    private const long ProbeTimeoutMs = 5_000;

    private readonly Func<CTraderFixConfig, ICTraderFixTransport> _transportFactory;
    private readonly Func<long> _tickCount;

    // Ngắt mạch chống bão reconnect (sự cố 2026-09-21 19:23: 29 lần logon/phút suốt 18 phút lên broker).
    // Quá StormThreshold lần mất phiên trong StormWindowMs → dừng hẳn transport, KHÔNG tự nối lại; chỉ
    // EnsureState với trạng thái/cấu hình KHÁC (đổi platform, sửa config, mở lại app) mới gỡ.
    public const long StormWindowMs = 60_000;
    public const int StormThreshold = 5;

    // Sau khi ngắt mạch thì TỰ thử lại (chủ dự án duyệt 2026-09-23). Sự cố live 23/09 05:36: sàn reset phiên
    // hằng ngày, TRADE bị ngắt mạch rồi NẰM CHẾT 3 h 17 m — "chết thầm" nguy hiểm hơn là thử lại thưa.
    // Hết StormMaxRetries lần thì dừng hẳn như cũ, nhưng vẫn nhắc Telegram mỗi DeadAlertIntervalMs.
    public const long StormRetryCooldownMs = 5 * 60_000;
    public const int StormMaxRetries = 3;
    public const long DeadAlertIntervalMs = 15 * 60_000;

    private readonly Queue<long> _logoutTimes = new();
    private bool _stormTripped;
    private long _stormTrippedAtTick;
    private long _lastDeadAlertTick;
    private int _stormRetries;
    private long _quoteLoggedOnTick;

    private readonly object _desiredLock = new();
    private bool _desiredActive;
    private CTraderFixConfig? _desiredConfig;
    private string? _configProblem;
    private bool _disposed;
    private Task _lifecycle = Task.CompletedTask;

    // Chỉ chuỗi _lifecycle chạm hai field này.
    private ICTraderFixTransport? _transport;
    private CTraderFixConfig? _activeConfig;

    private readonly object _stateLock = new();
    private readonly CTraderSecurityCatalog _catalog = new();
    private int _generation;
    private bool _quoteLoggedOn;
    private CTraderQuoteBook? _book;
    private int _symbolId;
    private bool _anomalyLogged;
    private int _anomalyCount;
    private bool _crossCheckPending;
    // Lớp B (chủ dự án duyệt 2026-09-21): CHỈ subscribe giá SAU khi SecurityList đã về. Hỏi symbol rồi stream ngay
    // khiến message SecurityList và luồng W chen nhau trên cùng socket — bộ đọc QuickFIX/n mất đồng bộ (sự cố 19:23).
    // Thêm một lợi ích: không nhận tick nào trước khi biết digits (R6 kiểm trước khi có giá).
    private bool _subscriptionSent;
    private long _securityListRequestedTick;
    private ICTraderFixTransport? _liveTransport;
    private long _lastInboundTick;
    private long _probeSentTick;
    private long _lastProbeTick;
    private bool _linkStale;
    private int _staleEvents;
    private int _failedReconnects;
    private string? _lastProbeId;
    private long _lastProbeIdSentTick;
    private int _probesSent;
    private int _probesAnswered;
    private long _probeAnswerMaxMs;
    private bool _probeAnswerLogged;
    private string _status = "Chưa bật (sàn B không phải cTrader)";

    // Phía đọc — chỉ thread poll chạm.
    private int _readGeneration = -1;
    private long _lastSequence;
    private long _tpsSecond = -1;
    private int _tpsCount;
    private float? _lastTps;
    private decimal _latSum;
    private long _latCount;
    private decimal _latMax;
    private long _prevQuoteTick;
    private decimal? _lastInterval;
    private (int Point, int Digits)? _mismatchReported;
    private readonly List<long> _windowAges = [];
    private long _windowStartMs = -1;
    private int _windowDisconnectedReads;
    private long _windowTicks;
    private int _windowWouldSkip;

    public CTraderQuoteSession(Func<CTraderFixConfig, ICTraderFixTransport> transportFactory, Func<long>? tickCount = null)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _tickCount = tickCount ?? (() => Environment.TickCount64);
    }

    // Transport thật: KHÔNG truyền callback log raw — W đến theo từng tick, log raw sẽ ngập file. Session tự log
    // các sự kiện vòng đời; mọi dòng qua CTraderFixLogMasker.
    public static CTraderQuoteSession CreateDefault()
    {
        CTraderQuoteSession? self = null;
        var raw = IsRawLogEnabled();
        self = new CTraderQuoteSession(config => new QuickFixCTraderTransport(
            config,
            Path.Combine(AppContext.BaseDirectory, DictionaryFileName),
            raw ? line => self?.EmitRaw(line) : null));
        return self;
    }

    // Chẩn đoán (Phase 5, chủ dự án duyệt): CTRADER_FIX_RAW_LOG=1 → ghi message FIX thô (trừ W) vào -ctrader.log. Mặc định TẮT.
    // Transport đã che 554; Emit che lần nữa.
    public const string RawLogEnvironmentVariable = "CTRADER_FIX_RAW_LOG";

    public static bool IsRawLogEnabled()
        => string.Equals(Environment.GetEnvironmentVariable(RawLogEnvironmentVariable), "1", StringComparison.Ordinal);

    internal void EmitRaw(string line)
    {
        if (line.Contains("|35=W|", StringComparison.Ordinal))
        {
            return;
        }

        Emit("DEBUG", line);
    }

    public event Action<CTraderQuoteSessionEvent>? EventRaised;
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

    public bool IsQuoteLoggedOn
    {
        get
        {
            lock (_stateLock)
            {
                return _quoteLoggedOn;
            }
        }
    }

    // Test đợi chuỗi start/stop nền chạy xong.
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
            var isCTrader = string.Equals(platformB?.Trim(), PlatformCTrader, StringComparison.OrdinalIgnoreCase);
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
                            $"{problem} — không mở QUOTE session");
                    }
                }

                if (want == _desiredActive && Equals(target, _desiredConfig))
                {
                    return;
                }

                _desiredActive = want;
                _desiredConfig = target;
                // Trạng thái mong muốn đổi = có can thiệp của người dùng → gỡ ngắt mạch.
                ResetStorm();
                _lifecycle = _lifecycle.ContinueWith(_ => Reconcile(), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            Emit("ERROR", $"EnsureState lỗi: {ex.Message}");
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
                desired = _desiredActive;
                problem = _configProblem;
            }

            var nowTick = _tickCount();
            CheckLiveness(nowTick);
            RetrySecurityListIfStuck(nowTick);
            CheckStormRecovery(nowTick);

            int generation;
            bool loggedOn;
            bool stale;
            decimal? bid;
            decimal? ask;
            long lastTick;
            long sequence;
            ulong sendingMs;
            CTraderSymbolInfo? info = null;
            bool crossCheck;
            lock (_stateLock)
            {
                generation = _generation;
                loggedOn = _quoteLoggedOn;
                bid = _book?.Bid;
                ask = _book?.Ask;
                lastTick = _book?.LastQuoteTickCount ?? 0;
                sequence = _book?.QuoteSequence ?? 0;
                sendingMs = _book?.LastSendingTimeMs ?? 0;
                if (_catalog.TryGet(_symbolId, out var resolved))
                {
                    info = resolved;
                }

                crossCheck = _crossCheckPending;
                stale = _linkStale;
            }

            var now = _tickCount();
            if (generation != _readGeneration)
            {
                ResetReadState(generation);
            }

            var newTicks = Math.Max(0, sequence - _lastSequence);
            _lastSequence = sequence;

            string? reason = null;
            if (!desired)
            {
                reason = problem ?? "cTrader QUOTE chưa bật";
            }
            else if (!loggedOn)
            {
                reason = "FIX QUOTE chưa logon";
            }
            else if (stale)
            {
                reason = "FIX QUOTE không phản hồi TestRequest — fail-closed";
            }
            else if (info is null)
            {
                reason = "Chưa có SecurityList cho symbolId đã cấu hình";
            }
            else
            {
                var check = PointDigitsConsistencyChecker.Check(point, info.Digits);
                if (!check.IsConsistent)
                {
                    reason = "Lệch digits: " + check.Message;
                    ReportDigitsMismatch(point, info, check.Message);
                }
                else
                {
                    if (_mismatchReported is not null)
                    {
                        _mismatchReported = null;
                        Emit("INFO", $"Hết lệch digits: {check.Message}");
                    }

                    if (bid is not > 0m || ask is not > 0m)
                    {
                        reason = "Book rỗng";
                    }
                }
            }

            if (reason is not null)
            {
                if (desired)
                {
                    RecordWindow(now, connected: false, ageMs: 0, newTicks, confirmLatencyMs, exchangeA, null, null, point);
                }

                return Disconnected(info?.SymbolName, reason);
            }

            var age = Math.Max(0, now - lastTick);
            var tps = UpdateTps(now, newTicks);
            // LatencyMs của chân B là TUỔI TICK (now − lúc nhận báo giá cuối), nên nó TĂNG DẦN giữa hai tick
            // rồi về ~0 khi có tick mới — khác chân A (MMF) vốn là ĐỘ TRỄ TRUYỀN và giữ nguyên số cũ giữa hai
            // tick. Đây là ngữ nghĩa R8 đã quyết và là đầu vào của guard; đừng "sửa" cho giống chân A.
            //
            // Nhưng Avg/Max thì phải lấy mẫu THEO TICK, không theo mỗi lần đọc 50 ms: đọc 20 lần/giây mà lần nào
            // cũng cộng `age` thì Avg bị kéo theo nhịp poll chứ không phản ánh dữ liệu, và không so được với chân A.
            // Mẫu dùng ở đây là khoảng cách giữa hai tick — cũng chính là mức "ôi" nhất mà dữ liệu đạt tới.
            if (newTicks > 0)
            {
                if (_prevQuoteTick > 0)
                {
                    var interval = Math.Max(0, lastTick - _prevQuoteTick);
                    _lastInterval = interval;
                    _latSum += interval;
                    _latCount++;
                    if (_latCount == 1 || interval > _latMax)
                    {
                        _latMax = interval;
                    }
                }

                _prevQuoteTick = lastTick;
            }

            if (crossCheck && exchangeA.Bid is > 0m)
            {
                CrossCheckMagnitude(exchangeA.Bid.Value, bid!.Value);
            }

            RecordWindow(now, connected: true, age, newTicks, confirmLatencyMs, exchangeA, bid, ask, point);

            return new ExchangeMetrics(
                Symbol: info!.SymbolName,
                Bid: bid,
                Ask: ask,
                Spread: ask - bid,
                LatencyMs: age,
                Tps: tps,
                Time: FormatSendingTime(sendingMs),
                MaxLatMs: _latCount > 0 ? _latMax : null,
                AvgLatMs: _latCount > 0 ? _latSum / _latCount : null,
                IsConnected: true,
                Error: null,
                TickIntervalMs: _lastInterval);
        }
        catch (Exception ex)
        {
            return Disconnected(null, $"cTrader Read lỗi: {ex.Message}");
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

            bool tripped;
            lock (_stateLock)
            {
                tripped = _stormTripped;
            }

            if (want && config is not null && _transport is null && !tripped)
            {
                StartTransport(config);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Lỗi: {ex.Message}");
            Emit("ERROR", $"Reconcile lỗi: {ex.Message}");
            Raise(CTraderQuoteEventKind.Error, ex.Message);
        }
    }

    private void StartTransport(CTraderFixConfig config)
    {
        var transport = _transportFactory(config);
        int generation;
        lock (_stateLock)
        {
            generation = ++_generation;
            _quoteLoggedOn = false;
            _book = new CTraderQuoteBook(config.SymbolId, _tickCount);
            _catalog.Clear();
            _symbolId = config.SymbolId;
            _anomalyLogged = false;
            _anomalyCount = 0;
            _crossCheckPending = false;
            _subscriptionSent = false;
            _securityListRequestedTick = 0;
            _liveTransport = transport;
            _failedReconnects = 0;
            ResetLiveness(_tickCount());
            _status = "Đang kết nối QUOTE…";
        }

        transport.LoggedOn += role => OnLoggedOn(transport, generation, role);
        transport.LoggedOut += role => OnLoggedOut(generation, role);
        transport.MessageReceived += (role, message) => OnMessage(generation, role, message);

        _transport = transport;
        _activeConfig = config;

        Emit("INFO",
            $"QUOTE connecting host={config.Quote.Host} port={config.ActivePort(CTraderSessionRole.Quote)} " +
            $"ssl={(config.UseSsl ? "Y" : "N")} sender={config.SenderCompId} symbolId={config.SymbolId}");
        Raise(CTraderQuoteEventKind.Connecting, config.Quote.Host);
        transport.Start(CTraderSessionRole.Quote);
    }

    private void StopTransport(string reason)
    {
        var transport = _transport!;
        var symbolId = _activeConfig?.SymbolId ?? 0;
        _transport = null;
        _activeConfig = null;

        lock (_stateLock)
        {
            // Tăng generation TRƯỚC khi logout: LoggedOut do chính mình gây ra không bị coi là mất session.
            _generation++;
            _quoteLoggedOn = false;
            _book?.Clear();
            _book = null;
            _catalog.Clear();
            _liveTransport = null;
            ResetLiveness(0);
            _status = $"Đã dừng: {reason}";
        }

        try
        {
            if (transport.IsLoggedOn(CTraderSessionRole.Quote) && symbolId > 0)
            {
                // Unsubscribe PHẢI dùng lại đúng MDReqID đã subscribe.
                var sent = transport.Send(CTraderSessionRole.Quote, CTraderMessageFactory.MarketDataRequest(symbolId, subscribe: false));
                Emit("INFO", $"QUOTE unsubscribe symbolId={symbolId} sent={sent}");
            }
        }
        catch (Exception ex)
        {
            Emit("WARN", $"QUOTE unsubscribe lỗi: {ex.Message}");
        }

        try
        {
            transport.Stop(CTraderSessionRole.Quote);
        }
        catch (Exception ex)
        {
            Emit("WARN", $"QUOTE stop lỗi: {ex.Message}");
        }

        try
        {
            (transport as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Emit("WARN", $"QUOTE dispose lỗi: {ex.Message}");
        }

        Emit("INFO", $"QUOTE stopped ({reason})");
        Raise(CTraderQuoteEventKind.Stopped, reason);
    }

    private void OnLoggedOn(ICTraderFixTransport transport, int generation, CTraderSessionRole role)
    {
        if (role != CTraderSessionRole.Quote)
        {
            return;
        }

        int symbolId;
        int failedBeforeLogon;
        lock (_stateLock)
        {
            if (generation != _generation)
            {
                return;
            }

            _quoteLoggedOn = true;
            _quoteLoggedOnTick = _tickCount();
            _book?.Clear();
            _catalog.Clear();
            _crossCheckPending = true;
            _subscriptionSent = false;
            _securityListRequestedTick = _tickCount();
            ResetLiveness(_tickCount());
            symbolId = _symbolId;
            failedBeforeLogon = _failedReconnects;
            _failedReconnects = 0;
            _status = "QUOTE đã logon — chờ SecurityList";
        }

        Emit("INFO", failedBeforeLogon > 0 ? $"QUOTE logged on (sau {failedBeforeLogon} lần reconnect hỏng)" : "QUOTE logged on");
        Raise(CTraderQuoteEventKind.LoggedOn, "QUOTE logged on");

        try
        {
            // Re-issue mỗi lần logon (catalog vừa bị xoá). Kênh QUOTE theo Phase 0 câu 10.
            var securityReqId = "sec-" + _tickCount().ToString(CultureInfo.InvariantCulture);
            var securitySent = transport.Send(CTraderSessionRole.Quote, CTraderMessageFactory.SecurityListRequest(securityReqId, symbolId));
            Emit("INFO", $"QUOTE SecurityListRequest symbolId={symbolId} sent={securitySent} — subscribe giá sau khi nhận SecurityList");
        }
        catch (Exception ex)
        {
            Emit("ERROR", $"QUOTE gửi request sau logon lỗi: {ex.Message}");
        }
    }

    private void OnLoggedOut(int generation, CTraderSessionRole role)
    {
        if (role != CTraderSessionRole.Quote)
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

            // R9: xoá book NGAY — IsConnected=false ở lần đọc 50 ms kế tiếp, không phục vụ tick cuối.
            wasLoggedOn = _quoteLoggedOn;
            _quoteLoggedOn = false;
            _book?.Clear();
            _catalog.Clear();
            _status = "Mất QUOTE session — QuickFIX/n đang tự reconnect";
            failed = wasLoggedOn ? 0 : ++_failedReconnects;
        }

        // QuickFIX/n gọi OnLogout cả khi lần reconnect hỏng trước logon (đo trên live 2026-09-17: 5 lần trong 8 s lúc
        // mạng chập chờn). Chỉ lần mất phiên thật mới WARN + event; lần thử hỏng ghi lần đầu rồi gom số khi logon lại.
        if (wasLoggedOn)
        {
            Emit("WARN", "QUOTE logged out — book đã xoá, chờ reconnect");
            Raise(CTraderQuoteEventKind.LoggedOut, "QUOTE logged out");
        }
        else if (failed == 1)
        {
            Emit("INFO", "QUOTE reconnect chưa logon được — QuickFIX/n thử lại mỗi 2 s");
        }

        // CHỈ đếm lần mất phiên THẬT. Lần thử logon hỏng (wasLoggedOn=false) không được tính: sự cố live
        // 2026-09-23 05:36 — sàn reset phiên, TRADE gửi Logon 5 lần không ai trả lời, bị tính thành "bão"
        // rồi dừng hẳn. Vòng lặp P5-O1 vẫn bị chặn vì mỗi vòng ở đó đều logon xong mới rớt.
        if (wasLoggedOn)
        {
            TrackLogoutForStorm("QUOTE");
        }
    }

    private void OnMessage(int generation, CTraderSessionRole role, Message message)
    {
        if (role != CTraderSessionRole.Quote)
        {
            return;
        }

        var msgType = FixFieldReader.MsgType(message);
        var recovered = false;
        long? answerMs = null;
        var firstAnswer = false;
        lock (_stateLock)
        {
            if (generation != _generation)
            {
                return;
            }

            // Mọi message về (W, Heartbeat trả lời TestRequest, …) đều chứng minh đường truyền còn sống.
            var nowTick = _tickCount();
            if (msgType == "0" && _lastProbeId is not null
                && string.Equals(FixFieldReader.String(message, 112), _lastProbeId, StringComparison.Ordinal))
            {
                answerMs = nowTick - _lastProbeIdSentTick;
                _lastProbeId = null;
                _probesAnswered++;
                _probeAnswerMaxMs = Math.Max(_probeAnswerMaxMs, answerMs.Value);
                firstAnswer = !_probeAnswerLogged;
                _probeAnswerLogged = true;
            }

            _lastInboundTick = nowTick;
            _probeSentTick = 0;
            if (_linkStale)
            {
                _linkStale = false;
                recovered = true;
            }
        }

        if (firstAnswer)
        {
            Emit("INFO", $"QUOTE trả lời TestRequest sau {answerMs} ms (lần đầu trong phiên; các lần sau đếm trong STATS)");
        }

        if (recovered)
        {
            Emit("INFO", $"QUOTE phản hồi trở lại (35={msgType}) — hết fail-closed, chờ W để có giá");
        }

        switch (msgType)
        {
            case "W":
            case "X":
                string? anomaly = null;
                var firstAnomaly = false;
                lock (_stateLock)
                {
                    if (generation != _generation || _book is null)
                    {
                        return;
                    }

                    _book.Apply(message);
                    if (msgType == "X")
                    {
                        anomaly = _book.LastAnomaly;
                        _anomalyCount++;
                        firstAnomaly = !_anomalyLogged;
                        _anomalyLogged = true;
                    }
                }

                if (firstAnomaly)
                {
                    Emit("WARN", $"QUOTE nhận 35=X ở chế độ spot ({anomaly}) — book đã xoá, chờ W kế tiếp");
                }

                return;

            case "y":
                int count;
                CTraderSymbolInfo? info = null;
                int symbolId;
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
                        _status = $"QUOTE đang chạy — {resolved.SymbolName} digits={resolved.Digits}";
                    }
                    else
                    {
                        _status = $"SecurityList không có symbolId={symbolId}";
                    }
                }

                if (info is null)
                {
                    Emit("ERROR", $"SecurityList ({count} symbol) không có symbolId={symbolId} — sàn B disconnected");
                    return;
                }

                Emit("INFO", $"symbolName={info.SymbolName} symbolId={info.SymbolId} digits={info.Digits} (SecurityList {count} symbol)");
                Raise(CTraderQuoteEventKind.SymbolResolved, $"{info.SymbolName} digits={info.Digits}");
                SendSubscriptionIfNeeded();
                return;

            case "3":
            case "j":
            case "Y":
                Emit("WARN", $"QUOTE nhận 35={msgType} text={FixFieldReader.String(message, 58) ?? "(không có 58)"}");
                return;

            // Logout: xác nhận khi mình chủ động dừng, hoặc server đóng phiên (lý do nếu có nằm ở 58). Mất phiên thật
            // đã được báo WARN ở OnLoggedOut.
            case "5":
                Emit("INFO", $"QUOTE nhận 35=5 text={FixFieldReader.String(message, 58) ?? "(không có 58)"}");
                return;
        }
    }

    // Lớp B: subscribe đúng một lần, sau khi SecurityList về. Nếu SecurityList không về trong SecurityListTimeoutMs
    // thì hỏi lại (không subscribe mò) — thiếu digits là fail-closed theo R6 nên không có giá vẫn an toàn.
    public const long SecurityListTimeoutMs = 10_000;

    private void SendSubscriptionIfNeeded()
    {
        ICTraderFixTransport? transport = null;
        int symbolId;
        lock (_stateLock)
        {
            if (_subscriptionSent || !_quoteLoggedOn || _liveTransport is null || !_catalog.TryGet(_symbolId, out _))
            {
                return;
            }

            _subscriptionSent = true;
            transport = _liveTransport;
            symbolId = _symbolId;
        }

        try
        {
            var sent = transport.Send(CTraderSessionRole.Quote, CTraderMessageFactory.MarketDataRequest(symbolId, subscribe: true));
            Emit("INFO", $"QUOTE MarketDataRequest subscribe symbolId={symbolId} depth=spot sent={sent}");
        }
        catch (Exception ex)
        {
            Emit("ERROR", $"QUOTE gửi MarketDataRequest lỗi: {ex.Message}");
        }
    }

    private void RetrySecurityListIfStuck(long now)
    {
        ICTraderFixTransport? transport = null;
        int symbolId;
        lock (_stateLock)
        {
            if (_subscriptionSent || !_quoteLoggedOn || _liveTransport is null
                || _securityListRequestedTick == 0 || now - _securityListRequestedTick < SecurityListTimeoutMs)
            {
                return;
            }

            _securityListRequestedTick = now;
            transport = _liveTransport;
            symbolId = _symbolId;
        }

        try
        {
            var securityReqId = "sec-retry-" + now.ToString(CultureInfo.InvariantCulture);
            var sent = transport.Send(CTraderSessionRole.Quote, CTraderMessageFactory.SecurityListRequest(securityReqId, symbolId));
            Emit("WARN", $"Chưa nhận SecurityList sau {SecurityListTimeoutMs} ms — hỏi lại symbolId={symbolId} sent={sent} (chưa subscribe giá)");
        }
        catch (Exception ex)
        {
            Emit("ERROR", $"QUOTE hỏi lại SecurityList lỗi: {ex.Message}");
        }
    }

    // Gọi từ vòng poll 50 ms. Không chặn: Send của QuickFIX/n chỉ xếp message vào socket.
    private void CheckLiveness(long now)
    {
        ICTraderFixTransport? probeTransport = null;
        string? probeId = null;
        string? warning = null;
        lock (_stateLock)
        {
            if (!_quoteLoggedOn || _liveTransport is null)
            {
                return;
            }

            if (_probeSentTick != 0)
            {
                if (now - _probeSentTick < ProbeTimeoutMs)
                {
                    return;
                }

                if (!_linkStale)
                {
                    _linkStale = true;
                    _staleEvents++;
                    _book?.Clear();
                    _status = "FIX QUOTE không phản hồi — book đã xoá (fail-closed)";
                    warning = $"QUOTE im lặng {now - _lastInboundTick} ms, TestRequest không phản hồi sau {now - _probeSentTick} ms " +
                              "— book đã xoá (fail-closed), chờ phản hồi hoặc QuickFIX/n logout";
                }

                _probeSentTick = 0;
                _lastProbeTick = now;
            }
            else if (now - Math.Max(_lastInboundTick, _lastProbeTick) >= ProbeSilenceMs)
            {
                probeTransport = _liveTransport;
                probeId = "live-" + now.ToString(CultureInfo.InvariantCulture);
                _probeSentTick = now;
                _lastProbeTick = now;
                _lastProbeId = probeId;
                _lastProbeIdSentTick = now;
                _probesSent++;
            }
        }

        if (warning is not null)
        {
            Emit("WARN", warning);
        }

        if (probeTransport is null)
        {
            return;
        }

        try
        {
            probeTransport.Send(CTraderSessionRole.Quote, new QuickFix.FIX44.TestRequest(new QuickFix.Fields.TestReqID(probeId)));
        }
        catch (Exception ex)
        {
            Emit("WARN", $"QUOTE gửi TestRequest lỗi: {ex.Message}");
        }
    }

    // Gọi dưới _stateLock.
    private void ResetLiveness(long now)
    {
        _lastInboundTick = now;
        _probeSentTick = 0;
        _lastProbeTick = 0;
        _linkStale = false;
    }

    private void ReportDigitsMismatch(int point, CTraderSymbolInfo info, string message)
    {
        if (_mismatchReported == (point, info.Digits))
        {
            return;
        }

        _mismatchReported = (point, info.Digits);
        SetStatus($"FAIL-CLOSED lệch digits: {message}");
        Emit("ERROR", $"CTRADER_DIGITS_MISMATCH symbol={info.SymbolName} {message} — sàn B disconnected");
        Raise(CTraderQuoteEventKind.DigitsMismatch, $"{info.SymbolName}: {message}");
    }

    private void CrossCheckMagnitude(decimal bidA, decimal bidB)
    {
        lock (_stateLock)
        {
            _crossCheckPending = false;
        }

        var ratio = bidB / bidA;
        var suspicious = ratio > CrossCheckMaxRatio || ratio < 1m / CrossCheckMaxRatio;
        Emit(suspicious ? "WARN" : "INFO",
            $"Cross-check độ lớn giá: bidA={bidA.ToString(CultureInfo.InvariantCulture)} bidB={bidB.ToString(CultureInfo.InvariantCulture)} " +
            $"ratio={ratio.ToString("0.0000", CultureInfo.InvariantCulture)}{(suspicious ? " — LỆCH > 2 lần, nghi map nhầm symbol" : string.Empty)}");
    }

    // Generation mới = book mới (QuoteSequence bắt đầu từ 0) → mọi W đã nhận đều là tick mới.
    private void ResetReadState(int generation)
    {
        _readGeneration = generation;
        _lastSequence = 0;
        _tpsSecond = -1;
        _tpsCount = 0;
        _lastTps = null;
        _latSum = 0;
        _latCount = 0;
        _latMax = 0;
        _prevQuoteTick = 0;
        _lastInterval = null;
    }

    // Cùng ngữ nghĩa TpsAccumulator của reader MMF: trong giây hiện tại trả số tick đang đếm, sang giây mới trả số của giây trước.
    private float? UpdateTps(long nowMs, long newTicks)
    {
        if (newTicks <= 0)
        {
            return _lastTps;
        }

        var second = nowMs / 1000;
        if (_tpsSecond < 0)
        {
            _tpsSecond = second;
            _tpsCount = (int)newTicks;
            _lastTps = _tpsCount;
        }
        else if (second == _tpsSecond)
        {
            _tpsCount += (int)newTicks;
            _lastTps = _tpsCount;
        }
        else
        {
            _lastTps = _tpsCount;
            _tpsSecond = second;
            _tpsCount = (int)newTicks;
        }

        return _lastTps;
    }

    // R8: dữ liệu để chọn ctrader_confirm_latency_b — một dòng mỗi phút khi session được bật.
    private void RecordWindow(
        long now,
        bool connected,
        long ageMs,
        long newTicks,
        int confirmLatencyMs,
        ExchangeMetrics exchangeA,
        decimal? bidB,
        decimal? askB,
        int point)
    {
        if (_windowStartMs < 0)
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

        int anomalies;
        int staleEvents;
        string probes;
        lock (_stateLock)
        {
            anomalies = _anomalyCount;
            _anomalyCount = 0;
            staleEvents = _staleEvents;
            _staleEvents = 0;
            probes = $"probes_sent={_probesSent} probes_answered={_probesAnswered} probe_answer_max_ms={_probeAnswerMaxMs}";
            _probesSent = 0;
            _probesAnswered = 0;
            _probeAnswerMaxMs = 0;
        }

        var inv = CultureInfo.InvariantCulture;
        var line = $"[STATS] window_ms={now - _windowStartMs} reads_connected={_windowAges.Count} " +
                   $"reads_disconnected={_windowDisconnectedReads} ticks={_windowTicks} x_anomalies={anomalies} stale_events={staleEvents} {probes}";
        if (_windowAges.Count > 0)
        {
            _windowAges.Sort();
            var skipPct = 100m * _windowWouldSkip / _windowAges.Count;
            line += $" tick_age_ms min={_windowAges[0]} p50={Percentile(0.50)} p95={Percentile(0.95)} max={_windowAges[^1]}" +
                    $" confirm_latency_ms={confirmLatencyMs} would_skip_latency_b={_windowWouldSkip} ({skipPct.ToString("0.0", inv)}%)";
        }

        if (bidB is > 0m && askB is > 0m)
        {
            line += $" b_bid={bidB.Value.ToString(inv)} b_ask={askB.Value.ToString(inv)} b_spread_pts={((askB.Value - bidB.Value) * point).ToString("0.##", inv)}";
            if (exchangeA.Ask is > 0m)
            {
                line += $" a_ask={exchangeA.Ask.Value.ToString(inv)} gap_buy_pts={((bidB.Value - exchangeA.Ask.Value) * point).ToString("0.##", inv)} point={point}";
            }
        }

        Emit("INFO", line);

        _windowStartMs = now;
        _windowAges.Clear();
        _windowDisconnectedReads = 0;
        _windowTicks = 0;
        _windowWouldSkip = 0;
    }

    private long Percentile(double p)
    {
        var index = (int)Math.Ceiling(p * _windowAges.Count) - 1;
        return _windowAges[Math.Clamp(index, 0, _windowAges.Count - 1)];
    }

    // Đếm số lần mất phiên trong cửa sổ trượt; vượt ngưỡng thì dừng hẳn để không dồn logon lên broker.
    // Gọi từ thread của QuickFIX/n (OnLogout), kể cả các lần reconnect hỏng trước khi logon.
    private void TrackLogoutForStorm(string role)
    {
        var now = _tickCount();
        int count;
        int retriesLeft;
        lock (_stateLock)
        {
            if (_stormTripped)
            {
                return;
            }

            _logoutTimes.Enqueue(now);
            while (_logoutTimes.Count > 0 && now - _logoutTimes.Peek() > StormWindowMs)
            {
                _logoutTimes.Dequeue();
            }

            count = _logoutTimes.Count;
            if (count <= StormThreshold)
            {
                return;
            }

            _stormTripped = true;
            _stormTrippedAtTick = now;
            _lastDeadAlertTick = now;
            retriesLeft = Math.Max(0, StormMaxRetries - _stormRetries);
            _status = $"NGẮT MẠCH: {role} mất phiên {count} lần trong {StormWindowMs / 1000} s — đã dừng, "
                + (retriesLeft > 0 ? $"tự thử lại sau {StormRetryCooldownMs / 60_000} phút" : "cần bật lại tay");
        }

        var message = $"{role} mất phiên {count} lần trong {StormWindowMs / 1000} s — NGẮT MẠCH: dừng session. "
            + (retriesLeft > 0
                ? $"Sàn B fail-closed, tự thử lại sau {StormRetryCooldownMs / 60_000} phút (còn {retriesLeft} lần)."
                : "KHÔNG tự nối lại nữa. Sàn B fail-closed. Đổi platform_b (hoặc sửa config / mở lại app) để bật lại.");
        Emit("ERROR", message);
        Raise(CTraderQuoteEventKind.ReconnectStorm, message);

        lock (_desiredLock)
        {
            if (_disposed)
            {
                return;
            }

            _lifecycle = _lifecycle.ContinueWith(
                _ =>
                {
                    if (_transport is not null)
                    {
                        StopTransport("ngắt mạch: quá nhiều lần mất phiên trong một phút");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    // Gọi dưới _desiredLock khi trạng thái mong muốn đổi (can thiệp của người dùng).
    private void ResetStorm()
    {
        lock (_stateLock)
        {
            _stormTripped = false;
            _logoutTimes.Clear();
            _stormRetries = 0;
        }
    }

    // Gọi từ vòng poll 50 ms. Hai việc: (1) hết cooldown thì mở lại session đã bị ngắt mạch;
    // (2) khi đã hết lượt thử, nhắc lại định kỳ để sàn B chết không nằm im không ai biết.
    private void CheckStormRecovery(long now)
    {
        bool retry = false;
        bool remind = false;
        int attempt = 0;
        lock (_stateLock)
        {
            if (!_stormTripped)
            {
                // Chỉ coi là đã khỏi khi session ĐỨNG VỮNG đủ lâu. Logon lại rồi rớt ngay (đúng dạng bão)
                // không được xoá số lượt thử, nếu không "3 lần rồi dừng" sẽ không bao giờ tới.
                if (_quoteLoggedOn && now - _quoteLoggedOnTick >= StormRetryCooldownMs)
                {
                    _stormRetries = 0;
                }

                return;
            }

            if (_stormRetries < StormMaxRetries && now - _stormTrippedAtTick >= StormRetryCooldownMs)
            {
                _stormTripped = false;
                _logoutTimes.Clear();
                _stormRetries++;
                attempt = _stormRetries;
                _lastDeadAlertTick = now;
                retry = true;
            }
            else if (now - _lastDeadAlertTick >= DeadAlertIntervalMs)
            {
                _lastDeadAlertTick = now;
                remind = true;
            }
        }

        if (retry)
        {
            Emit("WARN", $"QUOTE thử nối lại sau ngắt mạch (lần {attempt}/{StormMaxRetries})");
            lock (_desiredLock)
            {
                if (_disposed)
                {
                    return;
                }

                _lifecycle = _lifecycle.ContinueWith(_ => Reconcile(), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default);
            }

            return;
        }

        if (remind)
        {
            var message = $"QUOTE vẫn đang NGẮT MẠCH sau {StormMaxRetries} lần thử — sàn B fail-closed, cần can thiệp tay.";
            Emit("ERROR", message);
            Raise(CTraderQuoteEventKind.ReconnectStorm, message);
        }
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
            handler(CTraderFixLogMasker.Apply($"[CTRADER][{level}] {message}"));
        }
        catch
        {
            // Log không được làm hỏng session hay vòng poll.
        }
    }

    private void Raise(CTraderQuoteEventKind kind, string message)
    {
        try
        {
            EventRaised?.Invoke(new CTraderQuoteSessionEvent(kind, message));
        }
        catch
        {
            // Subscriber lỗi không được làm hỏng session.
        }
    }

    private static string FormatSendingTime(ulong sendingMs)
    {
        if (sendingMs == 0)
        {
            return DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)sendingMs)
            .ToLocalTime()
            .ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    private static ExchangeMetrics Disconnected(string? symbol, string error)
        => new(
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
}
