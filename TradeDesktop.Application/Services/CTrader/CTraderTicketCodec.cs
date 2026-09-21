namespace TradeDesktop.Application.Services.CTrader;

// R4: _pairIdByTicket, _openRequestByTicket… key bằng ulong trần, không kèm sàn. positionId cTrader là bộ đếm
// riêng nên có thể trùng ticket MT → cross-wire pair → đóng nhầm lệnh. Gắn bit 62 làm namespace.
// Dạng đã mã hoá là dạng lưu current_slots và round-trip qua recovery; executor decode khi ghi tag 721.
public static class CTraderTicketCodec
{
    public const ulong Namespace = 0x4000_0000_0000_0000UL;

    // Bit 63 dành trống để giá trị luôn dương khi ai đó ép sang long.
    private const ulong ReservedHighBits = 0xC000_0000_0000_0000UL;

    public const long MaxPositionId = (long)(Namespace - 1);

    public static ulong Encode(long positionId)
    {
        if (positionId < 0 || positionId > MaxPositionId)
        {
            throw new ArgumentOutOfRangeException(nameof(positionId), positionId,
                $"positionId phải trong [0, {MaxPositionId}] để không đụng bit namespace.");
        }

        return Namespace | (ulong)positionId;
    }

    public static bool TryDecode(ulong ticket, out long positionId)
    {
        if ((ticket & ReservedHighBits) != Namespace)
        {
            positionId = 0;
            return false;
        }

        positionId = (long)(ticket & ~ReservedHighBits);
        return true;
    }

    public static bool IsCTraderTicket(ulong ticket) => (ticket & ReservedHighBits) == Namespace;
}
