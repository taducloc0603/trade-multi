using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Abstractions;

// Hỏi phán quyết sàn C (docs/plans/fast-leg). Chỉ đọc; không có đường nào tới đặt lệnh (CLAUDE.md Rule E).
public interface IFastLegGate
{
    /// <summary>null khi F tắt: caller phải giữ hành vi y hệt bản chưa có Fast Leg.</summary>
    FastLegVerdict? EvaluateNow();

    /// <summary>Tổng số tick sàn C đã nhận, để tính tick/phút trong dòng STATS.</summary>
    long TotalTicks { get; }
}

public interface IFastLegSampler
{
    /// <summary>Idempotent. Luôn chạy; tự nghỉ khi F tắt nên hỗ trợ đổi cấu hình qua Reconnect mà không restart app.</summary>
    void Start();
}
