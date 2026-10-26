namespace TradeDesktop.Application.Abstractions;

// Phiên đăng nhập TradeLocker (docs/plans/tradelocker Phase 0/2): refresh token Keycloak (offline_access, ~30 ngày)
// là thứ duy nhất cần giữ; access token (~30') và account JWT (5') đều sinh lại được từ nó.
// BÍ MẬT — chỉ lưu cục bộ (DPAPI), không log, không lên Supabase.
public sealed record TradeLockerSession(
    string RefreshToken,
    string AccessToken,
    DateTime SavedUtc)
{
    // Refresh token Keycloak là JWT (typ=Refresh) nên đọc được hạn; access token cũng vậy.
    public DateTime? RefreshExpiresUtc => PrimeXbtJwt.ReadExpiryUtc(RefreshToken);

    public DateTime? AccessExpiresUtc => PrimeXbtJwt.ReadExpiryUtc(AccessToken);

    public bool IsUsableAt(DateTime utcNow) => RefreshExpiresUtc is { } expires && expires > utcNow;

    // Không bao giờ in token: chỉ hạn.
    public override string ToString() =>
        $"TradeLockerSession {{ RefreshExpiresUtc = {RefreshExpiresUtc:yyyy-MM-dd HH:mm}Z, AccessExpiresUtc = {AccessExpiresUtc:yyyy-MM-dd HH:mm}Z, SavedUtc = {SavedUtc:yyyy-MM-dd HH:mm}Z }}";
}

public interface ITradeLockerTokenStore
{
    // null khi chưa đăng nhập, file hỏng hoặc không giải mã được (khác máy/khác user Windows). Không throw.
    TradeLockerSession? Load();

    void Save(TradeLockerSession session);

    void Clear();
}
