using QuickFix;

namespace TradeDesktop.Infrastructure.CTrader;

public sealed record CTraderPosition(
    long PositionId,
    int SymbolId,
    bool IsBuy,
    decimal VolumeUnits,
    decimal EntryPrice,
    ulong OpenTimeMsc,
    ulong OpenEaTimeLocal);

// PosReqResult 728: 0 = có dữ liệu, 2 = KHÔNG có position (tài khoản trống) — vẫn là batch hoàn tất.
public sealed record CTraderPositionReport(
    string PosReqId,
    int TotalNumPosReports,
    int PosReqResult,
    CTraderPosition? Position)
{
    public const int ResultValid = 0;
    public const int ResultNoPositions = 2;
}

// PositionReport (35=AP) theo MessageExtensions.GetPosition của Spotware: group NoPositions(702) →
// 704 LongQty / 705 ShortQty (bên lớn hơn là side), 730 SettlPrice = giá mở trung bình, 721 = positionId.
// AP không có thời điểm mở (R10) → OpenTimeMsc = 0; không có execution nên OpenEaTimeLocal = 0.
public static class CTraderPositionReportParser
{
    public static bool TryParse(Message message, out CTraderPositionReport report)
    {
        report = null!;
        if (FixFieldReader.MsgType(message) != "AP")
        {
            return false;
        }

        var posReqId = FixFieldReader.String(message, 710) ?? string.Empty;
        var total = FixFieldReader.Int(message, 727) ?? 0;
        var result = FixFieldReader.Int(message, 728) ?? CTraderPositionReport.ResultValid;

        CTraderPosition? position = null;
        if (result == CTraderPositionReport.ResultValid)
        {
            var positionId = FixFieldReader.Long(message, 721);
            var symbolId = FixFieldReader.Int(message, 55);
            var group = FixFieldReader.Groups(message, 702).FirstOrDefault();
            FieldMap volumeSource = group is null ? message : group;
            var longQty = FixFieldReader.Decimal(volumeSource, 704) ?? 0m;
            var shortQty = FixFieldReader.Decimal(volumeSource, 705) ?? 0m;

            if (positionId is not null && symbolId is not null && (longQty > 0m || shortQty > 0m))
            {
                var isBuy = longQty > shortQty;
                position = new CTraderPosition(
                    positionId.Value,
                    symbolId.Value,
                    isBuy,
                    isBuy ? longQty : shortQty,
                    FixFieldReader.Decimal(message, 730) ?? 0m,
                    OpenTimeMsc: 0,
                    OpenEaTimeLocal: 0);
            }
        }

        report = new CTraderPositionReport(posReqId, total, result, position);
        return true;
    }
}
