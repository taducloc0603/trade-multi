using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Infrastructure.Supabase;

public sealed class SupabaseConfigRepository(HttpClient httpClient, string? supabaseUrl, string? supabaseKey) : IConfigRepository
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly string? _supabaseUrl = supabaseUrl?.TrimEnd('/');
    private readonly string? _supabaseKey = supabaseKey;

    public async Task<ConfigRecord?> GetByHostNameAsync(string hostName, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured())
        {
            throw new InvalidOperationException("Thiếu SUPABASE_URL hoặc SUPABASE_KEY/SUPABASE_ANON_KEY.");
        }

        if (string.IsNullOrWhiteSpace(hostName))
        {
            return null;
        }

        var normalizedHostName = NormalizeHostName(hostName);

        var row = await GetFirstByColumnAsync("hostname", normalizedHostName, cancellationToken)
                  ?? await GetFirstByColumnLikeAsync("hostname", normalizedHostName, cancellationToken);

        if (row is null)
        {
            return null;
        }

        var sansJson = row.Sans.ValueKind is JsonValueKind.Array or JsonValueKind.Object
            ? row.Sans.GetRawText()
            : "[]";

        return new ConfigRecord(
            Id: string.IsNullOrWhiteSpace(row.Id) ? string.Empty : row.Id,
            SansJson: sansJson,
            HostName: row.HostName,
            PlatformA: NormalizePlatform(row.PlatformA),
            PlatformB: NormalizePlatform(row.PlatformB),
            Point: row.Point > 0 ? row.Point : 1,
            OpenPts: row.OpenPts,
            ConfirmGapPts: row.ConfirmGapPts,
            HoldConfirmMs: row.HoldConfirmMs,
            // Price-freeze là cột độc lập: KHÔNG fallback về hold-time.
            OpenPriceFreezeMs: Math.Max(0, row.OpenPriceFreezeMs),
            ClosePts: row.ClosePts,
            CloseConfirmGapPts: row.CloseConfirmGapPts,
            CloseTpProfit: row.CloseTpProfit,
            CloseConfirmTpProfit: row.CloseConfirmTpProfit,
            CloseMaxTpProfit: row.CloseMaxTpProfit,
            LimitMaxTp: row.LimitMaxTp,
            SosTriggerAOpenDistancePts: row.SosTriggerAOpenDistancePts,
            SosTriggerAfterSeconds: row.SosTriggerAfterSeconds,
            SosCloseConfirmGapPts: row.SosCloseConfirmGapPts,
            SosCloseGapPts: row.SosCloseGapPts,
            CloseHoldConfirmMs: row.CloseHoldConfirmMs,
            // Price-freeze là cột độc lập: KHÔNG fallback về hold-time.
            ClosePriceFreezeMs: Math.Max(0, row.ClosePriceFreezeMs),
            StartTimeHold: row.StartTimeHold,
            EndTimeHold: row.EndTimeHold,
            ConfirmLatencyMs: row.ConfirmLatencyMs,
            MaxGap: row.MaxGap,
            LimitMaxGap: row.LimitMaxGap,
            MaxSpread: row.MaxSpread,
            OpenMaxTimesTick: row.OpenMaxTimesTick,
            CloseMaxTimesTick: row.CloseMaxTimesTick,
            OpenPendingTimeMs: row.OpenPendingTimeMs,
            ClosePendingTimeMs: row.ClosePendingTimeMs,
            DelayOpenAMs: row.DelayOpenAMs,
            DelayOpenBMs: row.DelayOpenBMs,
            DelayCloseAMs: row.DelayCloseAMs,
            DelayCloseBMs: row.DelayCloseBMs,
            OpenNumberOfQualifyingTimes: row.OpenNumberOfQualifyingTimes,
            CloseNumberOfQualifyingTimes: row.CloseNumberOfQualifyingTimes,
            IsShowConfig: row.IsShowConfig,
            CurrentTickA: row.CurrentTickA ?? string.Empty,
            CurrentTickB: row.CurrentTickB ?? string.Empty,
            CurrentSlots: row.CurrentSlotsJson,
            MaxLifeTimeBySecond: row.MaxLifeTimeBySecond,
            MinProfitToClose: row.MinProfitToClose,
            MinBuyOpens: row.MinBuyOpens,
            MaxBuyOpens: row.MaxBuyOpens,
            MinSellOpens: row.MinSellOpens,
            MaxSellOpens: row.MaxSellOpens,
            MaxTotalOpens: row.MaxTotalOpens,
            OppositeSideLockSeconds: row.OppositeSideLockSeconds,
            OppositeOpenMinDistancePts: row.OppositeOpenMinDistancePts,
            RdStartSameActionLockSeconds: row.RdStartSameActionLockSeconds,
            RdEndSameActionLockSeconds: row.RdEndSameActionLockSeconds,
            RdStartPostCloseLockSeconds: row.RdStartPostCloseLockSeconds,
            RdEndPostCloseLockSeconds: row.RdEndPostCloseLockSeconds,
            RdStartPostOpenLockSeconds: row.RdStartPostOpenLockSeconds,
            RdEndPostOpenLockSeconds: row.RdEndPostOpenLockSeconds,
            ScheduleSleepingJson: row.ScheduleSleepingJson,
            SignalCycleSize: row.SignalCycleSize,
            OpenGapStability: new GapStabilityConfig(
                row.OpenGapAbsoluteFloor,
                row.OpenGapRelativeTolerance,
                row.OpenGapMadMultiplier,
                row.OpenGapMinStableSamples,
                row.OpenGapMaxDispersion,
                row.OpenGapMaxDrift),
            CloseGapStability: new GapStabilityConfig(
                row.CloseGapAbsoluteFloor,
                row.CloseGapRelativeTolerance,
                row.CloseGapMadMultiplier,
                row.CloseGapMinStableSamples,
                row.CloseGapMaxDispersion,
                row.CloseGapMaxDrift));
    }

    public async Task<bool> UpdateCurrentTicksAsync(
        string hostName,
        string currentTickA,
        string currentTickB,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured())
        {
            throw new InvalidOperationException("Thiếu SUPABASE_URL hoặc SUPABASE_KEY/SUPABASE_ANON_KEY.");
        }

        if (string.IsNullOrWhiteSpace(hostName))
        {
            return false;
        }

        var normalizedHostName = NormalizeHostName(hostName);

        var payload = JsonSerializer.Serialize(new
        {
            current_tick_a = currentTickA ?? string.Empty,
            current_tick_b = currentTickB ?? string.Empty
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{_supabaseUrl}/rest/v1/configs?hostname=eq.{Uri.EscapeDataString(normalizedHostName)}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        AddAuthHeaders(request);
        request.Headers.TryAddWithoutValidation("Prefer", "return=representation");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Supabase UpdateCurrentTicksAsync thất bại. Status={(int)response.StatusCode}, Body={errorBody}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0;
    }

    public async Task<bool> UpdateCurrentSlotsAsync(
        string hostName,
        string currentSlotsJson,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured())
        {
            throw new InvalidOperationException("Thiếu SUPABASE_URL hoặc SUPABASE_KEY/SUPABASE_ANON_KEY.");
        }

        if (string.IsNullOrWhiteSpace(hostName))
        {
            return false;
        }

        var normalizedHostName = NormalizeHostName(hostName);
        using var slotsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(currentSlotsJson) ? "[]" : currentSlotsJson);

        var payload = JsonSerializer.Serialize(new
        {
            current_slots = slotsDoc.RootElement
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{_supabaseUrl}/rest/v1/configs?hostname=eq.{Uri.EscapeDataString(normalizedHostName)}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        AddAuthHeaders(request);
        request.Headers.TryAddWithoutValidation("Prefer", "return=representation");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Supabase UpdateCurrentSlotsAsync thất bại. Status={(int)response.StatusCode}, Body={errorBody}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0;
    }

    public async Task<bool> UpdateSansAndHostNameByHostNameAsync(
        string hostName,
        string sansJson,
        string platformA,
        string platformB,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured())
        {
            throw new InvalidOperationException("Thiếu SUPABASE_URL hoặc SUPABASE_KEY/SUPABASE_ANON_KEY.");
        }

        if (string.IsNullOrWhiteSpace(hostName))
        {
            return false;
        }

        var normalizedHostName = NormalizeHostName(hostName);

        return await UpdateByColumnAsync(
            "hostname",
            normalizedHostName,
            sansJson,
            normalizedHostName,
            platformA,
            platformB,
            cancellationToken);
    }

    private static string NormalizeHostName(string hostName) => hostName.Trim().ToLower();
    private static string NormalizePlatform(string? platform)
    {
        var normalized = (platform ?? string.Empty).Trim().ToLower();
        return normalized is "mt4" or "mt5" ? normalized : "mt5";
    }

    private bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(_supabaseUrl) &&
        !string.IsNullOrWhiteSpace(_supabaseKey);

    private void AddAuthHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("apikey", _supabaseKey);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_supabaseKey}");
    }

    private async Task<ConfigRow?> GetFirstByColumnAsync(string columnName, string value, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_supabaseUrl}/rest/v1/configs?select=*&{columnName}=eq.{Uri.EscapeDataString(value)}&limit=1");

        AddAuthHeaders(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Supabase GetFirstByColumnAsync thất bại. Column={columnName}, Status={(int)response.StatusCode}, Body={errorBody}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var first = doc.RootElement[0];
        if (first.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        first.TryGetProperty("id", out var idElement);
        first.TryGetProperty("sans", out var sansElement);
        first.TryGetProperty("point", out var pointElement);
        first.TryGetProperty("open_pts", out var openPtsElement);
        first.TryGetProperty("open_confirm_gap_pts", out var confirmGapPtsElement);
        first.TryGetProperty("open_hold_confirm_ms", out var holdConfirmMsElement);
        first.TryGetProperty("open_price_freeze_ms", out var openPriceFreezeMsElement);
        first.TryGetProperty("close_pts", out var closePtsElement);
        first.TryGetProperty("close_confirm_gap_pts", out var closeConfirmGapPtsElement);
        first.TryGetProperty("close_tp_profit", out var closeTpProfitElement);
        first.TryGetProperty("close_confirm_tp_profit", out var closeConfirmTpProfitElement);
        first.TryGetProperty("close_max_tp_profit", out var closeMaxTpProfitElement);
        first.TryGetProperty("limit_max_tp", out var limitMaxTpElement);
        first.TryGetProperty("sos_trigger_a_open_distance_pts", out var sosTriggerAOpenDistancePtsElement);
        first.TryGetProperty("sos_trigger_after_seconds", out var sosTriggerAfterSecondsElement);
        first.TryGetProperty("sos_close_confirm_gap_pts", out var sosCloseConfirmGapPtsElement);
        first.TryGetProperty("sos_close_gap_pts", out var sosCloseGapPtsElement);
        first.TryGetProperty("close_hold_confirm_ms", out var closeHoldConfirmMsElement);
        first.TryGetProperty("close_price_freeze_ms", out var closePriceFreezeMsElement);
        first.TryGetProperty("start_time_hold", out var startTimeHoldElement);
        first.TryGetProperty("end_time_hold", out var endTimeHoldElement);
        first.TryGetProperty("confirm_latency", out var confirmLatencyMsElement);
        first.TryGetProperty("max_gap", out var maxGapElement);
        first.TryGetProperty("limit_max_gap", out var limitMaxGapElement);
        first.TryGetProperty("max_spread", out var maxSpreadElement);
        first.TryGetProperty("open_max_times_tick", out var openMaxTimesTickElement);
        first.TryGetProperty("close_max_times_tick", out var closeMaxTimesTickElement);
        first.TryGetProperty("signal_cycle_size", out var signalCycleSizeElement);
        first.TryGetProperty("open_pending_time_ms", out var openPendingTimeMsElement);
        first.TryGetProperty("close_pending_time_ms", out var closePendingTimeMsElement);
        first.TryGetProperty("delay_open_a_ms", out var delayOpenAMsElement);
        first.TryGetProperty("delay_open_b_ms", out var delayOpenBMsElement);
        first.TryGetProperty("delay_close_a_ms", out var delayCloseAMsElement);
        first.TryGetProperty("delay_close_b_ms", out var delayCloseBMsElement);
        first.TryGetProperty("open_number_of_qualifying_times", out var openQtElement);
        first.TryGetProperty("close_number_of_qualifying_times", out var closeQtElement);
        first.TryGetProperty("platform_a", out var platformAElement);
        first.TryGetProperty("platform_b", out var platformBElement);
        first.TryGetProperty("is_show_config", out var isShowConfigElement);
        first.TryGetProperty("current_tick_a", out var currentTickAElement);
        first.TryGetProperty("current_tick_b", out var currentTickBElement);
        first.TryGetProperty("current_slots", out var currentSlotsElement);
        first.TryGetProperty("max_life_time_by_second", out var maxLifeTimeBySecondElement);
        first.TryGetProperty("min_profit_to_close", out var minProfitToCloseElement);
        first.TryGetProperty("min_buy_opens", out var minBuyOpensElement);
        first.TryGetProperty("max_buy_opens", out var maxBuyOpensElement);
        first.TryGetProperty("min_sell_opens", out var minSellOpensElement);
        first.TryGetProperty("max_sell_opens", out var maxSellOpensElement);
        first.TryGetProperty("max_total_opens", out var maxTotalOpensElement);
        first.TryGetProperty("opposite_side_lock_seconds", out var oppositeSideLockSecondsElement);
        first.TryGetProperty("opposite_open_min_distance_pts", out var oppositeOpenMinDistancePtsElement);
        first.TryGetProperty("rd_start_same_action_lock_seconds", out var rdStartSameActionLockSecondsElement);
        first.TryGetProperty("rd_end_same_action_lock_seconds", out var rdEndSameActionLockSecondsElement);
        first.TryGetProperty("rd_start_post_close_lock_seconds", out var rdStartPostCloseLockSecondsElement);
        first.TryGetProperty("rd_end_post_close_lock_seconds", out var rdEndPostCloseLockSecondsElement);
        first.TryGetProperty("rd_start_post_open_lock_seconds", out var rdStartPostOpenLockSecondsElement);
        first.TryGetProperty("rd_end_post_open_lock_seconds", out var rdEndPostOpenLockSecondsElement);
        ReadGapStabilityElements(first, out var openGapElements, out var closeGapElements);
        first.TryGetProperty("schedule_sleeping", out var scheduleSleepingElement);

        // DB column name is lowercase: hostname
        var hasHostName = first.TryGetProperty("hostname", out var hostNameElement);
        if (!hasHostName)
        {
            first.TryGetProperty("HostName", out hostNameElement);
        }

        return new ConfigRow
        {
            Id = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null,
            // Clone để JsonElement không còn phụ thuộc JsonDocument đã dispose.
            Sans = sansElement.ValueKind is JsonValueKind.Undefined
                ? default
                : sansElement.Clone(),
            HostName = hostNameElement.ValueKind == JsonValueKind.String ? hostNameElement.GetString() : null,
            Point = pointElement.ValueKind == JsonValueKind.Number && pointElement.TryGetInt32(out var p) ? p : 1,
            OpenPts = openPtsElement.ValueKind == JsonValueKind.Number && openPtsElement.TryGetInt32(out var openPts) ? openPts : 0,
            ConfirmGapPts = confirmGapPtsElement.ValueKind == JsonValueKind.Number && confirmGapPtsElement.TryGetInt32(out var confirmGapPts) ? confirmGapPts : 0,
            HoldConfirmMs = holdConfirmMsElement.ValueKind == JsonValueKind.Number && holdConfirmMsElement.TryGetInt32(out var holdConfirmMs) ? holdConfirmMs : 0,
            OpenPriceFreezeMs = openPriceFreezeMsElement.ValueKind == JsonValueKind.Number && openPriceFreezeMsElement.TryGetInt32(out var openPriceFreezeMs) ? openPriceFreezeMs : 0,
            ClosePts = closePtsElement.ValueKind == JsonValueKind.Number && closePtsElement.TryGetInt32(out var closePts) ? closePts : 0,
            CloseConfirmGapPts = closeConfirmGapPtsElement.ValueKind == JsonValueKind.Number && closeConfirmGapPtsElement.TryGetInt32(out var closeConfirmGapPts) ? closeConfirmGapPts : 0,
            CloseTpProfit = closeTpProfitElement.ValueKind == JsonValueKind.Number && closeTpProfitElement.TryGetDouble(out var closeTpProfit) ? closeTpProfit : 0d,
            CloseConfirmTpProfit = closeConfirmTpProfitElement.ValueKind == JsonValueKind.Number && closeConfirmTpProfitElement.TryGetDouble(out var closeConfirmTpProfit) ? closeConfirmTpProfit : 0d,
            CloseMaxTpProfit = closeMaxTpProfitElement.ValueKind == JsonValueKind.Number && closeMaxTpProfitElement.TryGetDouble(out var closeMaxTpProfit) ? closeMaxTpProfit : 0d,
            LimitMaxTp = limitMaxTpElement.ValueKind == JsonValueKind.Number && limitMaxTpElement.TryGetDouble(out var limitMaxTp) ? limitMaxTp : 0d,
            SosTriggerAOpenDistancePts = sosTriggerAOpenDistancePtsElement.ValueKind == JsonValueKind.Number && sosTriggerAOpenDistancePtsElement.TryGetDouble(out var sosTriggerAOpenDistancePts) ? sosTriggerAOpenDistancePts : 0d,
            SosTriggerAfterSeconds = sosTriggerAfterSecondsElement.ValueKind == JsonValueKind.Number && sosTriggerAfterSecondsElement.TryGetInt32(out var sosTriggerAfterSeconds) ? sosTriggerAfterSeconds : 0,
            SosCloseConfirmGapPts = sosCloseConfirmGapPtsElement.ValueKind == JsonValueKind.Number && sosCloseConfirmGapPtsElement.TryGetInt32(out var sosCloseConfirmGapPts) ? sosCloseConfirmGapPts : 0,
            SosCloseGapPts = sosCloseGapPtsElement.ValueKind == JsonValueKind.Number && sosCloseGapPtsElement.TryGetInt32(out var sosCloseGapPts) ? sosCloseGapPts : 0,
            CloseHoldConfirmMs = closeHoldConfirmMsElement.ValueKind == JsonValueKind.Number && closeHoldConfirmMsElement.TryGetInt32(out var closeHoldConfirmMs) ? closeHoldConfirmMs : 0,
            ClosePriceFreezeMs = closePriceFreezeMsElement.ValueKind == JsonValueKind.Number && closePriceFreezeMsElement.TryGetInt32(out var closePriceFreezeMs) ? closePriceFreezeMs : 0,
            StartTimeHold = startTimeHoldElement.ValueKind == JsonValueKind.Number && startTimeHoldElement.TryGetInt32(out var startTimeHold) ? startTimeHold : 0,
            EndTimeHold = endTimeHoldElement.ValueKind == JsonValueKind.Number && endTimeHoldElement.TryGetInt32(out var endTimeHold) ? endTimeHold : 0,
            ConfirmLatencyMs = confirmLatencyMsElement.ValueKind == JsonValueKind.Number && confirmLatencyMsElement.TryGetInt32(out var confirmLatencyMs) ? confirmLatencyMs : 0,
            MaxGap = maxGapElement.ValueKind == JsonValueKind.Number && maxGapElement.TryGetInt32(out var maxGap) ? maxGap : 0,
            LimitMaxGap = limitMaxGapElement.ValueKind == JsonValueKind.Number && limitMaxGapElement.TryGetInt32(out var limitMaxGap) ? limitMaxGap : 0,
            MaxSpread = maxSpreadElement.ValueKind == JsonValueKind.Number && maxSpreadElement.TryGetInt32(out var maxSpread) ? maxSpread : 0,
            OpenMaxTimesTick = openMaxTimesTickElement.ValueKind == JsonValueKind.Number && openMaxTimesTickElement.TryGetInt32(out var openMaxTimesTick) ? openMaxTimesTick : 0,
            CloseMaxTimesTick = closeMaxTimesTickElement.ValueKind == JsonValueKind.Number && closeMaxTimesTickElement.TryGetInt32(out var closeMaxTimesTick) ? closeMaxTimesTick : 0,
            SignalCycleSize = signalCycleSizeElement.ValueKind == JsonValueKind.Number && signalCycleSizeElement.TryGetInt32(out var signalCycleSize) ? signalCycleSize : 10,
            OpenPendingTimeMs = openPendingTimeMsElement.ValueKind == JsonValueKind.Number && openPendingTimeMsElement.TryGetInt32(out var openPendingTimeMs) ? openPendingTimeMs : 0,
            ClosePendingTimeMs = closePendingTimeMsElement.ValueKind == JsonValueKind.Number && closePendingTimeMsElement.TryGetInt32(out var closePendingTimeMs) ? closePendingTimeMs : 0,
            DelayOpenAMs = delayOpenAMsElement.ValueKind == JsonValueKind.Number && delayOpenAMsElement.TryGetInt32(out var delayOpenAMs) ? delayOpenAMs : 0,
            DelayOpenBMs = delayOpenBMsElement.ValueKind == JsonValueKind.Number && delayOpenBMsElement.TryGetInt32(out var delayOpenBMs) ? delayOpenBMs : 0,
            DelayCloseAMs = delayCloseAMsElement.ValueKind == JsonValueKind.Number && delayCloseAMsElement.TryGetInt32(out var delayCloseAMs) ? delayCloseAMs : 0,
            DelayCloseBMs = delayCloseBMsElement.ValueKind == JsonValueKind.Number && delayCloseBMsElement.TryGetInt32(out var delayCloseBMs) ? delayCloseBMs : 0,
            OpenNumberOfQualifyingTimes = openQtElement.ValueKind == JsonValueKind.Number && openQtElement.TryGetInt32(out var openQt) ? openQt : 1,
            CloseNumberOfQualifyingTimes = closeQtElement.ValueKind == JsonValueKind.Number && closeQtElement.TryGetInt32(out var closeQt) ? closeQt : 1,
            PlatformA = platformAElement.ValueKind == JsonValueKind.String ? platformAElement.GetString() : null,
            PlatformB = platformBElement.ValueKind == JsonValueKind.String ? platformBElement.GetString() : null,
            IsShowConfig = isShowConfigElement.ValueKind == JsonValueKind.Number && isShowConfigElement.TryGetInt32(out var isShowConfig) ? isShowConfig : 0,
            CurrentTickA = currentTickAElement.ValueKind == JsonValueKind.String ? currentTickAElement.GetString() : null,
            CurrentTickB = currentTickBElement.ValueKind == JsonValueKind.String ? currentTickBElement.GetString() : null,
            CurrentSlots = currentSlotsElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object
                ? currentSlotsElement.Clone()
                : default,
            MaxLifeTimeBySecond = maxLifeTimeBySecondElement.ValueKind == JsonValueKind.Number && maxLifeTimeBySecondElement.TryGetInt32(out var maxLifeTimeBySecond) ? maxLifeTimeBySecond : 0,
            MinProfitToClose = minProfitToCloseElement.ValueKind == JsonValueKind.Number && minProfitToCloseElement.TryGetDouble(out var minProfitToClose) ? minProfitToClose : 0d,
            MinBuyOpens = minBuyOpensElement.ValueKind == JsonValueKind.Number && minBuyOpensElement.TryGetInt32(out var minBuyOpens) ? minBuyOpens : 1,
            MaxBuyOpens = maxBuyOpensElement.ValueKind == JsonValueKind.Number && maxBuyOpensElement.TryGetInt32(out var maxBuyOpens) ? maxBuyOpens : 3,
            MinSellOpens = minSellOpensElement.ValueKind == JsonValueKind.Number && minSellOpensElement.TryGetInt32(out var minSellOpens) ? minSellOpens : 1,
            MaxSellOpens = maxSellOpensElement.ValueKind == JsonValueKind.Number && maxSellOpensElement.TryGetInt32(out var maxSellOpens) ? maxSellOpens : 3,
            MaxTotalOpens = maxTotalOpensElement.ValueKind == JsonValueKind.Number && maxTotalOpensElement.TryGetInt32(out var maxTotalOpens) ? maxTotalOpens : 5,
            OppositeSideLockSeconds = oppositeSideLockSecondsElement.ValueKind == JsonValueKind.Number && oppositeSideLockSecondsElement.TryGetInt32(out var oppositeSideLockSeconds) ? oppositeSideLockSeconds : 300,
            OppositeOpenMinDistancePts = oppositeOpenMinDistancePtsElement.ValueKind == JsonValueKind.Number && oppositeOpenMinDistancePtsElement.TryGetInt32(out var oppositeOpenMinDistancePts) ? oppositeOpenMinDistancePts : 0,
            RdStartSameActionLockSeconds = rdStartSameActionLockSecondsElement.ValueKind == JsonValueKind.Number && rdStartSameActionLockSecondsElement.TryGetInt32(out var rdStartSameActionLockSeconds) ? rdStartSameActionLockSeconds : 3,
            RdEndSameActionLockSeconds = rdEndSameActionLockSecondsElement.ValueKind == JsonValueKind.Number && rdEndSameActionLockSecondsElement.TryGetInt32(out var rdEndSameActionLockSeconds) ? rdEndSameActionLockSeconds : 10,
            RdStartPostCloseLockSeconds = rdStartPostCloseLockSecondsElement.ValueKind == JsonValueKind.Number && rdStartPostCloseLockSecondsElement.TryGetInt32(out var rdStartPostCloseLockSeconds) ? rdStartPostCloseLockSeconds : 300,
            RdEndPostCloseLockSeconds = rdEndPostCloseLockSecondsElement.ValueKind == JsonValueKind.Number && rdEndPostCloseLockSecondsElement.TryGetInt32(out var rdEndPostCloseLockSeconds) ? rdEndPostCloseLockSeconds : 300,
            RdStartPostOpenLockSeconds = rdStartPostOpenLockSecondsElement.ValueKind == JsonValueKind.Number && rdStartPostOpenLockSecondsElement.TryGetInt32(out var rdStartPostOpenLockSeconds) ? rdStartPostOpenLockSeconds : 0,
            RdEndPostOpenLockSeconds = rdEndPostOpenLockSecondsElement.ValueKind == JsonValueKind.Number && rdEndPostOpenLockSecondsElement.TryGetInt32(out var rdEndPostOpenLockSeconds) ? rdEndPostOpenLockSeconds : 0,
            OpenGapAbsoluteFloor = ReadInt(openGapElements.AbsoluteFloor, -1),
            OpenGapRelativeTolerance = ReadDouble(openGapElements.RelativeTolerance),
            OpenGapMadMultiplier = ReadDouble(openGapElements.MadMultiplier),
            OpenGapMinStableSamples = ReadInt(openGapElements.MinStableSamples),
            OpenGapMaxDispersion = ReadDouble(openGapElements.MaxDispersion),
            OpenGapMaxDrift = ReadDouble(openGapElements.MaxDrift),
            CloseGapAbsoluteFloor = ReadInt(closeGapElements.AbsoluteFloor, -1),
            CloseGapRelativeTolerance = ReadDouble(closeGapElements.RelativeTolerance),
            CloseGapMadMultiplier = ReadDouble(closeGapElements.MadMultiplier),
            CloseGapMinStableSamples = ReadInt(closeGapElements.MinStableSamples),
            CloseGapMaxDispersion = ReadDouble(closeGapElements.MaxDispersion),
            CloseGapMaxDrift = ReadDouble(closeGapElements.MaxDrift),
            ScheduleSleeping = scheduleSleepingElement.ValueKind == JsonValueKind.Object
                ? scheduleSleepingElement.Clone()
                : default
        };
    }

    private async Task<ConfigRow?> GetFirstByColumnLikeAsync(string columnName, string value, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_supabaseUrl}/rest/v1/configs?select=*&{columnName}=ilike.*{Uri.EscapeDataString(value)}*&limit=1");

        AddAuthHeaders(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Supabase GetFirstByColumnLikeAsync thất bại. Column={columnName}, Status={(int)response.StatusCode}, Body={errorBody}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var first = doc.RootElement[0];
        if (first.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        first.TryGetProperty("id", out var idElement);
        first.TryGetProperty("sans", out var sansElement);
        first.TryGetProperty("point", out var pointElement);
        first.TryGetProperty("open_pts", out var openPtsElement);
        first.TryGetProperty("open_confirm_gap_pts", out var confirmGapPtsElement);
        first.TryGetProperty("open_hold_confirm_ms", out var holdConfirmMsElement);
        first.TryGetProperty("open_price_freeze_ms", out var openPriceFreezeMsElement);
        first.TryGetProperty("close_pts", out var closePtsElement);
        first.TryGetProperty("close_confirm_gap_pts", out var closeConfirmGapPtsElement);
        first.TryGetProperty("close_tp_profit", out var closeTpProfitElement);
        first.TryGetProperty("close_confirm_tp_profit", out var closeConfirmTpProfitElement);
        first.TryGetProperty("close_max_tp_profit", out var closeMaxTpProfitElement);
        first.TryGetProperty("limit_max_tp", out var limitMaxTpElement);
        first.TryGetProperty("sos_trigger_a_open_distance_pts", out var sosTriggerAOpenDistancePtsElement);
        first.TryGetProperty("sos_trigger_after_seconds", out var sosTriggerAfterSecondsElement);
        first.TryGetProperty("sos_close_confirm_gap_pts", out var sosCloseConfirmGapPtsElement);
        first.TryGetProperty("sos_close_gap_pts", out var sosCloseGapPtsElement);
        first.TryGetProperty("close_hold_confirm_ms", out var closeHoldConfirmMsElement);
        first.TryGetProperty("close_price_freeze_ms", out var closePriceFreezeMsElement);
        first.TryGetProperty("start_time_hold", out var startTimeHoldElement);
        first.TryGetProperty("end_time_hold", out var endTimeHoldElement);
        first.TryGetProperty("confirm_latency", out var confirmLatencyMsElement);
        first.TryGetProperty("max_gap", out var maxGapElement);
        first.TryGetProperty("limit_max_gap", out var limitMaxGapElement);
        first.TryGetProperty("max_spread", out var maxSpreadElement);
        first.TryGetProperty("open_max_times_tick", out var openMaxTimesTickElement);
        first.TryGetProperty("close_max_times_tick", out var closeMaxTimesTickElement);
        first.TryGetProperty("signal_cycle_size", out var signalCycleSizeElement);
        first.TryGetProperty("open_pending_time_ms", out var openPendingTimeMsElement);
        first.TryGetProperty("close_pending_time_ms", out var closePendingTimeMsElement);
        first.TryGetProperty("delay_open_a_ms", out var delayOpenAMsElement);
        first.TryGetProperty("delay_open_b_ms", out var delayOpenBMsElement);
        first.TryGetProperty("delay_close_a_ms", out var delayCloseAMsElement);
        first.TryGetProperty("delay_close_b_ms", out var delayCloseBMsElement);
        first.TryGetProperty("open_number_of_qualifying_times", out var openQtElement);
        first.TryGetProperty("close_number_of_qualifying_times", out var closeQtElement);
        first.TryGetProperty("platform_a", out var platformAElement);
        first.TryGetProperty("platform_b", out var platformBElement);
        first.TryGetProperty("is_show_config", out var isShowConfigElement);
        first.TryGetProperty("current_tick_a", out var currentTickAElement);
        first.TryGetProperty("current_tick_b", out var currentTickBElement);
        first.TryGetProperty("current_slots", out var currentSlotsElement);
        first.TryGetProperty("max_life_time_by_second", out var maxLifeTimeBySecondElement);
        first.TryGetProperty("min_profit_to_close", out var minProfitToCloseElement);
        first.TryGetProperty("min_buy_opens", out var minBuyOpensElement);
        first.TryGetProperty("max_buy_opens", out var maxBuyOpensElement);
        first.TryGetProperty("min_sell_opens", out var minSellOpensElement);
        first.TryGetProperty("max_sell_opens", out var maxSellOpensElement);
        first.TryGetProperty("max_total_opens", out var maxTotalOpensElement);
        first.TryGetProperty("opposite_side_lock_seconds", out var oppositeSideLockSecondsElement);
        first.TryGetProperty("opposite_open_min_distance_pts", out var oppositeOpenMinDistancePtsElement);
        first.TryGetProperty("rd_start_same_action_lock_seconds", out var rdStartSameActionLockSecondsElement);
        first.TryGetProperty("rd_end_same_action_lock_seconds", out var rdEndSameActionLockSecondsElement);
        first.TryGetProperty("rd_start_post_close_lock_seconds", out var rdStartPostCloseLockSecondsElement);
        first.TryGetProperty("rd_end_post_close_lock_seconds", out var rdEndPostCloseLockSecondsElement);
        first.TryGetProperty("rd_start_post_open_lock_seconds", out var rdStartPostOpenLockSecondsElement);
        first.TryGetProperty("rd_end_post_open_lock_seconds", out var rdEndPostOpenLockSecondsElement);
        ReadGapStabilityElements(first, out var openGapElements, out var closeGapElements);
        first.TryGetProperty("schedule_sleeping", out var scheduleSleepingElement);

        var hasHostName = first.TryGetProperty("hostname", out var hostNameElement);
        if (!hasHostName)
        {
            first.TryGetProperty("HostName", out hostNameElement);
        }

        return new ConfigRow
        {
            Id = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null,
            Sans = sansElement.ValueKind is JsonValueKind.Undefined
                ? default
                : sansElement.Clone(),
            HostName = hostNameElement.ValueKind == JsonValueKind.String ? hostNameElement.GetString() : null,
            Point = pointElement.ValueKind == JsonValueKind.Number && pointElement.TryGetInt32(out var p) ? p : 1,
            OpenPts = openPtsElement.ValueKind == JsonValueKind.Number && openPtsElement.TryGetInt32(out var openPts) ? openPts : 0,
            ConfirmGapPts = confirmGapPtsElement.ValueKind == JsonValueKind.Number && confirmGapPtsElement.TryGetInt32(out var confirmGapPts) ? confirmGapPts : 0,
            HoldConfirmMs = holdConfirmMsElement.ValueKind == JsonValueKind.Number && holdConfirmMsElement.TryGetInt32(out var holdConfirmMs) ? holdConfirmMs : 0,
            OpenPriceFreezeMs = openPriceFreezeMsElement.ValueKind == JsonValueKind.Number && openPriceFreezeMsElement.TryGetInt32(out var openPriceFreezeMs) ? openPriceFreezeMs : 0,
            ClosePts = closePtsElement.ValueKind == JsonValueKind.Number && closePtsElement.TryGetInt32(out var closePts) ? closePts : 0,
            CloseConfirmGapPts = closeConfirmGapPtsElement.ValueKind == JsonValueKind.Number && closeConfirmGapPtsElement.TryGetInt32(out var closeConfirmGapPts) ? closeConfirmGapPts : 0,
            CloseTpProfit = closeTpProfitElement.ValueKind == JsonValueKind.Number && closeTpProfitElement.TryGetDouble(out var closeTpProfit) ? closeTpProfit : 0d,
            CloseConfirmTpProfit = closeConfirmTpProfitElement.ValueKind == JsonValueKind.Number && closeConfirmTpProfitElement.TryGetDouble(out var closeConfirmTpProfit) ? closeConfirmTpProfit : 0d,
            CloseMaxTpProfit = closeMaxTpProfitElement.ValueKind == JsonValueKind.Number && closeMaxTpProfitElement.TryGetDouble(out var closeMaxTpProfit) ? closeMaxTpProfit : 0d,
            LimitMaxTp = limitMaxTpElement.ValueKind == JsonValueKind.Number && limitMaxTpElement.TryGetDouble(out var limitMaxTp) ? limitMaxTp : 0d,
            SosTriggerAOpenDistancePts = sosTriggerAOpenDistancePtsElement.ValueKind == JsonValueKind.Number && sosTriggerAOpenDistancePtsElement.TryGetDouble(out var sosTriggerAOpenDistancePts) ? sosTriggerAOpenDistancePts : 0d,
            SosTriggerAfterSeconds = sosTriggerAfterSecondsElement.ValueKind == JsonValueKind.Number && sosTriggerAfterSecondsElement.TryGetInt32(out var sosTriggerAfterSeconds) ? sosTriggerAfterSeconds : 0,
            SosCloseConfirmGapPts = sosCloseConfirmGapPtsElement.ValueKind == JsonValueKind.Number && sosCloseConfirmGapPtsElement.TryGetInt32(out var sosCloseConfirmGapPts) ? sosCloseConfirmGapPts : 0,
            SosCloseGapPts = sosCloseGapPtsElement.ValueKind == JsonValueKind.Number && sosCloseGapPtsElement.TryGetInt32(out var sosCloseGapPts) ? sosCloseGapPts : 0,
            CloseHoldConfirmMs = closeHoldConfirmMsElement.ValueKind == JsonValueKind.Number && closeHoldConfirmMsElement.TryGetInt32(out var closeHoldConfirmMs) ? closeHoldConfirmMs : 0,
            ClosePriceFreezeMs = closePriceFreezeMsElement.ValueKind == JsonValueKind.Number && closePriceFreezeMsElement.TryGetInt32(out var closePriceFreezeMs) ? closePriceFreezeMs : 0,
            StartTimeHold = startTimeHoldElement.ValueKind == JsonValueKind.Number && startTimeHoldElement.TryGetInt32(out var startTimeHold) ? startTimeHold : 0,
            EndTimeHold = endTimeHoldElement.ValueKind == JsonValueKind.Number && endTimeHoldElement.TryGetInt32(out var endTimeHold) ? endTimeHold : 0,
            ConfirmLatencyMs = confirmLatencyMsElement.ValueKind == JsonValueKind.Number && confirmLatencyMsElement.TryGetInt32(out var confirmLatencyMs) ? confirmLatencyMs : 0,
            MaxGap = maxGapElement.ValueKind == JsonValueKind.Number && maxGapElement.TryGetInt32(out var maxGap) ? maxGap : 0,
            LimitMaxGap = limitMaxGapElement.ValueKind == JsonValueKind.Number && limitMaxGapElement.TryGetInt32(out var limitMaxGap) ? limitMaxGap : 0,
            MaxSpread = maxSpreadElement.ValueKind == JsonValueKind.Number && maxSpreadElement.TryGetInt32(out var maxSpread) ? maxSpread : 0,
            OpenMaxTimesTick = openMaxTimesTickElement.ValueKind == JsonValueKind.Number && openMaxTimesTickElement.TryGetInt32(out var openMaxTimesTick) ? openMaxTimesTick : 0,
            CloseMaxTimesTick = closeMaxTimesTickElement.ValueKind == JsonValueKind.Number && closeMaxTimesTickElement.TryGetInt32(out var closeMaxTimesTick) ? closeMaxTimesTick : 0,
            SignalCycleSize = signalCycleSizeElement.ValueKind == JsonValueKind.Number && signalCycleSizeElement.TryGetInt32(out var signalCycleSize) ? signalCycleSize : 10,
            OpenPendingTimeMs = openPendingTimeMsElement.ValueKind == JsonValueKind.Number && openPendingTimeMsElement.TryGetInt32(out var openPendingTimeMs) ? openPendingTimeMs : 0,
            ClosePendingTimeMs = closePendingTimeMsElement.ValueKind == JsonValueKind.Number && closePendingTimeMsElement.TryGetInt32(out var closePendingTimeMs) ? closePendingTimeMs : 0,
            DelayOpenAMs = delayOpenAMsElement.ValueKind == JsonValueKind.Number && delayOpenAMsElement.TryGetInt32(out var delayOpenAMs) ? delayOpenAMs : 0,
            DelayOpenBMs = delayOpenBMsElement.ValueKind == JsonValueKind.Number && delayOpenBMsElement.TryGetInt32(out var delayOpenBMs) ? delayOpenBMs : 0,
            DelayCloseAMs = delayCloseAMsElement.ValueKind == JsonValueKind.Number && delayCloseAMsElement.TryGetInt32(out var delayCloseAMs) ? delayCloseAMs : 0,
            DelayCloseBMs = delayCloseBMsElement.ValueKind == JsonValueKind.Number && delayCloseBMsElement.TryGetInt32(out var delayCloseBMs) ? delayCloseBMs : 0,
            OpenNumberOfQualifyingTimes = openQtElement.ValueKind == JsonValueKind.Number && openQtElement.TryGetInt32(out var openQt) ? openQt : 1,
            CloseNumberOfQualifyingTimes = closeQtElement.ValueKind == JsonValueKind.Number && closeQtElement.TryGetInt32(out var closeQt) ? closeQt : 1,
            PlatformA = platformAElement.ValueKind == JsonValueKind.String ? platformAElement.GetString() : null,
            PlatformB = platformBElement.ValueKind == JsonValueKind.String ? platformBElement.GetString() : null,
            IsShowConfig = isShowConfigElement.ValueKind == JsonValueKind.Number && isShowConfigElement.TryGetInt32(out var isShowConfig) ? isShowConfig : 0,
            CurrentTickA = currentTickAElement.ValueKind == JsonValueKind.String ? currentTickAElement.GetString() : null,
            CurrentTickB = currentTickBElement.ValueKind == JsonValueKind.String ? currentTickBElement.GetString() : null,
            CurrentSlots = currentSlotsElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object
                ? currentSlotsElement.Clone()
                : default,
            MaxLifeTimeBySecond = maxLifeTimeBySecondElement.ValueKind == JsonValueKind.Number && maxLifeTimeBySecondElement.TryGetInt32(out var maxLifeTimeBySecond) ? maxLifeTimeBySecond : 0,
            MinProfitToClose = minProfitToCloseElement.ValueKind == JsonValueKind.Number && minProfitToCloseElement.TryGetDouble(out var minProfitToClose) ? minProfitToClose : 0d,
            MinBuyOpens = minBuyOpensElement.ValueKind == JsonValueKind.Number && minBuyOpensElement.TryGetInt32(out var minBuyOpens) ? minBuyOpens : 1,
            MaxBuyOpens = maxBuyOpensElement.ValueKind == JsonValueKind.Number && maxBuyOpensElement.TryGetInt32(out var maxBuyOpens) ? maxBuyOpens : 3,
            MinSellOpens = minSellOpensElement.ValueKind == JsonValueKind.Number && minSellOpensElement.TryGetInt32(out var minSellOpens) ? minSellOpens : 1,
            MaxSellOpens = maxSellOpensElement.ValueKind == JsonValueKind.Number && maxSellOpensElement.TryGetInt32(out var maxSellOpens) ? maxSellOpens : 3,
            MaxTotalOpens = maxTotalOpensElement.ValueKind == JsonValueKind.Number && maxTotalOpensElement.TryGetInt32(out var maxTotalOpens) ? maxTotalOpens : 5,
            OppositeSideLockSeconds = oppositeSideLockSecondsElement.ValueKind == JsonValueKind.Number && oppositeSideLockSecondsElement.TryGetInt32(out var oppositeSideLockSeconds) ? oppositeSideLockSeconds : 300,
            OppositeOpenMinDistancePts = oppositeOpenMinDistancePtsElement.ValueKind == JsonValueKind.Number && oppositeOpenMinDistancePtsElement.TryGetInt32(out var oppositeOpenMinDistancePts) ? oppositeOpenMinDistancePts : 0,
            RdStartSameActionLockSeconds = rdStartSameActionLockSecondsElement.ValueKind == JsonValueKind.Number && rdStartSameActionLockSecondsElement.TryGetInt32(out var rdStartSameActionLockSeconds) ? rdStartSameActionLockSeconds : 3,
            RdEndSameActionLockSeconds = rdEndSameActionLockSecondsElement.ValueKind == JsonValueKind.Number && rdEndSameActionLockSecondsElement.TryGetInt32(out var rdEndSameActionLockSeconds) ? rdEndSameActionLockSeconds : 10,
            RdStartPostCloseLockSeconds = rdStartPostCloseLockSecondsElement.ValueKind == JsonValueKind.Number && rdStartPostCloseLockSecondsElement.TryGetInt32(out var rdStartPostCloseLockSeconds) ? rdStartPostCloseLockSeconds : 300,
            RdEndPostCloseLockSeconds = rdEndPostCloseLockSecondsElement.ValueKind == JsonValueKind.Number && rdEndPostCloseLockSecondsElement.TryGetInt32(out var rdEndPostCloseLockSeconds) ? rdEndPostCloseLockSeconds : 300,
            RdStartPostOpenLockSeconds = rdStartPostOpenLockSecondsElement.ValueKind == JsonValueKind.Number && rdStartPostOpenLockSecondsElement.TryGetInt32(out var rdStartPostOpenLockSeconds) ? rdStartPostOpenLockSeconds : 0,
            RdEndPostOpenLockSeconds = rdEndPostOpenLockSecondsElement.ValueKind == JsonValueKind.Number && rdEndPostOpenLockSecondsElement.TryGetInt32(out var rdEndPostOpenLockSeconds) ? rdEndPostOpenLockSeconds : 0,
            OpenGapAbsoluteFloor = ReadInt(openGapElements.AbsoluteFloor, -1),
            OpenGapRelativeTolerance = ReadDouble(openGapElements.RelativeTolerance),
            OpenGapMadMultiplier = ReadDouble(openGapElements.MadMultiplier),
            OpenGapMinStableSamples = ReadInt(openGapElements.MinStableSamples),
            OpenGapMaxDispersion = ReadDouble(openGapElements.MaxDispersion),
            OpenGapMaxDrift = ReadDouble(openGapElements.MaxDrift),
            CloseGapAbsoluteFloor = ReadInt(closeGapElements.AbsoluteFloor, -1),
            CloseGapRelativeTolerance = ReadDouble(closeGapElements.RelativeTolerance),
            CloseGapMadMultiplier = ReadDouble(closeGapElements.MadMultiplier),
            CloseGapMinStableSamples = ReadInt(closeGapElements.MinStableSamples),
            CloseGapMaxDispersion = ReadDouble(closeGapElements.MaxDispersion),
            CloseGapMaxDrift = ReadDouble(closeGapElements.MaxDrift),
            ScheduleSleeping = scheduleSleepingElement.ValueKind == JsonValueKind.Object
                ? scheduleSleepingElement.Clone()
                : default
        };
    }

    private async Task<bool> UpdateByColumnAsync(
        string columnName,
        string value,
        string sansJson,
        string hostName,
        string platformA,
        string platformB,
        CancellationToken cancellationToken)
    {
        using var sansDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(sansJson) ? "[]" : sansJson);

        var payload = JsonSerializer.Serialize(new
        {
            sans = sansDoc.RootElement,
            hostname = hostName.Trim().ToLower(),
            platform_a = NormalizePlatform(platformA),
            platform_b = NormalizePlatform(platformB)
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{_supabaseUrl}/rest/v1/configs?{columnName}=eq.{Uri.EscapeDataString(value)}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        AddAuthHeaders(request);
        request.Headers.TryAddWithoutValidation("Prefer", "return=representation");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Supabase UpdateByColumnAsync thất bại. Column={columnName}, Status={(int)response.StatusCode}, Body={errorBody}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0;
    }

    private static void ReadGapStabilityElements(
        JsonElement row,
        out GapStabilityElements open,
        out GapStabilityElements close)
    {
        row.TryGetProperty("open_gap_absolute_floor", out var openAbsoluteFloor);
        row.TryGetProperty("open_gap_relative_tolerance", out var openRelativeTolerance);
        row.TryGetProperty("open_gap_mad_multiplier", out var openMadMultiplier);
        row.TryGetProperty("open_gap_min_stable_samples", out var openMinStableSamples);
        row.TryGetProperty("open_gap_max_dispersion", out var openMaxDispersion);
        row.TryGetProperty("open_gap_max_drift", out var openMaxDrift);
        open = new GapStabilityElements(
            openAbsoluteFloor,
            openRelativeTolerance,
            openMadMultiplier,
            openMinStableSamples,
            openMaxDispersion,
            openMaxDrift);

        row.TryGetProperty("close_gap_absolute_floor", out var closeAbsoluteFloor);
        row.TryGetProperty("close_gap_relative_tolerance", out var closeRelativeTolerance);
        row.TryGetProperty("close_gap_mad_multiplier", out var closeMadMultiplier);
        row.TryGetProperty("close_gap_min_stable_samples", out var closeMinStableSamples);
        row.TryGetProperty("close_gap_max_dispersion", out var closeMaxDispersion);
        row.TryGetProperty("close_gap_max_drift", out var closeMaxDrift);
        close = new GapStabilityElements(
            closeAbsoluteFloor,
            closeRelativeTolerance,
            closeMadMultiplier,
            closeMinStableSamples,
            closeMaxDispersion,
            closeMaxDrift);
    }

    private static int ReadInt(JsonElement element, int missingValue = 0) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : missingValue;

    private static double ReadDouble(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value)
            ? value
            : double.NaN;

    private readonly record struct GapStabilityElements(
        JsonElement AbsoluteFloor,
        JsonElement RelativeTolerance,
        JsonElement MadMultiplier,
        JsonElement MinStableSamples,
        JsonElement MaxDispersion,
        JsonElement MaxDrift);

    private sealed class ConfigRow
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("sans")]
        public JsonElement Sans { get; set; }

        [JsonPropertyName("hostname")]
        public string? HostName { get; set; }

        [JsonPropertyName("point")]
        public int Point { get; set; }

        [JsonPropertyName("open_pts")]
        public int OpenPts { get; set; }

        [JsonPropertyName("open_confirm_gap_pts")]
        public int ConfirmGapPts { get; set; }

        [JsonPropertyName("open_hold_confirm_ms")]
        public int HoldConfirmMs { get; set; }

        [JsonPropertyName("open_price_freeze_ms")]
        public int OpenPriceFreezeMs { get; set; }

        [JsonPropertyName("close_pts")]
        public int ClosePts { get; set; }

        [JsonPropertyName("close_confirm_gap_pts")]
        public int CloseConfirmGapPts { get; set; }

        [JsonPropertyName("close_tp_profit")]
        public double CloseTpProfit { get; set; }

        [JsonPropertyName("close_confirm_tp_profit")]
        public double CloseConfirmTpProfit { get; set; }

        [JsonPropertyName("close_max_tp_profit")]
        public double CloseMaxTpProfit { get; set; }

        [JsonPropertyName("limit_max_tp")]
        public double LimitMaxTp { get; set; }

        [JsonPropertyName("sos_trigger_a_open_distance_pts")]
        public double SosTriggerAOpenDistancePts { get; set; }

        [JsonPropertyName("sos_trigger_after_seconds")]
        public int SosTriggerAfterSeconds { get; set; }

        [JsonPropertyName("sos_close_confirm_gap_pts")]
        public int SosCloseConfirmGapPts { get; set; }

        [JsonPropertyName("sos_close_gap_pts")]
        public int SosCloseGapPts { get; set; }

        [JsonPropertyName("close_hold_confirm_ms")]
        public int CloseHoldConfirmMs { get; set; }

        [JsonPropertyName("open_gap_absolute_floor")]
        public int OpenGapAbsoluteFloor { get; set; } = -1;

        [JsonPropertyName("open_gap_relative_tolerance")]
        public double OpenGapRelativeTolerance { get; set; } = double.NaN;

        [JsonPropertyName("open_gap_mad_multiplier")]
        public double OpenGapMadMultiplier { get; set; } = double.NaN;

        [JsonPropertyName("open_gap_min_stable_samples")]
        public int OpenGapMinStableSamples { get; set; }

        [JsonPropertyName("open_gap_max_dispersion")]
        public double OpenGapMaxDispersion { get; set; } = double.NaN;

        [JsonPropertyName("open_gap_max_drift")]
        public double OpenGapMaxDrift { get; set; } = double.NaN;

        [JsonPropertyName("close_gap_absolute_floor")]
        public int CloseGapAbsoluteFloor { get; set; } = -1;

        [JsonPropertyName("close_gap_relative_tolerance")]
        public double CloseGapRelativeTolerance { get; set; } = double.NaN;

        [JsonPropertyName("close_gap_mad_multiplier")]
        public double CloseGapMadMultiplier { get; set; } = double.NaN;

        [JsonPropertyName("close_gap_min_stable_samples")]
        public int CloseGapMinStableSamples { get; set; }

        [JsonPropertyName("close_gap_max_dispersion")]
        public double CloseGapMaxDispersion { get; set; } = double.NaN;

        [JsonPropertyName("close_gap_max_drift")]
        public double CloseGapMaxDrift { get; set; } = double.NaN;

        [JsonPropertyName("close_price_freeze_ms")]
        public int ClosePriceFreezeMs { get; set; }

        [JsonPropertyName("start_time_hold")]
        public int StartTimeHold { get; set; }

        [JsonPropertyName("end_time_hold")]
        public int EndTimeHold { get; set; }

        [JsonPropertyName("confirm_latency")]
        public int ConfirmLatencyMs { get; set; }

        [JsonPropertyName("max_gap")]
        public int MaxGap { get; set; }

        [JsonPropertyName("limit_max_gap")]
        public int LimitMaxGap { get; set; }

        [JsonPropertyName("max_spread")]
        public int MaxSpread { get; set; }

        [JsonPropertyName("open_max_times_tick")]
        public int OpenMaxTimesTick { get; set; }

        [JsonPropertyName("close_max_times_tick")]
        public int CloseMaxTimesTick { get; set; }

        // Chỉ dùng để ghi log đối chiếu với nhánh TICK; không tham gia quyết định signal.
        [JsonPropertyName("signal_cycle_size")]
        public int SignalCycleSize { get; set; } = 10;

        [JsonPropertyName("open_pending_time_ms")]
        public int OpenPendingTimeMs { get; set; }

        [JsonPropertyName("close_pending_time_ms")]
        public int ClosePendingTimeMs { get; set; }

        [JsonPropertyName("delay_open_a_ms")]
        public int DelayOpenAMs { get; set; }

        [JsonPropertyName("delay_open_b_ms")]
        public int DelayOpenBMs { get; set; }

        [JsonPropertyName("delay_close_a_ms")]
        public int DelayCloseAMs { get; set; }

        [JsonPropertyName("delay_close_b_ms")]
        public int DelayCloseBMs { get; set; }

        [JsonPropertyName("open_number_of_qualifying_times")]
        public int OpenNumberOfQualifyingTimes { get; set; } = 1;

        [JsonPropertyName("close_number_of_qualifying_times")]
        public int CloseNumberOfQualifyingTimes { get; set; } = 1;

        [JsonPropertyName("platform_a")]
        public string? PlatformA { get; set; }

        [JsonPropertyName("platform_b")]
        public string? PlatformB { get; set; }

        [JsonPropertyName("is_show_config")]
        public int IsShowConfig { get; set; }

        [JsonPropertyName("current_tick_a")]
        public string? CurrentTickA { get; set; }

        [JsonPropertyName("current_tick_b")]
        public string? CurrentTickB { get; set; }

        [JsonPropertyName("current_slots")]
        public JsonElement CurrentSlots { get; set; }

        [JsonPropertyName("max_life_time_by_second")]
        public int MaxLifeTimeBySecond { get; set; }

        [JsonPropertyName("min_profit_to_close")]
        public double MinProfitToClose { get; set; }

        [JsonPropertyName("min_buy_opens")]
        public int MinBuyOpens { get; set; } = 1;

        [JsonPropertyName("max_buy_opens")]
        public int MaxBuyOpens { get; set; } = 3;

        [JsonPropertyName("min_sell_opens")]
        public int MinSellOpens { get; set; } = 1;

        [JsonPropertyName("max_sell_opens")]
        public int MaxSellOpens { get; set; } = 3;

        [JsonPropertyName("max_total_opens")]
        public int MaxTotalOpens { get; set; } = 5;

        [JsonPropertyName("opposite_side_lock_seconds")]
        public int OppositeSideLockSeconds { get; set; } = 300;

        [JsonPropertyName("opposite_open_min_distance_pts")]
        public int OppositeOpenMinDistancePts { get; set; }

        [JsonPropertyName("rd_start_same_action_lock_seconds")]
        public int RdStartSameActionLockSeconds { get; set; } = 3;

        [JsonPropertyName("rd_end_same_action_lock_seconds")]
        public int RdEndSameActionLockSeconds { get; set; } = 10;

        [JsonPropertyName("rd_start_post_close_lock_seconds")]
        public int RdStartPostCloseLockSeconds { get; set; } = 300;

        [JsonPropertyName("rd_end_post_close_lock_seconds")]
        public int RdEndPostCloseLockSeconds { get; set; } = 300;

        [JsonPropertyName("rd_start_post_open_lock_seconds")]
        public int RdStartPostOpenLockSeconds { get; set; }

        [JsonPropertyName("rd_end_post_open_lock_seconds")]
        public int RdEndPostOpenLockSeconds { get; set; }

        [JsonPropertyName("schedule_sleeping")]
        public JsonElement ScheduleSleeping { get; set; }

        public string CurrentSlotsJson => CurrentSlots.ValueKind is JsonValueKind.Array or JsonValueKind.Object
            ? CurrentSlots.GetRawText()
            : string.Empty;

        public string ScheduleSleepingJson => ScheduleSleeping.ValueKind == JsonValueKind.Object
            ? ScheduleSleeping.GetRawText()
            : string.Empty;
    }
}
