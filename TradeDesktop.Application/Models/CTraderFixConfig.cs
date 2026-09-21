namespace TradeDesktop.Application.Models;

public enum CTraderSessionRole
{
    Quote = 0,
    Trade = 1
}

public sealed record CTraderEndpoint(string Host, int PortSsl, int PortPlain)
{
    public static CTraderEndpoint Empty { get; } = new(string.Empty, 0, 0);

    public CTraderEndpoint Normalize()
        => new((Host ?? string.Empty).Trim(), Math.Max(0, PortSsl), Math.Max(0, PortPlain));
}

// Thông số FIX API của sàn B, lưu trong khối `ctraderFix` của configs.sans_json.
// Password lưu plaintext theo quyết định chủ dự án → ToString() PHẢI che, không được log record này thô.
public sealed record CTraderFixConfig(
    CTraderEndpoint Quote,
    CTraderEndpoint Trade,
    bool UseSsl,
    string SenderCompId,
    string TargetCompId,
    string Password,
    string Username,
    int SymbolId,
    string SymbolName,
    decimal VolumeBUnits,
    decimal ContractSizeB,
    decimal VolumeALots)
{
    // Tên kênh B cố định khi platform_b = ctrader (Phase 2 câu 3). Không có MMF nào mang tên này nên
    // reader MMF không thể đọc nhầm EA MT sàn B còn đang chạy.
    public const string ChannelMapName = "CTRADER_B";

    public const string DefaultTargetCompId = "cServer";

    public static CTraderFixConfig Empty { get; } = new(
        CTraderEndpoint.Empty,
        CTraderEndpoint.Empty,
        UseSsl: true,
        SenderCompId: string.Empty,
        TargetCompId: DefaultTargetCompId,
        Password: string.Empty,
        Username: string.Empty,
        SymbolId: 0,
        SymbolName: string.Empty,
        VolumeBUnits: 0m,
        ContractSizeB: 0m,
        VolumeALots: 0m);

    public bool HasPassword => !string.IsNullOrEmpty(Password);

    public bool IsDemoSender =>
        (SenderCompId ?? string.Empty).Trim().StartsWith("demo.", StringComparison.OrdinalIgnoreCase);

    public CTraderFixConfig Normalize()
        => new(
            (Quote ?? CTraderEndpoint.Empty).Normalize(),
            (Trade ?? CTraderEndpoint.Empty).Normalize(),
            UseSsl,
            (SenderCompId ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(TargetCompId) ? DefaultTargetCompId : TargetCompId.Trim(),
            Password ?? string.Empty,
            (Username ?? string.Empty).Trim(),
            Math.Max(0, SymbolId),
            (SymbolName ?? string.Empty).Trim(),
            Math.Max(0m, VolumeBUnits),
            Math.Max(0m, ContractSizeB),
            Math.Max(0m, VolumeALots));

    // Username tag 553: ưu tiên giá trị ghi đè; rỗng thì lấy đoạn cuối của `<env>.<broker>.<login>`.
    public string ResolveUsername()
    {
        if (!string.IsNullOrWhiteSpace(Username))
        {
            return Username.Trim();
        }

        var parts = (SenderCompId ?? string.Empty).Trim().Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^1] : string.Empty;
    }

    public int ActivePort(CTraderSessionRole role)
    {
        var endpoint = role == CTraderSessionRole.Quote ? Quote : Trade;
        return UseSsl ? endpoint.PortSsl : endpoint.PortPlain;
    }

    public static bool IsValidVolumeBUnits(decimal value) => value > 0m && value % 0.01m == 0m;

    public static bool IsValidContractSize(decimal value) => value > 0m;

    // Trường bắt buộc khi platform_b = ctrader. passwordAvailable = đã lưu HOẶC vừa nhập.
    public IReadOnlyList<string> GetMissingRequiredFields(bool passwordAvailable)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Quote?.Host)) missing.Add("QUOTE host");
        if (ActivePort(CTraderSessionRole.Quote) <= 0) missing.Add(UseSsl ? "QUOTE port SSL" : "QUOTE port plain");
        if (string.IsNullOrWhiteSpace(Trade?.Host)) missing.Add("TRADE host");
        if (ActivePort(CTraderSessionRole.Trade) <= 0) missing.Add(UseSsl ? "TRADE port SSL" : "TRADE port plain");
        if (string.IsNullOrWhiteSpace(SenderCompId)) missing.Add("SenderCompID");
        if (SymbolId <= 0) missing.Add("Symbol ID");
        if (!IsValidVolumeBUnits(VolumeBUnits)) missing.Add("Volume B (units)");
        if (!IsValidContractSize(ContractSizeB)) missing.Add("Contract size B");
        if (!passwordAvailable) missing.Add("Password");
        return missing;
    }

    public override string ToString()
        => $"CTraderFixConfig {{ Quote = {Quote}, Trade = {Trade}, UseSsl = {UseSsl}, " +
           $"SenderCompId = {SenderCompId}, TargetCompId = {TargetCompId}, " +
           $"Password = {(HasPassword ? "***" : "(empty)")}, Username = {Username}, SymbolId = {SymbolId}, " +
           $"SymbolName = {SymbolName}, VolumeBUnits = {VolumeBUnits}, ContractSizeB = {ContractSizeB}, " +
           $"VolumeALots = {VolumeALots} }}";
}
