namespace TradeDesktop.App.Services;

// docs/plans/primexbt Phase 8.1 — executor tự báo "chưa nên mở chân mới" (vd. phiên PrimeXBT sắp hết hạn mà refresh JWT đang
// thất bại). Router hỏi TRƯỚC khi dispatch bất kỳ chân nào ⇒ chặn cả cặp, không sinh partial-open. CHỈ chặn Open; Close
// không hỏi. Executor không cài interface này (MT4/MT5/cTrader) ⇒ hành vi cũ không đổi.
public interface ITradeLegOpenReadiness
{
    // null = sẵn sàng; khác null = lý do chặn (đưa vào log policy).
    string? GetOpenBlockReason();
}
