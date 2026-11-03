using System.Globalization;

namespace TradeDesktop.Application.Models;

public enum FastLegVerdictKind
{
    // Sàn C yên trong cửa sổ đo.
    Confirmed,
    // |move| trong cửa sổ đo >= MaxMovePts.
    TooFast,
    // Terminal C còn sống (hoặc không có heartbeat) nhưng chưa có tick / tick cũ hơn MaxAgeMs.
    Stale,
    // Không có nhân chứng: tắt cấu hình, thiếu map, sai version/symbol, heartbeat cũ, terminal mất kết nối, point sai.
    Down
}

public enum FastLegHeartbeatState
{
    // Không cấu hình map heartbeat hoặc map chưa có.
    NotAvailable,
    Alive,
    Dead
}

// Phán quyết sàn C tại một thời điểm. Chỉ để chặn/hoãn signal đã có; KHÔNG bao giờ tạo lệnh (CLAUDE.md Rule E).
public sealed record FastLegVerdict(
    FastLegVerdictKind Kind,
    decimal MovePts,
    decimal VelocityPtsPerSec,
    long SpanMs,
    long? AgeMs,
    decimal? CBid,
    decimal? CAsk,
    FastLegMode Mode,
    bool BlocksOpen,
    bool BlocksClose,
    string? DownReason,
    FastLegHeartbeatState HeartbeatState,
    // Trần hoãn của cấu hình sinh ra verdict này; engine dùng để bỏ cycle khi hoãn quá lâu (quyết định D8).
    int MaxDeferMs = FastLegConfig.DefaultMaxDeferMs)
{
    // Giá trị dạng key=value cho log, cùng thứ tự ở mọi nơi để parser đọc được.
    public string ToLogFields() => string.Create(
        CultureInfo.InvariantCulture,
        $"verdict={Kind} move_pts={MovePts:0.##} velocity_pts_s={VelocityPtsPerSec:0.#} span_ms={SpanMs} age_ms={(AgeMs.HasValue ? AgeMs.Value.ToString(CultureInfo.InvariantCulture) : "-")} c_bid={FormatPrice(CBid)} c_ask={FormatPrice(CAsk)} hb={FormatHeartbeat(HeartbeatState)} down={DownReason ?? "-"}");

    private static string FormatPrice(decimal? price)
        => price.HasValue ? price.Value.ToString("0.#####", CultureInfo.InvariantCulture) : "-";

    private static string FormatHeartbeat(FastLegHeartbeatState state) => state switch
    {
        FastLegHeartbeatState.Alive => "alive",
        FastLegHeartbeatState.Dead => "dead",
        _ => "n/a"
    };
}
