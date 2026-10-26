namespace TradeDesktop.Application.Services.TradeLocker;

// R4: ticket chân B lưu trong current_slots / _pairIdByTicket là ulong trần, không kèm sàn ⇒ mỗi nền tảng B gắn một
// namespace. Position id TradeLocker là chuỗi số kiểu snowflake (~9e17 năm 2026, tăng theo thời gian) nên KHÔNG
// nhét vừa 60 bit lâu dài; bit 61 = PrimeXBT, bit 62 = cTrader, 0x6000… bị codec cTrader nhận nhầm. Vì vậy dùng
// BIT 63 (bit cao nhất = 1). Hai codec kia đều đòi bit 63 = 0 nên loại trừ nhau; ticket MT không bao giờ có bit 63.
// Ticket là ulong ở mọi tầng (TradeSharedRecord, PositionSlot, SlotPersistence, current_tick_* dạng chuỗi) — đã rà
// 2026-10-26, không chỗ nào ép sang long. Đừng ép ticket TradeLocker sang long: giá trị sẽ âm.
public static class TradeLockerTicketCodec
{
    public const ulong Namespace = 0x8000_0000_0000_0000UL;

    public const long MaxPositionId = long.MaxValue;

    public static ulong Encode(long positionId)
    {
        if (positionId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(positionId), positionId, "positionId TradeLocker phải > 0.");
        }

        return Namespace | (ulong)positionId;
    }

    // Wire gửi id dạng chuỗi số; chuỗi lạ / vượt long / ≤ 0 ⇒ false (không đoán).
    public static bool TryEncode(string? positionId, out ulong ticket)
    {
        if (long.TryParse(positionId, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            ticket = Namespace | (ulong)id;
            return true;
        }

        ticket = 0;
        return false;
    }

    public static bool TryDecode(ulong ticket, out long positionId)
    {
        if ((ticket & Namespace) == 0 || (ticket & ~Namespace) == 0)
        {
            positionId = 0;
            return false;
        }

        positionId = (long)(ticket & ~Namespace);
        return true;
    }

    // Dạng chuỗi gửi lên route `v3.positions.{id}:close`.
    public static bool TryDecodeToWireId(ulong ticket, out string positionId)
    {
        if (TryDecode(ticket, out var id))
        {
            positionId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        positionId = string.Empty;
        return false;
    }

    public static bool IsTradeLockerTicket(ulong ticket) => TryDecode(ticket, out _);
}
