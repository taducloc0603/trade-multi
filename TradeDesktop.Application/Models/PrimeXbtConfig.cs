namespace TradeDesktop.Application.Models;

// Khối `primexbt` trong sans_json (docs/plans/primexbt Phase 2). KHÔNG chứa bí mật: token/cookie đăng nhập lưu
// cục bộ bằng DPAPI (PrimeXbtTokenStore), không bao giờ lên Supabase.
// Khối lượng sàn B tính bằng OUNCE (lotUnit của PXTrader 2.0), không phải lot: 1 lot MT XAUUSD = 100 oz.
public sealed record PrimeXbtConfig(
    string AccountId,
    string Symbol,
    decimal VolumeBOz,
    decimal ContractSizeB,
    decimal VolumeALots,
    // Ngưỡng latency riêng cho chân B (ms, tuổi tick). null ⇒ dùng chung confirm_latency. KHÔNG clamp: 0 = tắt.
    int? ConfirmLatencyB)
{
    public const string DefaultSymbol = "XAU/USD";
    public const decimal DefaultContractSizeB = 100m;
    public const decimal VolumeStep = 0.01m;

    public static PrimeXbtConfig Empty { get; } = new(string.Empty, string.Empty, 0m, 0m, 0m, null);

    // Tài khoản PXTrader 2.0: demo "#D…", thật "#L…".
    public bool IsDemoAccount => AccountId.StartsWith("D", StringComparison.OrdinalIgnoreCase);

    public PrimeXbtConfig Normalize() => this with
    {
        AccountId = (AccountId ?? string.Empty).Trim().TrimStart('#').ToUpperInvariant(),
        Symbol = (Symbol ?? string.Empty).Trim(),
        VolumeBOz = Math.Max(0m, VolumeBOz),
        ContractSizeB = Math.Max(0m, ContractSizeB),
        VolumeALots = Math.Max(0m, VolumeALots)
    };

    public static bool IsValidVolumeBOz(decimal value) => value > 0m && value % VolumeStep == 0m;

    public static bool IsValidContractSize(decimal value) => value > 0m;

    public IReadOnlyList<string> GetMissingRequiredFields()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(AccountId)) missing.Add("Account ID");
        if (string.IsNullOrWhiteSpace(Symbol)) missing.Add("Symbol");
        if (!IsValidVolumeBOz(VolumeBOz)) missing.Add("Volume B (oz)");
        if (!IsValidContractSize(ContractSizeB)) missing.Add("Contract size B");
        return missing;
    }
}
