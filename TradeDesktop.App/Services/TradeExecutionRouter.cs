using System.Diagnostics;
using System.Collections.Concurrent;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services;
using TradeDesktop.Application.Services.Portfolio;

namespace TradeDesktop.App.Services;

public sealed class TradeExecutionRouter : ITradeExecutionRouter
{
    private readonly ITradePlatformExecutor _mt4Executor;
    private readonly ITradePlatformExecutor _mt5Executor;
    private readonly ITradeSessionFileLogger _logger;
    private readonly IPortfolioCoordinator _portfolioCoordinator;
    private readonly IRuntimeConfigProvider _runtimeConfig;
    private readonly ITradesSharedMemoryReader _tradesSharedMemoryReader;
    private readonly SemaphoreSlim _physicalDispatchGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, byte> _consumedSignalIds = new();
    private const int SignalDispatchMaxAgeMs = 1000;

    public TradeExecutionRouter(
        IEnumerable<ITradePlatformExecutor> executors,
        ITradeSessionFileLogger logger,
        IPortfolioCoordinator portfolioCoordinator,
        IRuntimeConfigProvider runtimeConfig,
        ITradesSharedMemoryReader tradesSharedMemoryReader)
    {
        _mt4Executor = executors.First(x => x.Platform == TradeLegPlatform.Mt4);
        _mt5Executor = executors.First(x => x.Platform == TradeLegPlatform.Mt5);
        _logger = logger;
        _portfolioCoordinator = portfolioCoordinator;
        _runtimeConfig = runtimeConfig;
        _tradesSharedMemoryReader = tradesSharedMemoryReader;
    }

    public async Task<ManualTradeResult> OpenPairAsync(TradeOpenPairRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        ValidatePlatformOrThrow(request.LegA.Platform, request.LegA.Exchange);
        ValidatePlatformOrThrow(request.LegB.Platform, request.LegB.Exchange);

        var policy = ValidateOpenPolicy(request);
        if (!policy.Allowed)
        {
            return PolicyBlockedResult("OPEN", request.Context, policy.Code);
        }

        if (!TryReserveSignal(request.Context))
        {
            return PolicyBlockedResult("OPEN", request.Context, "SIGNAL_ALREADY_CONSUMED");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _physicalDispatchGate.WaitAsync(cancellationToken);
        }
        catch
        {
            ReleaseSignalReservation(request.Context);
            throw;
        }
        try
        {
            policy = ValidateOpenPolicy(request, logOppositeGuard: true);
            if (!policy.Allowed)
            {
                ReleaseSignalReservation(request.Context);
                return PolicyBlockedResult("OPEN", request.Context, policy.Code);
            }

            var gate = _portfolioCoordinator.TryAcquireTradeAction(
                DateTime.UtcNow,
                action: "OPEN",
                source: "TradeExecutionRouter.OpenPairAsync",
                origin: ResolveActionOrigin(request.Context.Reason),
                side: ResolveOpenSide(request),
                pairId: request.Context.PairId);
            if (!gate.Acquired)
            {
                ReleaseSignalReservation(request.Context);
                return BlockedResult("OPEN", gate);
            }
            LogPolicyAllowed(request.Context, "OPEN");

        SafeLog(
            "[ROUTER][INFO] Open pair request: " +
            $"legA={{platform={request.LegA.Platform},action={request.LegA.Action},hwnd={request.LegA.ChartHwnd},delay={request.LegA.DelayMs}ms}} " +
            $"legB={{platform={request.LegB.Platform},action={request.LegB.Action},hwnd={request.LegB.ChartHwnd},delay={request.LegB.DelayMs}ms}}");

        Debug.WriteLine($"[TradeRouter][Open] leg={request.LegA.Exchange}, platform={request.LegA.Platform}, action={request.LegA.Action}, chartHwnd={request.LegA.ChartHwnd}");
        Debug.WriteLine($"[TradeRouter][Open] leg={request.LegB.Exchange}, platform={request.LegB.Platform}, action={request.LegB.Action}, chartHwnd={request.LegB.ChartHwnd}");

            var result = await ExecuteOpenPairPerLegAsync(request, cancellationToken);
            stopwatch.Stop();
            var legA = result.Legs.FirstOrDefault(x => string.Equals(x.Exchange, "A", StringComparison.OrdinalIgnoreCase));
            var legB = result.Legs.FirstOrDefault(x => string.Equals(x.Exchange, "B", StringComparison.OrdinalIgnoreCase));
            SafeLog(
                $"[ROUTER][{(result.Success ? "INFO" : "WARN")}] Open pair result: success={result.Success} " +
                $"legA={{ok={legA?.Success},detail={legA?.Detail}}} " +
                $"legB={{ok={legB?.Success},detail={legB?.Detail}}} " +
                $"elapsed={stopwatch.ElapsedMilliseconds}ms");
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            SafeLog($"[ROUTER][ERROR] Open pair threw after {stopwatch.ElapsedMilliseconds}ms: {ex}");
            throw;
        }
        finally
        {
            _physicalDispatchGate.Release();
        }
    }

    public async Task<ManualTradeResult> ClosePairAsync(TradeClosePairRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var stopwatch = Stopwatch.StartNew();
        SafeLog(
            "[ROUTER][INFO] Close pair request: " +
            $"legA={{platform={request.LegA?.Platform},action={request.LegA?.Action},hwnd={request.LegA?.TradeHwnd},ticket={request.LegA?.Ticket},delay={request.LegA?.DelayMs}ms}} " +
            $"legB={{platform={request.LegB?.Platform},action={request.LegB?.Action},hwnd={request.LegB?.TradeHwnd},ticket={request.LegB?.Ticket},delay={request.LegB?.DelayMs}ms}}");

        var platformA = request.LegA?.Platform;
        var platformB = request.LegB?.Platform;

        if (platformA.HasValue)
        {
            ValidatePlatformOrThrow(platformA.Value, request.LegA!.Exchange);
            Debug.WriteLine($"[TradeRouter][Close] leg={request.LegA.Exchange}, platform={platformA.Value}, action={request.LegA.Action}, tradeHwnd={request.LegA.TradeHwnd}, ticket={request.LegA.Ticket}");
        }
        else
        {
            Debug.WriteLine("[TradeRouter][Close] leg=A, skipped (no close request)");
        }

        if (platformB.HasValue)
        {
            ValidatePlatformOrThrow(platformB.Value, request.LegB!.Exchange);
            Debug.WriteLine($"[TradeRouter][Close] leg={request.LegB.Exchange}, platform={platformB.Value}, action={request.LegB.Action}, tradeHwnd={request.LegB.TradeHwnd}, ticket={request.LegB.Ticket}");
        }
        else
        {
            Debug.WriteLine("[TradeRouter][Close] leg=B, skipped (no close request)");
        }

        var policy = ValidateClosePolicy(request);
        if (!policy.Allowed)
        {
            return PolicyBlockedResult("CLOSE", request.Context, policy.Code);
        }

        if (!TryReserveSignal(request.Context))
        {
            return PolicyBlockedResult("CLOSE", request.Context, "SIGNAL_ALREADY_CONSUMED");
        }

        try
        {
            await _physicalDispatchGate.WaitAsync(cancellationToken);
        }
        catch
        {
            ReleaseSignalReservation(request.Context);
            throw;
        }
        try
        {
            policy = ValidateClosePolicy(request);
            if (!policy.Allowed)
            {
                ReleaseSignalReservation(request.Context);
                return PolicyBlockedResult("CLOSE", request.Context, policy.Code);
            }

            if (!TryRefreshCloseRows(request, out request, out var refreshError))
            {
                ReleaseSignalReservation(request.Context);
                SafeLog($"[ROUTER][WARN] Close pair cancelled before dispatch: {refreshError}");
                return new ManualTradeResult(
                    Label: "CLOSE_STALE_TARGET",
                    Success: false,
                    Legs: [],
                    ErrorMessage: refreshError);
            }

            var gate = _portfolioCoordinator.TryAcquireTradeAction(
                DateTime.UtcNow,
                action: "CLOSE",
                source: "TradeExecutionRouter.ClosePairAsync",
                origin: ResolveActionOrigin(request.Context.Reason),
                side: ResolveCloseSide(request),
                pairId: request.Context.PairId);
            if (!gate.Acquired)
            {
                ReleaseSignalReservation(request.Context);
                return BlockedResult("CLOSE", gate);
            }
            LogPolicyAllowed(request.Context, "CLOSE");

            var result = await ExecuteClosePairPerLegAsync(request, cancellationToken);
            stopwatch.Stop();
            var legA = result.Legs.FirstOrDefault(x => string.Equals(x.Exchange, "A", StringComparison.OrdinalIgnoreCase));
            var legB = result.Legs.FirstOrDefault(x => string.Equals(x.Exchange, "B", StringComparison.OrdinalIgnoreCase));
            SafeLog(
                $"[ROUTER][{(result.Success ? "INFO" : "WARN")}] Close pair result: success={result.Success} " +
                $"legA={{ok={legA?.Success},detail={legA?.Detail}}} " +
                $"legB={{ok={legB?.Success},detail={legB?.Detail}}} " +
                $"elapsed={stopwatch.ElapsedMilliseconds}ms");
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            SafeLog($"[ROUTER][ERROR] Close pair threw after {stopwatch.ElapsedMilliseconds}ms: {ex}");
            throw;
        }
        finally
        {
            _physicalDispatchGate.Release();
        }
    }

    private bool TryRefreshCloseRows(
        TradeClosePairRequest source,
        out TradeClosePairRequest refreshed,
        out string error)
    {
        refreshed = source;
        error = string.Empty;

        if (!TryRefreshCloseLeg(source.LegA, out var legA, out error) ||
            !TryRefreshCloseLeg(source.LegB, out var legB, out error))
        {
            return false;
        }

        refreshed = source with { LegA = legA, LegB = legB };
        return true;
    }

    private bool TryRefreshCloseLeg(
        TradeCloseLegRequest? source,
        out TradeCloseLegRequest? refreshed,
        out string error)
    {
        refreshed = source;
        error = string.Empty;
        if (source is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(source.TradeMapName))
        {
            error = $"Close {source.Exchange} cancelled: missing trade map for ticket={source.Ticket}";
            return false;
        }

        var read = _tradesSharedMemoryReader.ReadTrades(source.TradeMapName);
        if (!read.IsMapAvailable || !read.IsParseSuccess)
        {
            error = $"Close {source.Exchange} cancelled: MMF unavailable/invalid map={source.TradeMapName} ticket={source.Ticket}";
            return false;
        }

        var rowIndex = -1;
        for (var index = 0; index < read.Records.Count; index++)
        {
            if (read.Records[index].Ticket == source.Ticket)
            {
                rowIndex = index;
                break;
            }
        }

        if (rowIndex < 0)
        {
            error = $"Close {source.Exchange} cancelled: ticket={source.Ticket} no longer exists in map={source.TradeMapName}";
            return false;
        }

        refreshed = source with { RowIndex = rowIndex };
        return true;
    }

    private static ManualTradeResult BlockedResult(string action, TradeActionGateResult gate)
    {
        var remainingMs = Math.Max(1, (int)Math.Ceiling(gate.Remaining.TotalMilliseconds));
        return new ManualTradeResult(
            Label: $"{action}_BLOCKED",
            Success: false,
            Legs: [],
            ErrorMessage: $"{gate.Reason}: còn {remainingMs}ms",
            BlockedByGlobalActionGate: true,
            GateRemainingMilliseconds: remainingMs,
            PolicyBlockCode: gate.Reason);
    }

    private static TradeActionOrigin ResolveActionOrigin(TradeExecutionReason reason)
        => reason switch
        {
            TradeExecutionReason.ManualOpen or TradeExecutionReason.ManualClose or TradeExecutionReason.ManualPairClose
                => TradeActionOrigin.Manual,
            TradeExecutionReason.OpenPartialRollback
                or TradeExecutionReason.ExternalPartialCloseRecovery
                or TradeExecutionReason.PendingCloseRetry
                => TradeActionOrigin.Recovery,
            _ => TradeActionOrigin.Auto
        };

    private static TradingPositionSide ResolveOpenSide(TradeOpenPairRequest request)
        => request.LegA.Action switch
        {
            TradeLegAction.Buy => TradingPositionSide.Buy,
            TradeLegAction.Sell => TradingPositionSide.Sell,
            _ => TradingPositionSide.None
        };

    private TradingPositionSide ResolveCloseSide(TradeClosePairRequest request)
        => string.IsNullOrWhiteSpace(request.Context.PairId)
            ? TradingPositionSide.None
            : _portfolioCoordinator.GetSlotByPairId(request.Context.PairId)?.Side
                ?? TradingPositionSide.None;

    private ManualTradeResult PolicyBlockedResult(
        string action,
        TradeExecutionContext context,
        string code)
    {
        SafeLog(
            $"[TRADE_POLICY][BLOCKED] requestId={context.RequestId} action={action} " +
            $"reason={context.Reason} source={context.Source} pairId={context.PairId ?? "-"} " +
            $"slotId={context.SlotId?.ToString() ?? "-"} code={code}");
        return new ManualTradeResult(
            Label: $"{action}_POLICY_BLOCKED",
            Success: false,
            Legs: [],
            ErrorMessage: $"TRADE_POLICY_BLOCKED: {code}",
            BlockedByExecutionPolicy: true,
            PolicyBlockCode: code);
    }

    private void LogPolicyAllowed(TradeExecutionContext context, string action)
    {
        var signalAgeMs = context.Signal is null
            ? "-"
            : Math.Max(0, (DateTime.UtcNow - context.Signal.CreatedAtUtc).TotalMilliseconds)
                .ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        SafeLog(
            $"[TRADE_POLICY][ALLOWED] requestId={context.RequestId} action={action} " +
            $"reason={context.Reason} source={context.Source} pairId={context.PairId ?? "-"} " +
            $"slotId={context.SlotId?.ToString() ?? "-"} signalId={context.Signal?.SignalId.ToString() ?? "-"} " +
            $"signalAgeMs={signalAgeMs} recoveryTicket={context.Recovery?.Ticket.ToString() ?? "-"}");
    }

    private (bool Allowed, string Code) ValidateOpenPolicy(TradeOpenPairRequest request, bool logOppositeGuard = false)
    {
        var context = request.Context;
        if (context.Reason == TradeExecutionReason.ManualOpen)
        {
            return (false, "MANUAL_WITHOUT_SIGNAL_DISABLED");
        }

        if (context.Reason != TradeExecutionReason.StrategicOpen)
        {
            return (false, "OPEN_REASON_NOT_ALLOWED");
        }

        var signalCheck = ValidateSignal(context, GapSignalAction.Open);
        if (!signalCheck.Allowed)
        {
            return signalCheck;
        }

        var signal = context.Signal!;
        var legsMatchSignal = signal.TriggerType switch
        {
            GapSignalTriggerType.OpenByGapBuy
                => request.LegA.Action == TradeLegAction.Buy
                   && request.LegB.Action == TradeLegAction.Sell,
            GapSignalTriggerType.OpenByGapSell
                => request.LegA.Action == TradeLegAction.Sell
                   && request.LegB.Action == TradeLegAction.Buy,
            _ => false
        };
        if (!legsMatchSignal)
        {
            return (false, "OPEN_LEGS_DO_NOT_MATCH_SIGNAL");
        }

        var metrics = _runtimeConfig.CurrentDashboardMetrics;
        if (metrics is null || !metrics.IsConnectedA || !metrics.IsConnectedB)
        {
            return (false, "LATEST_SNAPSHOT_UNAVAILABLE");
        }
        var marketSafety = ValidateLatestMarketSafety(metrics, _runtimeConfig.CurrentOpenPriceFreezeMs);
        if (!marketSafety.Allowed)
        {
            return marketSafety;
        }

        var oppositeGuard = ValidateOppositeOpenPriceGuard(request, metrics, logOppositeGuard);
        if (!oppositeGuard.Allowed)
        {
            return oppositeGuard;
        }

        // Ngưỡng mang dấu, khớp với GapSignalConfirmationEngine: ngưỡng dương giữ nguyên hành vi cũ
        // (max(a,b) == max(|a|,|b|) khi cả hai >= 0), ngưỡng âm nới về phía trong.
        var threshold = Math.Max(
            (long)_runtimeConfig.CurrentOpenPts,
            (long)_runtimeConfig.CurrentConfirmGapPts);
        var conditionValid = signal.TriggerType switch
        {
            GapSignalTriggerType.OpenByGapBuy => metrics.GapBuy is { } gap && gap >= threshold,
            GapSignalTriggerType.OpenByGapSell => metrics.GapSell is { } gap && gap <= -threshold,
            _ => false
        };
        if (!conditionValid)
        {
            return (false, "LATEST_OPEN_CONDITION_INVALID");
        }

        // Trần cho gap cuối (open_max_last_gap_pts), giữ lockstep với GapSignalConfirmationEngine.
        // null = tắt gate; 0 và số âm vẫn hiệu lực. Reason riêng để phân biệt với ngưỡng cũ.
        if (_runtimeConfig.CurrentOpenMaxLastGapPts is { } maxLastGap)
        {
            var withinCeiling = signal.TriggerType switch
            {
                GapSignalTriggerType.OpenByGapBuy => metrics.GapBuy is { } gap && gap < maxLastGap,
                GapSignalTriggerType.OpenByGapSell => metrics.GapSell is { } gap && gap > -maxLastGap,
                _ => false
            };
            if (!withinCeiling)
            {
                return (false, "LATEST_OPEN_MAX_LAST_GAP_EXCEEDED");
            }
        }

        var limitMaxGap = Math.Abs(_runtimeConfig.CurrentLimitMaxGap);
        var currentGap = signal.TriggerType == GapSignalTriggerType.OpenByGapBuy
            ? metrics.GapBuy
            : metrics.GapSell;
        if (limitMaxGap > 0 && currentGap.HasValue && Math.Abs(currentGap.Value) > limitMaxGap)
        {
            return (false, "LATEST_GAP_EXCEEDS_LIMIT");
        }

        return (true, "ALLOWED");
    }

    private (bool Allowed, string Code) ValidateOppositeOpenPriceGuard(
        TradeOpenPairRequest request,
        TradeDesktop.Domain.Models.DashboardMetrics metrics,
        bool shouldLog)
    {
        var requestedTradeType = request.LegA.Action == TradeLegAction.Buy ? 0 : 1;
        var eligibleTickets = _portfolioCoordinator.LiveSlots
            .Concat(_portfolioCoordinator.PendingCloseSlots)
            .Where(x => x.TicketA.HasValue)
            .Select(x => x.TicketA!.Value)
            .ToHashSet();
        var read = _tradesSharedMemoryReader.ReadTrades(_runtimeConfig.CurrentMapName1);
        OppositeOpenPriceGuardResult result;
        if (_runtimeConfig.CurrentOppositeOpenMinDistancePts > 0
            && eligibleTickets.Count > 0
            && (!read.IsMapAvailable || !read.IsParseSuccess))
        {
            result = OppositeOpenPriceGuardResult.Block(
                "OPEN_PRICE_A_MISSING",
                "Không có giá mở hợp lệ trên sàn A để tính giá trung bình");
        }
        else
        {
            result = OppositeOpenPriceGuard.Evaluate(
                read.Records,
                eligibleTickets,
                requestedTradeType,
                metrics.ExchangeA.Bid,
                metrics.ExchangeA.Ask,
                _runtimeConfig.CurrentPoint,
                _runtimeConfig.CurrentOppositeOpenMinDistancePts);
        }

        if (shouldLog)
        {
            var status = result.Skipped ? "SKIP" : result.Allowed ? "PASS" : "BLOCK";
            var direction = result.LastTradeType switch
            {
                0 when requestedTradeType == 1 => "BUY->SELL",
                1 when requestedTradeType == 0 => "SELL->BUY",
                _ => requestedTradeType == 0 ? "NONE->BUY" : "NONE->SELL"
            };
            SafeLog(
                $"[OPPOSITE_OPEN_GUARD][RECHECK_{status}] direction={direction} pairId={request.Context.PairId ?? "-"} " +
                $"avgOpenA={FormatGuardNumber(result.AverageOpenPriceA)} currentA={FormatGuardNumber(result.CurrentPriceA)} " +
                $"currentPriceType={result.CurrentPriceType ?? "-"} positionCount={result.PositionCount} " +
                $"distancePts={result.DistancePts?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"} " +
                $"requiredPts={result.RequiredPts} result={status} reasonCode={result.ReasonCode} " +
                $"reason=\"{result.ReasonVietnamese}\"");
        }

        return (result.Allowed, result.ReasonCode);
    }

    private static string FormatGuardNumber(double? value)
        => value?.ToString("F5", System.Globalization.CultureInfo.InvariantCulture) ?? "null";

    private (bool Allowed, string Code) ValidateClosePolicy(TradeClosePairRequest request)
    {
        var context = request.Context;
        if ((request.LegA is not null && request.LegA.Action != TradeLegAction.Close)
            || (request.LegB is not null && request.LegB.Action != TradeLegAction.Close))
        {
            return (false, "CLOSE_LEG_ACTION_INVALID");
        }
        if (context.Reason == TradeExecutionReason.ManualClose)
        {
            return (false, "MANUAL_WITHOUT_SIGNAL_DISABLED");
        }

        if (context.Reason == TradeExecutionReason.ManualPairClose)
        {
            return ValidateManualPairClose(request);
        }

        if (context.Reason is TradeExecutionReason.OpenPartialRollback
            or TradeExecutionReason.ExternalPartialCloseRecovery
            or TradeExecutionReason.PendingCloseRetry)
        {
            return ValidateRecovery(request);
        }

        if (context.Reason != TradeExecutionReason.StrategicClose)
        {
            return (false, "CLOSE_REASON_NOT_ALLOWED");
        }

        var signalCheck = ValidateSignal(context, GapSignalAction.Close);
        if (!signalCheck.Allowed)
        {
            return signalCheck;
        }

        if (string.IsNullOrWhiteSpace(context.PairId))
        {
            return (false, "STRATEGIC_CLOSE_PAIR_REQUIRED");
        }

        var slot = _portfolioCoordinator.GetSlotByPairId(context.PairId);
        if (slot is null || slot.Status is not (PositionSlotStatus.Live or PositionSlotStatus.PendingClose))
        {
            return (false, "STRATEGIC_CLOSE_SLOT_NOT_ACTIVE");
        }
        if (context.SlotId.HasValue && slot.SlotId != context.SlotId.Value)
        {
            return (false, "STRATEGIC_CLOSE_SLOT_MISMATCH");
        }
        if (!RequestTicketsMatchSlot(request, slot))
        {
            return (false, "STRATEGIC_CLOSE_TICKET_MISMATCH");
        }

        var signal = context.Signal!;
        var latestMetrics = _runtimeConfig.CurrentDashboardMetrics;
        if (latestMetrics is null || !latestMetrics.IsConnectedA || !latestMetrics.IsConnectedB)
        {
            return (false, "LATEST_SNAPSHOT_UNAVAILABLE");
        }
        var latestSafety = ValidateLatestMarketSafety(latestMetrics, _runtimeConfig.CurrentClosePriceFreezeMs);
        if (!latestSafety.Allowed)
        {
            return latestSafety;
        }

        if (signal.CloseReason == CloseSignalReason.Tp)
        {
            var target = Math.Abs(_runtimeConfig.CurrentCloseTpProfit);
            if (!slot.LastProfitSnapshot.HasValue || slot.LastProfitSnapshot.Value < target)
            {
                return (false, "LATEST_TP_CONDITION_INVALID");
            }
        }
        else
        {
            var resolvedGap = SosCloseConfigResolver.ResolveGapThresholds(
                slot.IsSosActive,
                _runtimeConfig.CurrentCloseConfirmGapPts,
                _runtimeConfig.CurrentClosePts,
                _runtimeConfig.CurrentSosCloseConfirmGapPts,
                _runtimeConfig.CurrentSosCloseGapPts);
            var signalUsesSos = signal.CloseGapMode == CloseGapMode.Sos;
            if (signalUsesSos != resolvedGap.UsesSos)
            {
                return (false, "LATEST_CLOSE_GAP_MODE_CHANGED");
            }

            var gapError = signalUsesSos
                ? SosCloseConfigResolver.ValidateLatestSosGap(
                    signal.TriggerType,
                    latestMetrics.GapBuy,
                    latestMetrics.GapSell,
                    resolvedGap.CloseGapPts,
                    _runtimeConfig.CurrentLimitMaxGap)
                : SosCloseConfigResolver.ValidateLatestGap(
                    signal.TriggerType,
                    latestMetrics.GapBuy,
                    latestMetrics.GapSell,
                    resolvedGap.ConfirmGapPts,
                    resolvedGap.CloseGapPts,
                    _runtimeConfig.CurrentLimitMaxGap);
            if (gapError is not null)
            {
                return (false, gapError);
            }
        }

        return (true, "ALLOWED");
    }

    private (bool Allowed, string Code) ValidateLatestMarketSafety(
        TradeDesktop.Domain.Models.DashboardMetrics metrics,
        int priceFreezeMs)
    {
        var timestampUtc = metrics.TimestampUtc.Kind switch
        {
            DateTimeKind.Utc => metrics.TimestampUtc,
            DateTimeKind.Local => metrics.TimestampUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(metrics.TimestampUtc, DateTimeKind.Utc)
        };
        if ((DateTime.UtcNow - timestampUtc).TotalSeconds > 10)
        {
            return (false, "LATEST_SNAPSHOT_STALE");
        }

        if (priceFreezeMs > 0)
        {
            var nowUtc = DateTime.UtcNow;
            if (!_runtimeConfig.LastQuoteChangedAtUtcA.HasValue
                || (nowUtc - _runtimeConfig.LastQuoteChangedAtUtcA.Value).TotalMilliseconds >= priceFreezeMs)
            {
                return (false, "LATEST_EXCHANGE_A_QUOTE_FROZEN");
            }

            if (!_runtimeConfig.LastQuoteChangedAtUtcB.HasValue
                || (nowUtc - _runtimeConfig.LastQuoteChangedAtUtcB.Value).TotalMilliseconds >= priceFreezeMs)
            {
                return (false, "LATEST_EXCHANGE_B_QUOTE_FROZEN");
            }
        }

        var maxLatency = Math.Max(0, _runtimeConfig.CurrentConfirmLatencyMs);
        if (maxLatency > 0
            && (metrics.ExchangeA.LatencyMs > maxLatency
                || metrics.ExchangeB.LatencyMs > maxLatency))
        {
            return (false, "LATEST_LATENCY_EXCEEDS_LIMIT");
        }

        var maxSpread = Math.Max(0, _runtimeConfig.CurrentMaxSpread);
        var point = Math.Max(1, _runtimeConfig.CurrentPoint);
        if (maxSpread > 0)
        {
            var spreadAPts = metrics.ExchangeA.Spread.HasValue
                ? (int)(metrics.ExchangeA.Spread.Value * point)
                : 0;
            var spreadBPts = metrics.ExchangeB.Spread.HasValue
                ? (int)(metrics.ExchangeB.Spread.Value * point)
                : 0;
            if (spreadAPts > maxSpread || spreadBPts > maxSpread)
            {
                return (false, "LATEST_SPREAD_EXCEEDS_LIMIT");
            }
        }

        return (true, "ALLOWED");
    }

    private (bool Allowed, string Code) ValidateSignal(
        TradeExecutionContext context,
        GapSignalAction requiredAction)
    {
        var signal = context.Signal;
        if (signal is null)
        {
            return (false, "SIGNAL_AUTHORIZATION_REQUIRED");
        }
        if (signal.SignalId == Guid.Empty || string.IsNullOrWhiteSpace(signal.SnapshotFingerprint))
        {
            return (false, "SIGNAL_AUTHORIZATION_INVALID");
        }
        if (signal.Action != requiredAction)
        {
            return (false, "SIGNAL_ACTION_MISMATCH");
        }
        if (!string.Equals(signal.PairId, context.PairId, StringComparison.Ordinal)
            || signal.SlotId != context.SlotId)
        {
            return (false, "SIGNAL_TARGET_MISMATCH");
        }

        var nowUtc = DateTime.UtcNow;
        var ageMs = (nowUtc - signal.CreatedAtUtc).TotalMilliseconds;
        if (ageMs < -1000 || ageMs > SignalDispatchMaxAgeMs || nowUtc > signal.ValidUntilUtc)
        {
            return (false, "SIGNAL_EXPIRED");
        }
        return (true, "ALLOWED");
    }

    private static (bool Allowed, string Code) ValidateRecovery(TradeClosePairRequest request)
    {
        var context = request.Context;
        var evidence = context.Recovery;
        if (evidence is null
            || string.IsNullOrWhiteSpace(evidence.PairId)
            || evidence.Ticket == 0
            || string.IsNullOrWhiteSpace(evidence.Evidence))
        {
            return (false, "RECOVERY_EVIDENCE_REQUIRED");
        }
        if (!string.Equals(context.PairId, evidence.PairId, StringComparison.Ordinal)
            || context.SlotId != evidence.SlotId)
        {
            return (false, "RECOVERY_TARGET_MISMATCH");
        }
        var requestTickets = new[] { request.LegA?.Ticket, request.LegB?.Ticket }
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToArray();
        if (requestTickets.Length != 1 || requestTickets[0] != evidence.Ticket)
        {
            return (false, "RECOVERY_TICKET_MISMATCH");
        }
        return (true, "ALLOWED");
    }

    private (bool Allowed, string Code) ValidateManualPairClose(TradeClosePairRequest request)
    {
        var context = request.Context;
        var slot = string.IsNullOrWhiteSpace(context.PairId)
            ? null
            : _portfolioCoordinator.GetSlotByPairId(context.PairId);
        var result = ManualPairClosePolicy.Validate(new ManualPairClosePolicyInput(
            Source: context.Source,
            ContextPairId: context.PairId,
            ContextSlotId: context.SlotId,
            HasLegA: request.LegA is not null,
            HasLegB: request.LegB is not null,
            TicketA: request.LegA?.Ticket ?? 0,
            TicketB: request.LegB?.Ticket ?? 0,
            SlotExists: slot is not null,
            SlotPairId: slot?.PairId,
            SlotId: slot?.SlotId,
            SlotTicketA: slot?.TicketA,
            SlotTicketB: slot?.TicketB,
            SlotStatus: slot?.Status ?? PositionSlotStatus.Closed,
            IsCloseExecutionPending: slot?.IsCloseExecutionPending ?? false,
            CloseOwner: slot?.CloseOwner ?? CloseExecutionOwner.None));
        return (result.Allowed, result.Code);
    }

    private static bool RequestTicketsMatchSlot(TradeClosePairRequest request, PositionSlot slot)
    {
        if (request.LegA is not null && slot.TicketA != request.LegA.Ticket)
        {
            return false;
        }
        if (request.LegB is not null && slot.TicketB != request.LegB.Ticket)
        {
            return false;
        }
        return request.LegA is not null || request.LegB is not null;
    }

    private bool TryReserveSignal(TradeExecutionContext context)
        => context.Signal is null || _consumedSignalIds.TryAdd(context.Signal.SignalId, 0);

    private void ReleaseSignalReservation(TradeExecutionContext context)
    {
        if (context.Signal is not null)
        {
            _consumedSignalIds.TryRemove(context.Signal.SignalId, out _);
        }
    }

    private static void ValidatePlatformOrThrow(TradeLegPlatform platform, string exchange)
    {
        if (platform is TradeLegPlatform.Mt4 or TradeLegPlatform.Mt5)
        {
            return;
        }

        throw new InvalidOperationException($"Invalid platform for exchange {exchange}: {platform}");
    }

    private async Task<ManualTradeResult> ExecuteOpenPairPerLegAsync(TradeOpenPairRequest request, CancellationToken cancellationToken)
    {
        var executorA = ResolveExecutor(request.LegA.Platform);
        var executorB = ResolveExecutor(request.LegB.Platform);

        Debug.WriteLine($"[TradeRouter][Open] executorA={executorA.GetType().Name}, executorB={executorB.GetType().Name}");

        var legATask = OpenLegWithDelayAsync(executorA, request.LegA, cancellationToken);
        var legBTask = OpenLegWithDelayAsync(executorB, request.LegB, cancellationToken);
        var legs = await Task.WhenAll(legATask, legBTask);

        return new ManualTradeResult(
            Label: "OPEN_MANUAL",
            Success: legs.All(x => x.Success),
            Legs: legs);
    }

    private static async Task<ManualTradeLegResult> OpenLegWithDelayAsync(
        ITradePlatformExecutor executor,
        TradeOpenLegRequest request,
        CancellationToken cancellationToken)
    {
        var delayMs = Math.Max(0, request.DelayMs);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }

        return await executor.OpenLegAsync(request, cancellationToken);
    }

    private async Task<ManualTradeResult> ExecuteClosePairPerLegAsync(TradeClosePairRequest request, CancellationToken cancellationToken)
    {
        var tasks = new List<Task<ManualTradeLegResult>>(capacity: 2);

        if (request.LegA is not null)
        {
            var executorA = ResolveExecutor(request.LegA.Platform);
            Debug.WriteLine($"[TradeRouter][Close] executorA={executorA.GetType().Name}");
            tasks.Add(CloseLegWithDelayAsync(executorA, request.LegA, cancellationToken));
        }

        if (request.LegB is not null)
        {
            var executorB = ResolveExecutor(request.LegB.Platform);
            Debug.WriteLine($"[TradeRouter][Close] executorB={executorB.GetType().Name}");
            tasks.Add(CloseLegWithDelayAsync(executorB, request.LegB, cancellationToken));
        }

        if (tasks.Count == 0)
        {
            return new ManualTradeResult(
                Label: "CLOSE_MANUAL",
                Success: true,
                Legs: []);
        }

        var legs = await Task.WhenAll(tasks);
        return new ManualTradeResult(
            Label: "CLOSE_MANUAL",
            Success: legs.All(x => x.Success),
            Legs: legs);
    }

    private static async Task<ManualTradeLegResult> CloseLegWithDelayAsync(
        ITradePlatformExecutor executor,
        TradeCloseLegRequest request,
        CancellationToken cancellationToken)
    {
        var delayMs = Math.Max(0, request.DelayMs);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }

        return await executor.CloseLegAsync(request, cancellationToken);
    }

    private ITradePlatformExecutor ResolveExecutor(TradeLegPlatform platform)
    {
        return platform switch
        {
            TradeLegPlatform.Mt4 => _mt4Executor,
            TradeLegPlatform.Mt5 => _mt5Executor,
            _ => throw new InvalidOperationException($"Unsupported platform: {platform}")
        };
    }

    private void SafeLog(string message)
    {
        try
        {
            _logger.Log(message);
        }
        catch
        {
            // ignored by design
        }
    }
}
