namespace TradeDesktop.Application.Models;

// Khối `tradelocker` trong sans_json (docs/plans/tradelocker Phase 2). KHÔNG chứa bí mật: refresh token Keycloak lưu
// cục bộ bằng DPAPI (TradeLockerTokenStore), không bao giờ lên Supabase.
// Khối lượng sàn B tính bằng LOT (quantity trên wire là lot); 1 lot XAUUSD = lot_size 100 oz trên OPEN_DEMO.
public sealed record TradeLockerConfig(
    // accId dạng chuỗi số (vd "1791342294192995800"), KHÔNG phải "PRDTL#O#…". Rỗng ⇒ dùng account đầu tiên của profile.
    string AccountId,
    // "ODA brand" gửi kèm X-ODA-Brand khi liệt kê account; demo của TradeLocker là PRDTL.
    string Brand,
    string Symbol,
    decimal VolumeBLots,
    decimal ContractSizeB,
    decimal VolumeALots,
    // Ngưỡng latency riêng cho chân B (ms, tuổi tick). null ⇒ dùng chung confirm_latency. KHÔNG clamp: 0 = tắt.
    int? ConfirmLatencyB)
{
    public const string DefaultBrand = "PRDTL";
    public const string DefaultSymbol = "XAUUSD";
    public const decimal DefaultContractSizeB = 100m;
    public const decimal VolumeStep = 0.01m;

    public static TradeLockerConfig Empty { get; } = new(string.Empty, string.Empty, string.Empty, 0m, 0m, 0m, null);

    public TradeLockerConfig Normalize() => this with
    {
        AccountId = (AccountId ?? string.Empty).Trim(),
        Brand = (Brand ?? string.Empty).Trim().ToUpperInvariant(),
        Symbol = (Symbol ?? string.Empty).Trim().ToUpperInvariant(),
        VolumeBLots = Math.Max(0m, VolumeBLots),
        ContractSizeB = Math.Max(0m, ContractSizeB),
        VolumeALots = Math.Max(0m, VolumeALots)
    };

    // Brand rỗng ⇒ PRDTL (đúng giá trị duy nhất đã kiểm chứng ở Phase 0).
    public string EffectiveBrand => string.IsNullOrWhiteSpace(Brand) ? DefaultBrand : Brand;

    public static bool IsValidAccountId(string? value)
        => string.IsNullOrEmpty(value) || (value.All(char.IsAsciiDigit) && value.Length <= 19);

    public static bool IsValidVolumeBLots(decimal value) => value > 0m && value % VolumeStep == 0m;

    public static bool IsValidContractSize(decimal value) => value > 0m;

    public IReadOnlyList<string> GetMissingRequiredFields()
    {
        var missing = new List<string>();
        if (!IsValidAccountId(AccountId)) missing.Add("Account ID (chuỗi số)");
        if (string.IsNullOrWhiteSpace(Symbol)) missing.Add("Symbol");
        if (!IsValidVolumeBLots(VolumeBLots)) missing.Add("Volume B (lot)");
        if (!IsValidContractSize(ContractSizeB)) missing.Add("Contract size B");
        return missing;
    }
}
