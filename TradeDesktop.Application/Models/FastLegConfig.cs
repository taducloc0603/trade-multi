using System.Globalization;
using System.Text.Json;

namespace TradeDesktop.Application.Models;

public enum FastLegMode
{
    // Chỉ ghi verdict, không bao giờ chặn.
    Observe,
    // Verdict khác Confirmed ⇒ chặn Open (và hoãn Close khi ApplyToExit).
    Filter
}

// Cột JSONB `fast_leg` (sàn C, docs/plans/fast-leg). Sàn C chỉ cung cấp giá để lọc; KHÔNG bao giờ đặt lệnh.
// Chỉ hỗ trợ quy tắc "quiet" (tránh xung lượng): |move| của giá giữa sàn C trong WindowMs ≥ MaxMovePts ⇒ TooFast.
public sealed record FastLegConfig(
    bool Enabled,
    string MapName,
    // Map Trades của cùng EA: header ghi GetTickCount64 (offset 4) + TERMINAL_CONNECTED (offset 12) mỗi 50 ms,
    // dùng để tách Down (terminal chết / mất kết nối) khỏi Stale (sống nhưng không có tick).
    string HeartbeatMapName,
    // Rỗng ⇒ không kiểm symbol.
    string Symbol,
    FastLegMode Mode,
    decimal MaxMovePts,
    int WindowMs,
    int MaxAgeMs,
    // Trần hoãn một signal trong engine; quá trần ⇒ reset cycle (chống phình mẫu, CLAUDE.md §5).
    int MaxDeferMs,
    // Tha Down/Stale cho Open ở chế độ Filter. Close không bao giờ chờ Down/Stale (quyết định D6).
    bool FailOpen,
    // Hoãn cả Normal Close / TP khi TooFast. SOS Close luôn bỏ qua F.
    bool ApplyToExit)
{
    public const string RuleQuiet = "quiet";
    public const string DefaultMapName = @"Local\MT_C_Tick";
    public const decimal DefaultMaxMovePts = 10m;
    public const int DefaultWindowMs = 200;
    public const int DefaultMaxAgeMs = 1000;
    public const int DefaultMaxDeferMs = 3000;
    public const int MaxWindowMs = 5000;
    public const int MinMaxDeferMs = 500;
    public const int MaxMaxDeferMs = 10000;

    private const string TickSuffix = "_Tick";
    private const string TradesSuffix = "_Trades";

    public static FastLegConfig Disabled { get; } = new(
        Enabled: false,
        MapName: string.Empty,
        HeartbeatMapName: string.Empty,
        Symbol: string.Empty,
        Mode: FastLegMode.Observe,
        MaxMovePts: DefaultMaxMovePts,
        WindowMs: DefaultWindowMs,
        MaxAgeMs: DefaultMaxAgeMs,
        MaxDeferMs: DefaultMaxDeferMs,
        FailOpen: false,
        ApplyToExit: false);

    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(MapName);

    public bool IsFilter => IsActive && Mode == FastLegMode.Filter;

    /// <summary>
    /// Parse cột `fast_leg`. Rỗng / null ⇒ Disabled, không lỗi. JSON hỏng hoặc vi phạm ràng buộc ⇒ Disabled kèm
    /// <paramref name="error"/> (trả false). Không bao giờ ném lỗi: cấu hình F hỏng không được làm hỏng cả config.
    /// </summary>
    public static bool TryParse(
        string? json,
        string? mapName1,
        string? mapName2,
        out FastLegConfig config,
        out string? error)
    {
        config = Disabled;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"JSON không hợp lệ: {ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "fast_leg phải là JSON object.";
                return false;
            }

            var errors = new List<string>();
            var enabled = ReadBool(root, errors, false, "enabled");
            if (errors.Count > 0)
            {
                // "enabled":"yes" nghĩa là người cấu hình muốn bật: phải báo lỗi chứ không âm thầm coi là tắt.
                error = string.Join("; ", errors);
                return false;
            }

            if (!enabled)
            {
                // Tắt ⇒ không kiểm phần còn lại, để có thể giữ cấu hình dở dang trên DB mà không sinh lỗi.
                return true;
            }

            var mapName = ReadString(root, errors, DefaultMapName, "mapName", "map_name");
            var heartbeatMapName = ReadString(root, errors, DeriveHeartbeatMapName(mapName), "heartbeatMapName", "heartbeat_map_name");
            var symbol = ReadString(root, errors, string.Empty, "symbol");
            var modeText = ReadString(root, errors, "observe", "mode");
            var rule = ReadString(root, errors, RuleQuiet, "rule");
            var maxMovePts = ReadDecimal(root, errors, DefaultMaxMovePts, "maxMovePts", "max_move_pts");
            var windowMs = ReadInt(root, errors, DefaultWindowMs, "windowMs", "window_ms");
            var maxAgeMs = ReadInt(root, errors, DefaultMaxAgeMs, "maxAgeMs", "max_age_ms");
            var maxDeferMs = ReadInt(root, errors, DefaultMaxDeferMs, "maxDeferMs", "max_defer_ms");
            var failOpen = ReadBool(root, errors, false, "failOpen", "fail_open");
            var applyToExit = ReadBool(root, errors, false, "applyToExit", "apply_to_exit");

            FastLegMode mode = FastLegMode.Observe;
            if (string.Equals(modeText, "observe", StringComparison.OrdinalIgnoreCase))
            {
                mode = FastLegMode.Observe;
            }
            else if (string.Equals(modeText, "filter", StringComparison.OrdinalIgnoreCase))
            {
                mode = FastLegMode.Filter;
            }
            else
            {
                errors.Add($"mode '{modeText}' không hợp lệ (observe | filter)");
            }

            if (!string.Equals(rule, RuleQuiet, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"rule '{rule}' chưa được hỗ trợ (chỉ quiet)");
            }

            if (string.IsNullOrWhiteSpace(mapName))
            {
                errors.Add("mapName rỗng");
            }
            else if (IsSameMap(mapName, mapName1) || IsSameMap(mapName, mapName2))
            {
                // EA cùng kênh với A/B sẽ ghi đè giá chân đang giao dịch; đọc nhầm map A/B thì F không còn là nhân chứng độc lập.
                errors.Add($"mapName '{mapName}' trùng map của sàn A/B");
            }

            if (!string.IsNullOrWhiteSpace(heartbeatMapName)
                && (IsSameMap(heartbeatMapName, mapName)
                    || IsSameMap(heartbeatMapName, DeriveHeartbeatMapName(mapName1))
                    || IsSameMap(heartbeatMapName, DeriveHeartbeatMapName(mapName2))))
            {
                errors.Add($"heartbeatMapName '{heartbeatMapName}' trùng map tick hoặc map của sàn A/B");
            }

            if (maxMovePts <= 0m)
            {
                errors.Add("maxMovePts phải > 0");
            }

            if (windowMs < 1 || windowMs > MaxWindowMs)
            {
                errors.Add($"windowMs phải trong 1..{MaxWindowMs}");
            }

            if (maxAgeMs <= 0)
            {
                errors.Add("maxAgeMs phải > 0");
            }

            if (maxDeferMs < MinMaxDeferMs || maxDeferMs > MaxMaxDeferMs)
            {
                errors.Add($"maxDeferMs phải trong {MinMaxDeferMs}..{MaxMaxDeferMs}");
            }

            if (errors.Count > 0)
            {
                error = string.Join("; ", errors);
                return false;
            }

            config = new FastLegConfig(
                Enabled: true,
                MapName: mapName.Trim(),
                HeartbeatMapName: heartbeatMapName.Trim(),
                Symbol: symbol.Trim(),
                Mode: mode,
                MaxMovePts: maxMovePts,
                WindowMs: windowMs,
                MaxAgeMs: maxAgeMs,
                MaxDeferMs: maxDeferMs,
                FailOpen: failOpen,
                ApplyToExit: applyToExit);
            return true;
        }
    }

    /// <summary>
    /// Giá trị điền sẵn khi người dùng tick "Sàn C" lần đầu: thông số của Long nhưng ở chế độ observe
    /// (chỉ ghi verdict, không chặn) — chuyển sang filter là quyết định có chủ ý của người cấu hình.
    /// </summary>
    public static FastLegConfig FormDefaults { get; } = new(
        Enabled: true,
        MapName: DefaultMapName,
        HeartbeatMapName: DeriveHeartbeatMapName(DefaultMapName),
        Symbol: string.Empty,
        Mode: FastLegMode.Observe,
        MaxMovePts: 5m,
        WindowMs: DefaultWindowMs,
        MaxAgeMs: DefaultMaxAgeMs,
        MaxDeferMs: DefaultMaxDeferMs,
        FailOpen: false,
        ApplyToExit: true);

    /// <summary>
    /// JSON cho cột `fast_leg` (cùng tên khoá mà <see cref="TryParse"/> đọc). <paramref name="enabled"/> = false giữ
    /// nguyên các thông số để tick lại thì không phải nhập lại; TryParse bỏ qua phần còn lại khi enabled = false.
    /// </summary>
    public string ToJson(bool enabled) => JsonSerializer.Serialize(new
    {
        enabled,
        mapName = MapName,
        heartbeatMapName = HeartbeatMapName,
        symbol = Symbol,
        mode = Mode == FastLegMode.Filter ? "filter" : "observe",
        rule = RuleQuiet,
        maxMovePts = MaxMovePts,
        windowMs = WindowMs,
        maxAgeMs = MaxAgeMs,
        maxDeferMs = MaxDeferMs,
        failOpen = FailOpen,
        applyToExit = ApplyToExit
    });

    /// <summary>
    /// Đọc thông số đã lưu để điền form, KỂ CẢ khi enabled = false (TryParse trả Disabled trong trường hợp đó).
    /// Rỗng / hỏng / vi phạm ràng buộc ⇒ null (form dùng <see cref="FormDefaults"/>). Không ném lỗi.
    /// </summary>
    public static FastLegConfig? ReadDraft(string? json, string? mapName1, string? mapName2)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject node)
            {
                return null;
            }

            node["enabled"] = true;
            return TryParse(node.ToJsonString(), mapName1, mapName2, out var draft, out _) && draft.IsActive
                ? draft
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Chuỗi ngắn cho dòng RuntimeSummary / [DB] / [FAST_LEG][INFO].</summary>
    public string ToSummary()
    {
        if (!IsActive)
        {
            return "disabled";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(Mode == FastLegMode.Filter ? "filter" : "observe")}|{RuleQuiet}|move{MaxMovePts}|win{WindowMs}|age{MaxAgeMs}|defer{MaxDeferMs}|failOpen{(FailOpen ? 1 : 0)}|exit{(ApplyToExit ? 1 : 0)}|map={MapName}|hb={(string.IsNullOrEmpty(HeartbeatMapName) ? "n/a" : HeartbeatMapName)}|sym={(string.IsNullOrEmpty(Symbol) ? "*" : Symbol)}");
    }

    // Local\MT_C_Tick ⇒ Local\MT_C_Trades (cùng quy ước đặt tên của EA DataExporter). Không theo quy ước ⇒ rỗng.
    public static string DeriveHeartbeatMapName(string? tickMapName)
    {
        var name = (tickMapName ?? string.Empty).Trim();
        return name.EndsWith(TickSuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^TickSuffix.Length] + TradesSuffix
            : string.Empty;
    }

    private static bool IsSameMap(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool TryGet(JsonElement root, string[] names, out JsonElement value, out string name)
    {
        foreach (var candidate in names)
        {
            if (root.TryGetProperty(candidate, out value) && value.ValueKind != JsonValueKind.Null)
            {
                name = candidate;
                return true;
            }
        }

        value = default;
        name = names[0];
        return false;
    }

    private static bool ReadBool(JsonElement root, List<string> errors, bool fallback, params string[] names)
    {
        if (!TryGet(root, names, out var value, out var name))
        {
            return fallback;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        errors.Add($"{name} phải là true/false");
        return fallback;
    }

    private static string ReadString(JsonElement root, List<string> errors, string fallback, params string[] names)
    {
        if (!TryGet(root, names, out var value, out var name))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? fallback;
        }

        errors.Add($"{name} phải là chuỗi");
        return fallback;
    }

    private static int ReadInt(JsonElement root, List<string> errors, int fallback, params string[] names)
    {
        if (!TryGet(root, names, out var value, out var name))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result))
        {
            return result;
        }

        errors.Add($"{name} phải là số nguyên");
        return fallback;
    }

    private static decimal ReadDecimal(JsonElement root, List<string> errors, decimal fallback, params string[] names)
    {
        if (!TryGet(root, names, out var value, out var name))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var result))
        {
            return result;
        }

        errors.Add($"{name} phải là số");
        return fallback;
    }
}
