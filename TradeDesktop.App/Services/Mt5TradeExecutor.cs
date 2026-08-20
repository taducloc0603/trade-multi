using System.Diagnostics;
using TradeDesktop.Infrastructure.Mt5Bridge;

namespace TradeDesktop.App.Services;

public sealed class Mt5TradeExecutor : ITradePlatformExecutor
{
    private readonly IMt5BridgeTransportProvider _transportProvider;
    private readonly Mt5BridgeOptions _options;
    private readonly ITradeSessionFileLogger _logger;
    private readonly SemaphoreSlim _actionGateA = new(1, 1);
    private readonly SemaphoreSlim _actionGateB = new(1, 1);
    private readonly SemaphoreSlim _manualUiGate = new(1, 1);

    public Mt5TradeExecutor(
        IMt5BridgeTransportProvider transportProvider,
        Mt5BridgeOptions options,
        ITradeSessionFileLogger logger)
    {
        _transportProvider = transportProvider;
        _options = options;
        _logger = logger;
    }

    public TradeLegPlatform Platform => TradeLegPlatform.Mt5;

    public async Task<ManualTradeLegResult> OpenLegAsync(
        TradeOpenLegRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var action = request.Action.ToString().ToUpperInvariant();
        if (request.Action is not (TradeLegAction.Buy or TradeLegAction.Sell))
        {
            return Failure(request.Exchange, action, "invalid", "Unsupported MT5 open action");
        }

        var endpoint = _options.ResolveEndpoint(request.Exchange);
        var symbol = (request.Symbol ?? endpoint.Symbol).Trim();
        var volume = request.Volume ?? endpoint.Volume;
        if (string.IsNullOrWhiteSpace(symbol) || volume <= 0)
        {
            return Failure(
                request.Exchange,
                action,
                "invalid",
                $"MT5 Bridge config missing for exchange {request.Exchange}: symbol/volume");
        }

        var actionGate = ResolveActionGate(request.Exchange);
        await actionGate.WaitAsync(cancellationToken);
        var manualUiGateHeld = false;
        var uiQueuedAt = Stopwatch.GetTimestamp();
        try
        {
            // Both MT5 terminals share the Windows foreground, keyboard and
            // clipboard. Manual UI actions must therefore be serialized.
            await _manualUiGate.WaitAsync(cancellationToken);
            manualUiGateHeld = true;
            var uiAcquiredAt = Stopwatch.GetTimestamp();
            var queueWaitMs = (long)Stopwatch.GetElapsedTime(uiQueuedAt, uiAcquiredAt).TotalMilliseconds;
            var transport = _transportProvider.Resolve(request.Exchange);
            var healthError = await EnsureReadyAsync(
                transport, endpoint, requireManualUi: true, cancellationToken);
            if (healthError is not null)
            {
                return Failure(request.Exchange, action, "offline", healthError);
            }

            var now = Environment.TickCount64;
            var requestId = Guid.NewGuid().ToString("N");
            var command = new Mt5BridgeOpenCommand
            {
                RequestId = requestId,
                Account = endpoint.Account,
                Symbol = symbol,
                Side = action,
                Volume = volume,
                CreatedMilliseconds = now,
                ExpiresMilliseconds = now + (long)_options.ExecutionTimeout.TotalMilliseconds
            };

            SafeLog($"[MT5_BRIDGE][OPEN][SEND] exchange={request.Exchange} requestId={requestId} symbol={symbol} side={action} volume={volume} queueWaitMs={queueWaitMs}");
            var dispatched = new TaskCompletionSource<long>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var responseTask = transport.SendAsync(
                command,
                requestId,
                _options.ExecutionTimeout,
                cancellationToken,
                progress =>
                {
                    if (progress.Status == Mt5BridgeExecutionStatuses.Dispatched)
                    {
                        dispatched.TrySetResult(Stopwatch.GetTimestamp());
                    }
                });

            var firstCompleted = await Task.WhenAny(responseTask, dispatched.Task);
            if (firstCompleted == dispatched.Task)
            {
                var dispatchedAt = await dispatched.Task;
                var uiDispatchMs = (long)Stopwatch
                    .GetElapsedTime(uiAcquiredAt, dispatchedAt)
                    .TotalMilliseconds;
                _manualUiGate.Release();
                manualUiGateHeld = false;
                SafeLog(
                    $"[MT5_BRIDGE][OPEN][DISPATCHED] exchange={request.Exchange} " +
                    $"requestId={requestId} queueWaitMs={queueWaitMs} uiDispatchMs={uiDispatchMs}");
            }

            var response = await responseTask;
            if (dispatched.Task.IsCompletedSuccessfully)
            {
                var dispatchedAt = await dispatched.Task;
                var brokerConfirmMs = (long)Stopwatch
                    .GetElapsedTime(dispatchedAt)
                    .TotalMilliseconds;
                var totalExecutionMs = (long)Stopwatch
                    .GetElapsedTime(uiQueuedAt)
                    .TotalMilliseconds;
                SafeLog(
                    $"[MT5_BRIDGE][OPEN][TIMING] exchange={request.Exchange} " +
                    $"requestId={requestId} queueWaitMs={queueWaitMs} " +
                    $"brokerConfirmMs={brokerConfirmMs} totalExecutionMs={totalExecutionMs}");
            }
            return MapResponse(request.Exchange, action, response);
        }
        catch (TimeoutException ex)
        {
            SafeLog($"[MT5_BRIDGE][OPEN][TIMEOUT] exchange={request.Exchange} detail={ex.Message}");
            return Failure(request.Exchange, action, Mt5BridgeExecutionStatuses.Timeout, "MT5_BRIDGE_TIMEOUT");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SafeLog($"[MT5_BRIDGE][OPEN][ERROR] exchange={request.Exchange} error={ex}");
            return Failure(request.Exchange, action, Mt5BridgeExecutionStatuses.Unknown, ex.Message);
        }
        finally
        {
            if (manualUiGateHeld)
            {
                _manualUiGate.Release();
            }
            actionGate.Release();
        }
    }

    public async Task<ManualTradeLegResult> CloseLegAsync(
        TradeCloseLegRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var endpoint = _options.ResolveEndpoint(request.Exchange);
        var symbol = (request.Symbol ?? endpoint.Symbol).Trim();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return Failure(request.Exchange, "CLOSE", "invalid",
                $"MT5 Bridge config missing for exchange {request.Exchange}: symbol");
        }

        var actionGate = ResolveActionGate(request.Exchange);
        await actionGate.WaitAsync(cancellationToken);
        try
        {
            var transport = _transportProvider.Resolve(request.Exchange);
            var healthError = await EnsureReadyAsync(
                transport, endpoint, requireManualUi: false, cancellationToken);
            if (healthError is not null)
            {
                return Failure(request.Exchange, "CLOSE", "offline", healthError, request.Ticket);
            }

            var now = Environment.TickCount64;
            var requestId = Guid.NewGuid().ToString("N");
            var command = new Mt5BridgeCloseCommand
            {
                RequestId = requestId,
                Account = endpoint.Account,
                Ticket = request.Ticket,
                Symbol = symbol,
                Volume = request.Volume ?? 0,
                CreatedMilliseconds = now,
                ExpiresMilliseconds = now + (long)_options.ExecutionTimeout.TotalMilliseconds
            };

            SafeLog($"[MT5_BRIDGE][CLOSE][SEND] exchange={request.Exchange} requestId={requestId} ticket={request.Ticket} symbol={symbol}");
            var response = await transport.SendAsync(
                command,
                requestId,
                _options.ExecutionTimeout,
                cancellationToken);
            return MapResponse(request.Exchange, "CLOSE", response);
        }
        catch (TimeoutException ex)
        {
            SafeLog($"[MT5_BRIDGE][CLOSE][TIMEOUT] exchange={request.Exchange} ticket={request.Ticket} detail={ex.Message}");
            return Failure(request.Exchange, "CLOSE", Mt5BridgeExecutionStatuses.Timeout,
                "MT5_BRIDGE_TIMEOUT", request.Ticket);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SafeLog($"[MT5_BRIDGE][CLOSE][ERROR] exchange={request.Exchange} ticket={request.Ticket} error={ex}");
            return Failure(request.Exchange, "CLOSE", Mt5BridgeExecutionStatuses.Unknown,
                ex.Message, request.Ticket);
        }
        finally
        {
            actionGate.Release();
        }
    }

    public Task<ManualTradeResult> OpenPairAsync(
        TradeOpenPairRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecutePairAsync(
            "OPEN_MANUAL",
            () => OpenLegAsync(request.LegA, cancellationToken),
            () => OpenLegAsync(request.LegB, cancellationToken));
    }

    public Task<ManualTradeResult> ClosePairAsync(
        TradeClosePairRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecutePairAsync(
            "CLOSE_MANUAL",
            () => request.LegA is null
                ? Task.FromResult(new ManualTradeLegResult("A", "CLOSE", true, "Close A skipped: no open trade"))
                : CloseLegAsync(request.LegA, cancellationToken),
            () => request.LegB is null
                ? Task.FromResult(new ManualTradeLegResult("B", "CLOSE", true, "Close B skipped: no open trade"))
                : CloseLegAsync(request.LegB, cancellationToken));
    }

    private async Task<string?> EnsureReadyAsync(
        IMt5BridgeTransport transport,
        Mt5BridgeEndpointOptions endpoint,
        bool requireManualUi,
        CancellationToken cancellationToken)
    {
        if (endpoint.Account <= 0)
        {
            return "MT5 Bridge account is not configured";
        }

        var health = transport.Health;
        if (!health.IsReady)
        {
            try
            {
                await transport.PingAsync(_options.AckTimeout, cancellationToken);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                return $"MT5 Bridge is offline: {ex.Message}";
            }
            health = transport.Health;
        }

        if (!health.IsReady)
        {
            return "MT5 Bridge heartbeat is stale";
        }
        if (health.Account != endpoint.Account)
        {
            return $"MT5 Bridge account mismatch: expected={endpoint.Account}, actual={health.Account}";
        }
        if (health.GapCount > 0)
        {
            return $"MT5 Bridge shared-memory gap detected: {health.GapCount}";
        }
        if (requireManualUi && health.ManualUiReady is not true)
        {
            return $"MT5 manual UI is not ready: {health.ManualUiCode ?? "not_reported"}";
        }
        return null;
    }

    private ManualTradeLegResult MapResponse(
        string exchange,
        string action,
        Mt5BridgeInboundMessage response)
    {
        var success = response.Status is
            Mt5BridgeExecutionStatuses.Confirmed or Mt5BridgeExecutionStatuses.AlreadyClosed;
        var detail = response.Detail ?? response.Status ?? "unknown";
        SafeLog(
            $"[MT5_BRIDGE][{action}][{(success ? "CONFIRMED" : "FAILED")}] exchange={exchange} " +
            $"status={response.Status} ticket={response.Ticket} deal={response.Deal} " +
            $"price={response.Price} volume={response.Volume} retcode={response.Retcode} detail={detail}");
        return new ManualTradeLegResult(
            exchange,
            action,
            success,
            detail,
            response.Ticket,
            response.Deal,
            response.Price,
            response.Volume,
            response.Retcode,
            response.Status);
    }

    private static ManualTradeLegResult Failure(
        string exchange,
        string action,
        string status,
        string detail,
        ulong? ticket = null) =>
        new(exchange, action, false, detail, ticket, ExecutionStatus: status);

    private SemaphoreSlim ResolveActionGate(string exchange) => exchange.Trim().ToUpperInvariant() switch
    {
        "A" => _actionGateA,
        "B" => _actionGateB,
        _ => throw new ArgumentOutOfRangeException(nameof(exchange), exchange, "Exchange must be A or B.")
    };

    private static async Task<ManualTradeResult> ExecutePairAsync(
        string label,
        Func<Task<ManualTradeLegResult>> legATaskFactory,
        Func<Task<ManualTradeLegResult>> legBTaskFactory)
    {
        var legs = await Task.WhenAll(legATaskFactory(), legBTaskFactory());
        return new ManualTradeResult(label, legs.All(x => x.Success), legs);
    }

    private void SafeLog(string message)
    {
        try
        {
            _logger.Log(message);
        }
        catch
        {
            // Logging must never change execution outcome.
        }
    }
}
