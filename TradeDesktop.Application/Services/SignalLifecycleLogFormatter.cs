using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services;

public static class SignalLifecycleLogFormatter
{
    public static SignalLogItem Create(
        string eventType,
        string description,
        SignalLogLevel level = SignalLogLevel.Info,
        params (string Name, object? Value)[] fields)
        => new(
            DateTime.Now,
            eventType,
            level,
            description,
            string.Join(" ", fields
                .Where(field => field.Value is not null)
                .Select(field => SignalLogItem.Field(field.Name, field.Value))));

    public static string DescriptionForReason(string reasonCode, string action = "thực hiện")
        => reasonCode switch
        {
            "QUOTA_TOTAL_FULL" => "Không thể mở vì tổng số vị thế đã đạt giới hạn",
            "QUOTA_BUY_FULL" => "Không thể mở vì quota Buy đã đầy",
            "QUOTA_SELL_FULL" => "Không thể mở vì quota Sell đã đầy",
            "NO_AVAILABLE_SLOT" => "Không thể mở vì không còn slot trống",
            "UNRESOLVED_PENDING_CYCLE" => "Không thể mở vì chu kỳ trước chưa hoàn tất",
            "COOLDOWN_ACTIVE" => $"Không thể {action} vì hệ thống đang trong thời gian cooldown",
            "GLOBAL_ACTION_COOLDOWN" => $"Không thể {action} vì hệ thống đang trong thời gian cooldown toàn cục",
            "POST_CLOSE_OPEN_LOCK" => "Chưa thể mở vị thế mới vì đang trong thời gian khóa sau khi đóng",
            "SAME_SIDE_OPEN_RANDOM_LOCK" => "Chưa thể mở thêm vị thế cùng chiều trong thời gian khóa ngẫu nhiên",
            "OPEN_TO_CLOSE_RANDOM_LOCK" => "Chưa thể đóng vị thế vì đang trong thời gian khóa sau Auto Open gần nhất",
            "CLOSE_TO_CLOSE_RANDOM_LOCK" => "Chưa thể đóng vị thế tiếp theo trong thời gian khóa ngẫu nhiên",
            "PER_SLOT_POST_OPEN_LOCK" => "Chưa thể đóng vì vị thế vẫn đang trong thời gian giữ tối thiểu",
            "NON_AUTO_CLOSE_IN_FLIGHT" => $"Không thể {action} khi thao tác đóng lệnh thủ công hoặc khôi phục đang chạy",
            "AUTO_ACTION_CONTEXT_INVALID" => $"Không thể {action} vì ngữ cảnh thực thi tự động không hợp lệ",
            "AUTO_CLOSE_SLOT_CONTEXT_INVALID" => "Không thể đóng vì slot mục tiêu không còn hợp lệ hoặc chưa xác nhận mở",
            "OPPOSITE_SIDE_LOCK" => "Không thể mở vì hướng giao dịch đang bị khóa tạm thời",
            "TRADE_GATE_BLOCKED" => $"Không thể {action} vì execution gate đang bị khóa",
            "TRADE_POLICY_BLOCKED" => $"Không thể {action} vì chính sách giao dịch từ chối yêu cầu",
            "SIGNAL_EXPIRED" => "Hủy tín hiệu vì đã hết hạn trong thời gian chờ",
            "LATENCY_GUARD" => "Không thể mở vì độ trễ dữ liệu vượt mức an toàn",
            "MAX_GAP_GUARD" => "Không thể mở vì Gap vượt giới hạn an toàn",
            "SPREAD_GUARD" => "Không thể mở vì Spread vượt giới hạn cho phép",
            "PRICE_FREEZE_GUARD" => "Không thể mở vì giá đứng hoặc dữ liệu giá không hợp lệ",
            "CONNECTION_UNHEALTHY" => $"Không thể {action} vì kết nối không ổn định",
            "HWND_INVALID" => $"Không thể {action} vì HWND của nền tảng không hợp lệ",
            "WATCHDOG_PAUSED" => "Không thể mở vì Watchdog đang tạm dừng giao dịch",
            "TRADING_STOPPED" => $"Không thể {action} vì Trading Logic đang dừng",
            "SIDE_DISABLED" => "Không thể mở vì hướng tín hiệu đang bị tắt trên giao diện",
            "DUPLICATE_SIGNAL" => "Bỏ qua vì tín hiệu đã được xử lý trước đó",
            "MIN_PROFIT_WAITING" => "Chưa thể đóng vì lợi nhuận chưa đạt mức tối thiểu",
            "ORIGINAL_POSITION_CLOSED" => "Hủy Hedge vì vị thế gốc đã đóng trước khi gửi lệnh",
            "ORIGINAL_SLOT_NOT_FOUND" => "Hủy Hedge vì không còn tìm thấy slot gốc",
            "ORIGINAL_SIDE_CHANGED" => "Hủy Hedge vì hướng của vị thế gốc đã thay đổi",
            "POSITION_ALREADY_CLOSED" => "Hủy đóng vì vị thế đã được đóng trước đó",
            "QUALIFYING_NOT_REACHED" => "Chưa thực hiện vì tín hiệu chưa đủ số lần xác nhận",
            "PARTIAL_OPEN" => "Mở vị thế thất bại vì chỉ một chân thành công",
            "PARTIAL_CLOSE" => "Đóng vị thế thất bại vì một chân vẫn còn mở",
            "CONFIRMATION_TIMEOUT" => "Thực thi thất bại vì không nhận đủ xác nhận trong thời hạn",
            "EXECUTION_FAILED" => $"{char.ToUpperInvariant(action[0])}{action[1..]} thất bại trên cả hai chân",
            _ => $"Không thể {action}: {reasonCode}"
        };
}
