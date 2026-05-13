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
            OpenPriceFreezeMs: row.OpenPriceFreezeMs > 0 ? row.OpenPriceFreezeMs : row.HoldConfirmMs,
            ClosePts: row.ClosePts,
            CloseConfirmGapPts: row.CloseConfirmGapPts,
            CloseHoldConfirmMs: row.CloseHoldConfirmMs,
            ClosePriceFreezeMs: row.ClosePriceFreezeMs > 0 ? row.ClosePriceFreezeMs : row.CloseHoldConfirmMs,
            StartTimeHold: row.StartTimeHold,
            EndTimeHold: row.EndTimeHold,
            StartWaitTime: row.StartWaitTime,
            EndWaitTime: row.EndWaitTime,
            ConfirmLatencyMs: row.ConfirmLatencyMs,
            MaxGap: row.MaxGap,
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
            OpenGapTick: row.OpenGapTick,
            CloseGapTick: row.CloseGapTick,
            CoolDownGapTick: row.CoolDownGapTick,
            IsShowConfig: row.IsShowConfig,
            CurrentTickA: row.CurrentTickA ?? string.Empty,
            CurrentTickB: row.CurrentTickB ?? string.Empty);
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
        first.TryGetProperty("close_hold_confirm_ms", out var closeHoldConfirmMsElement);
        first.TryGetProperty("close_price_freeze_ms", out var closePriceFreezeMsElement);
        first.TryGetProperty("start_time_hold", out var startTimeHoldElement);
        first.TryGetProperty("end_time_hold", out var endTimeHoldElement);
        first.TryGetProperty("start_wait_time", out var startWaitTimeElement);
        first.TryGetProperty("end_wait_time", out var endWaitTimeElement);
        first.TryGetProperty("confirm_latency", out var confirmLatencyMsElement);
        first.TryGetProperty("max_gap", out var maxGapElement);
        first.TryGetProperty("max_spread", out var maxSpreadElement);
        first.TryGetProperty("open_max_times_tick", out var openMaxTimesTickElement);
        first.TryGetProperty("close_max_times_tick", out var closeMaxTimesTickElement);
        first.TryGetProperty("open_pending_time_ms", out var openPendingTimeMsElement);
        first.TryGetProperty("close_pending_time_ms", out var closePendingTimeMsElement);
        first.TryGetProperty("delay_open_a_ms", out var delayOpenAMsElement);
        first.TryGetProperty("delay_open_b_ms", out var delayOpenBMsElement);
        first.TryGetProperty("delay_close_a_ms", out var delayCloseAMsElement);
        first.TryGetProperty("delay_close_b_ms", out var delayCloseBMsElement);
        first.TryGetProperty("open_number_of_qualifying_times", out var openQtElement);
        first.TryGetProperty("close_number_of_qualifying_times", out var closeQtElement);
        first.TryGetProperty("open_gap_tick", out var openGapTickElement);
        first.TryGetProperty("close_gap_tick", out var closeGapTickElement);
        first.TryGetProperty("cool_down_gap_tick", out var coolDownGapTickElement);
        first.TryGetProperty("platform_a", out var platformAElement);
        first.TryGetProperty("platform_b", out var platformBElement);
        first.TryGetProperty("is_show_config", out var isShowConfigElement);
        first.TryGetProperty("current_tick_a", out var currentTickAElement);
        first.TryGetProperty("current_tick_b", out var currentTickBElement);

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
            CloseHoldConfirmMs = closeHoldConfirmMsElement.ValueKind == JsonValueKind.Number && closeHoldConfirmMsElement.TryGetInt32(out var closeHoldConfirmMs) ? closeHoldConfirmMs : 0,
            ClosePriceFreezeMs = closePriceFreezeMsElement.ValueKind == JsonValueKind.Number && closePriceFreezeMsElement.TryGetInt32(out var closePriceFreezeMs) ? closePriceFreezeMs : 0,
            StartTimeHold = startTimeHoldElement.ValueKind == JsonValueKind.Number && startTimeHoldElement.TryGetInt32(out var startTimeHold) ? startTimeHold : 0,
            EndTimeHold = endTimeHoldElement.ValueKind == JsonValueKind.Number && endTimeHoldElement.TryGetInt32(out var endTimeHold) ? endTimeHold : 0,
            StartWaitTime = startWaitTimeElement.ValueKind == JsonValueKind.Number && startWaitTimeElement.TryGetInt32(out var startWaitTime) ? startWaitTime : 0,
            EndWaitTime = endWaitTimeElement.ValueKind == JsonValueKind.Number && endWaitTimeElement.TryGetInt32(out var endWaitTime) ? endWaitTime : 0,
            ConfirmLatencyMs = confirmLatencyMsElement.ValueKind == JsonValueKind.Number && confirmLatencyMsElement.TryGetInt32(out var confirmLatencyMs) ? confirmLatencyMs : 0,
            MaxGap = maxGapElement.ValueKind == JsonValueKind.Number && maxGapElement.TryGetInt32(out var maxGap) ? maxGap : 0,
            MaxSpread = maxSpreadElement.ValueKind == JsonValueKind.Number && maxSpreadElement.TryGetInt32(out var maxSpread) ? maxSpread : 0,
            OpenMaxTimesTick = openMaxTimesTickElement.ValueKind == JsonValueKind.Number && openMaxTimesTickElement.TryGetInt32(out var openMaxTimesTick) ? openMaxTimesTick : 0,
            CloseMaxTimesTick = closeMaxTimesTickElement.ValueKind == JsonValueKind.Number && closeMaxTimesTickElement.TryGetInt32(out var closeMaxTimesTick) ? closeMaxTimesTick : 0,
            OpenPendingTimeMs = openPendingTimeMsElement.ValueKind == JsonValueKind.Number && openPendingTimeMsElement.TryGetInt32(out var openPendingTimeMs) ? openPendingTimeMs : 0,
            ClosePendingTimeMs = closePendingTimeMsElement.ValueKind == JsonValueKind.Number && closePendingTimeMsElement.TryGetInt32(out var closePendingTimeMs) ? closePendingTimeMs : 0,
            DelayOpenAMs = delayOpenAMsElement.ValueKind == JsonValueKind.Number && delayOpenAMsElement.TryGetInt32(out var delayOpenAMs) ? delayOpenAMs : 0,
            DelayOpenBMs = delayOpenBMsElement.ValueKind == JsonValueKind.Number && delayOpenBMsElement.TryGetInt32(out var delayOpenBMs) ? delayOpenBMs : 0,
            DelayCloseAMs = delayCloseAMsElement.ValueKind == JsonValueKind.Number && delayCloseAMsElement.TryGetInt32(out var delayCloseAMs) ? delayCloseAMs : 0,
            DelayCloseBMs = delayCloseBMsElement.ValueKind == JsonValueKind.Number && delayCloseBMsElement.TryGetInt32(out var delayCloseBMs) ? delayCloseBMs : 0,
            OpenNumberOfQualifyingTimes = openQtElement.ValueKind == JsonValueKind.Number && openQtElement.TryGetInt32(out var openQt) ? openQt : 1,
            CloseNumberOfQualifyingTimes = closeQtElement.ValueKind == JsonValueKind.Number && closeQtElement.TryGetInt32(out var closeQt) ? closeQt : 1,
            OpenGapTick = openGapTickElement.ValueKind == JsonValueKind.Number && openGapTickElement.TryGetInt32(out var openGapTick) ? openGapTick : 0,
            CloseGapTick = closeGapTickElement.ValueKind == JsonValueKind.Number && closeGapTickElement.TryGetInt32(out var closeGapTick) ? closeGapTick : 0,
            CoolDownGapTick = coolDownGapTickElement.ValueKind == JsonValueKind.Number && coolDownGapTickElement.TryGetInt32(out var coolDownGapTick) ? coolDownGapTick : 0,
            PlatformA = platformAElement.ValueKind == JsonValueKind.String ? platformAElement.GetString() : null,
            PlatformB = platformBElement.ValueKind == JsonValueKind.String ? platformBElement.GetString() : null,
            IsShowConfig = isShowConfigElement.ValueKind == JsonValueKind.Number && isShowConfigElement.TryGetInt32(out var isShowConfig) ? isShowConfig : 0,
            CurrentTickA = currentTickAElement.ValueKind == JsonValueKind.String ? currentTickAElement.GetString() : null,
            CurrentTickB = currentTickBElement.ValueKind == JsonValueKind.String ? currentTickBElement.GetString() : null
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
        first.TryGetProperty("close_hold_confirm_ms", out var closeHoldConfirmMsElement);
        first.TryGetProperty("close_price_freeze_ms", out var closePriceFreezeMsElement);
        first.TryGetProperty("start_time_hold", out var startTimeHoldElement);
        first.TryGetProperty("end_time_hold", out var endTimeHoldElement);
        first.TryGetProperty("start_wait_time", out var startWaitTimeElement);
        first.TryGetProperty("end_wait_time", out var endWaitTimeElement);
        first.TryGetProperty("confirm_latency", out var confirmLatencyMsElement);
        first.TryGetProperty("max_gap", out var maxGapElement);
        first.TryGetProperty("max_spread", out var maxSpreadElement);
        first.TryGetProperty("open_max_times_tick", out var openMaxTimesTickElement);
        first.TryGetProperty("close_max_times_tick", out var closeMaxTimesTickElement);
        first.TryGetProperty("open_pending_time_ms", out var openPendingTimeMsElement);
        first.TryGetProperty("close_pending_time_ms", out var closePendingTimeMsElement);
        first.TryGetProperty("delay_open_a_ms", out var delayOpenAMsElement);
        first.TryGetProperty("delay_open_b_ms", out var delayOpenBMsElement);
        first.TryGetProperty("delay_close_a_ms", out var delayCloseAMsElement);
        first.TryGetProperty("delay_close_b_ms", out var delayCloseBMsElement);
        first.TryGetProperty("open_number_of_qualifying_times", out var openQtElement);
        first.TryGetProperty("close_number_of_qualifying_times", out var closeQtElement);
        first.TryGetProperty("open_gap_tick", out var openGapTickElement);
        first.TryGetProperty("close_gap_tick", out var closeGapTickElement);
        first.TryGetProperty("cool_down_gap_tick", out var coolDownGapTickElement);
        first.TryGetProperty("platform_a", out var platformAElement);
        first.TryGetProperty("platform_b", out var platformBElement);
        first.TryGetProperty("is_show_config", out var isShowConfigElement);
        first.TryGetProperty("current_tick_a", out var currentTickAElement);
        first.TryGetProperty("current_tick_b", out var currentTickBElement);

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
            CloseHoldConfirmMs = closeHoldConfirmMsElement.ValueKind == JsonValueKind.Number && closeHoldConfirmMsElement.TryGetInt32(out var closeHoldConfirmMs) ? closeHoldConfirmMs : 0,
            ClosePriceFreezeMs = closePriceFreezeMsElement.ValueKind == JsonValueKind.Number && closePriceFreezeMsElement.TryGetInt32(out var closePriceFreezeMs) ? closePriceFreezeMs : 0,
            StartTimeHold = startTimeHoldElement.ValueKind == JsonValueKind.Number && startTimeHoldElement.TryGetInt32(out var startTimeHold) ? startTimeHold : 0,
            EndTimeHold = endTimeHoldElement.ValueKind == JsonValueKind.Number && endTimeHoldElement.TryGetInt32(out var endTimeHold) ? endTimeHold : 0,
            StartWaitTime = startWaitTimeElement.ValueKind == JsonValueKind.Number && startWaitTimeElement.TryGetInt32(out var startWaitTime) ? startWaitTime : 0,
            EndWaitTime = endWaitTimeElement.ValueKind == JsonValueKind.Number && endWaitTimeElement.TryGetInt32(out var endWaitTime) ? endWaitTime : 0,
            ConfirmLatencyMs = confirmLatencyMsElement.ValueKind == JsonValueKind.Number && confirmLatencyMsElement.TryGetInt32(out var confirmLatencyMs) ? confirmLatencyMs : 0,
            MaxGap = maxGapElement.ValueKind == JsonValueKind.Number && maxGapElement.TryGetInt32(out var maxGap) ? maxGap : 0,
            MaxSpread = maxSpreadElement.ValueKind == JsonValueKind.Number && maxSpreadElement.TryGetInt32(out var maxSpread) ? maxSpread : 0,
            OpenMaxTimesTick = openMaxTimesTickElement.ValueKind == JsonValueKind.Number && openMaxTimesTickElement.TryGetInt32(out var openMaxTimesTick) ? openMaxTimesTick : 0,
            CloseMaxTimesTick = closeMaxTimesTickElement.ValueKind == JsonValueKind.Number && closeMaxTimesTickElement.TryGetInt32(out var closeMaxTimesTick) ? closeMaxTimesTick : 0,
            OpenPendingTimeMs = openPendingTimeMsElement.ValueKind == JsonValueKind.Number && openPendingTimeMsElement.TryGetInt32(out var openPendingTimeMs) ? openPendingTimeMs : 0,
            ClosePendingTimeMs = closePendingTimeMsElement.ValueKind == JsonValueKind.Number && closePendingTimeMsElement.TryGetInt32(out var closePendingTimeMs) ? closePendingTimeMs : 0,
            DelayOpenAMs = delayOpenAMsElement.ValueKind == JsonValueKind.Number && delayOpenAMsElement.TryGetInt32(out var delayOpenAMs) ? delayOpenAMs : 0,
            DelayOpenBMs = delayOpenBMsElement.ValueKind == JsonValueKind.Number && delayOpenBMsElement.TryGetInt32(out var delayOpenBMs) ? delayOpenBMs : 0,
            DelayCloseAMs = delayCloseAMsElement.ValueKind == JsonValueKind.Number && delayCloseAMsElement.TryGetInt32(out var delayCloseAMs) ? delayCloseAMs : 0,
            DelayCloseBMs = delayCloseBMsElement.ValueKind == JsonValueKind.Number && delayCloseBMsElement.TryGetInt32(out var delayCloseBMs) ? delayCloseBMs : 0,
            OpenNumberOfQualifyingTimes = openQtElement.ValueKind == JsonValueKind.Number && openQtElement.TryGetInt32(out var openQt) ? openQt : 1,
            CloseNumberOfQualifyingTimes = closeQtElement.ValueKind == JsonValueKind.Number && closeQtElement.TryGetInt32(out var closeQt) ? closeQt : 1,
            OpenGapTick = openGapTickElement.ValueKind == JsonValueKind.Number && openGapTickElement.TryGetInt32(out var openGapTick) ? openGapTick : 0,
            CloseGapTick = closeGapTickElement.ValueKind == JsonValueKind.Number && closeGapTickElement.TryGetInt32(out var closeGapTick) ? closeGapTick : 0,
            CoolDownGapTick = coolDownGapTickElement.ValueKind == JsonValueKind.Number && coolDownGapTickElement.TryGetInt32(out var coolDownGapTick) ? coolDownGapTick : 0,
            PlatformA = platformAElement.ValueKind == JsonValueKind.String ? platformAElement.GetString() : null,
            PlatformB = platformBElement.ValueKind == JsonValueKind.String ? platformBElement.GetString() : null,
            IsShowConfig = isShowConfigElement.ValueKind == JsonValueKind.Number && isShowConfigElement.TryGetInt32(out var isShowConfig) ? isShowConfig : 0,
            CurrentTickA = currentTickAElement.ValueKind == JsonValueKind.String ? currentTickAElement.GetString() : null,
            CurrentTickB = currentTickBElement.ValueKind == JsonValueKind.String ? currentTickBElement.GetString() : null
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

        [JsonPropertyName("close_hold_confirm_ms")]
        public int CloseHoldConfirmMs { get; set; }

        [JsonPropertyName("close_price_freeze_ms")]
        public int ClosePriceFreezeMs { get; set; }

        [JsonPropertyName("start_time_hold")]
        public int StartTimeHold { get; set; }

        [JsonPropertyName("end_time_hold")]
        public int EndTimeHold { get; set; }

        [JsonPropertyName("start_wait_time")]
        public int StartWaitTime { get; set; }

        [JsonPropertyName("end_wait_time")]
        public int EndWaitTime { get; set; }

        [JsonPropertyName("confirm_latency")]
        public int ConfirmLatencyMs { get; set; }

        [JsonPropertyName("max_gap")]
        public int MaxGap { get; set; }

        [JsonPropertyName("max_spread")]
        public int MaxSpread { get; set; }

        [JsonPropertyName("open_max_times_tick")]
        public int OpenMaxTimesTick { get; set; }

        [JsonPropertyName("close_max_times_tick")]
        public int CloseMaxTimesTick { get; set; }

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

        [JsonPropertyName("open_gap_tick")]
        public int OpenGapTick { get; set; }

        [JsonPropertyName("close_gap_tick")]
        public int CloseGapTick { get; set; }

        [JsonPropertyName("cool_down_gap_tick")]
        public int CoolDownGapTick { get; set; }

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
    }
}
