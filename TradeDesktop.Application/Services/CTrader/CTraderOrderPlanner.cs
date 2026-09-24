using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Services.CTrader;

// Phần QUYẾT ĐỊNH của executor sàn B, tách khỏi tầng App để unit-test được (TradeDesktop.Tests không tham
// chiếu TradeDesktop.App). Thuần hàm: không I/O, không trạng thái, không gửi gì.
//
// Rule E: lớp này chỉ DỰNG yêu cầu khi được ra lệnh. Nó không quyết định CÓ nên vào lệnh hay không —
// việc đó là của signal engine và router.
public static class CTraderOrderPlanner
{
    public sealed record Plan(CTraderOrderRequest? Request, string? Error)
    {
        public bool IsValid => Request is not null;

        public static Plan Ok(CTraderOrderRequest request) => new(request, null);

        public static Plan Fail(string error) => new(null, error);
    }

    public static Plan PlanOpen(CTraderFixConfig config, bool isBuy, string clOrdId)
    {
        var volume = ValidateVolume(config);
        return volume is not null
            ? Plan.Fail(volume)
            // Không có 721 → sàn tạo position mới.
            : Plan.Ok(new CTraderOrderRequest(clOrdId, isBuy, config.VolumeBUnits, PositionId: null));
    }

    // `position` là chiều + khối lượng của position đang mở, lấy từ cache session. null = không tìm thấy.
    public static Plan PlanClose(
        CTraderFixConfig config,
        ulong ticket,
        (bool IsBuy, decimal QuantityUnits)? position,
        string clOrdId)
    {
        // R4: ticket sàn B mang bit namespace 62. Ticket lạ = nguy cơ cross-wire đóng nhầm lệnh MT → fail closed.
        if (!CTraderTicketCodec.TryDecode(ticket, out var positionId))
        {
            return Plan.Fail($"Ticket {ticket} không thuộc namespace cTrader");
        }

        // Không đoán khối lượng: thiếu dữ liệu position thì KHÔNG gửi lệnh.
        if (position is null)
        {
            return Plan.Fail($"Position {positionId} không còn trong cache — không đóng mò");
        }

        if (position.Value.QuantityUnits <= 0m)
        {
            return Plan.Fail($"Position {positionId} có khối lượng không hợp lệ: {position.Value.QuantityUnits}");
        }

        var volume = ValidateVolume(config);
        if (volume is not null)
        {
            return Plan.Fail(volume);
        }

        // Đóng = side NGƯỢC + ĐỦ volume của chính position + 721.
        return Plan.Ok(new CTraderOrderRequest(clOrdId, !position.Value.IsBuy, position.Value.QuantityUnits, positionId));
    }

    // R5: tag 38 tính bằng ĐƠN VỊ CƠ SỞ, không phải lot — sai đơn vị là đặt sai khối lượng gấp contractSize lần.
    private static string? ValidateVolume(CTraderFixConfig config)
    {
        if (config.VolumeBUnits <= 0m || config.VolumeBUnits != decimal.Round(config.VolumeBUnits, 2))
        {
            return $"volumeBUnits không hợp lệ: {config.VolumeBUnits}";
        }

        return config.ContractSizeB <= 0m ? $"contractSizeB không hợp lệ: {config.ContractSizeB}" : null;
    }
}
