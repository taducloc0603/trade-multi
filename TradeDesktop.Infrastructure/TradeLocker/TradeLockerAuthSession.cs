using TradeDesktop.Application.Abstractions;
using TradeDesktop.Application.Models;
using TradeDesktop.Application.Services.TradeLocker;

namespace TradeDesktop.Infrastructure.TradeLocker;

// Ghép kho DPAPI + Keycloak + kerber thành 3 việc mà socket/REST cần: access token còn hạn, account đúng cấu hình,
// account JWT còn hạn. Access token / account JWT được làm mới trước hạn 60 s (như scan\trade-locker).
// Refresh token mới (nếu Keycloak xoay) chỉ ghi khi TradeLockerSessionSyncDecision cho phép — không lùi token mà
// Chrome vừa đồng bộ. Mọi lỗi ⇒ null + LastError (không chứa token), caller fail-closed.
public sealed class TradeLockerAuthSession
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    private readonly ITradeLockerTokenStore _store;
    private readonly TradeLockerAuthClient _client;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TradeLockerAccountJwt? _jwt;
    private string? _jwtAccId;

    public TradeLockerAuthSession(ITradeLockerTokenStore store, TradeLockerAuthClient client, Func<DateTime>? utcNow = null)
    {
        _store = store;
        _client = client;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string? LastError { get; private set; }

    // Lỗi cuối do mạng (caller nối lại theo backoff, không báo "cần đăng nhập").
    public bool LastErrorIsTransient { get; private set; }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetAccessTokenCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // AccountId trong cấu hình rỗng ⇒ account đầu tiên của profile (giống scan\trade-locker). Không phải APOLLO ⇒ null.
    public async Task<TradeLockerAccount?> ResolveAccountAsync(TradeLockerConfig config, CancellationToken cancellationToken)
    {
        var access = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (access is null)
        {
            return null;
        }

        var accounts = await _client.ListAccountsAsync(access, config.EffectiveBrand, cancellationToken).ConfigureAwait(false);
        if (accounts is null)
        {
            LastError = _client.LastError;
            LastErrorIsTransient = _client.LastErrorIsTransient;
            return null;
        }

        var account = string.IsNullOrWhiteSpace(config.AccountId)
            ? accounts.FirstOrDefault()
            : accounts.FirstOrDefault(a => string.Equals(a.AccId, config.AccountId, StringComparison.Ordinal));
        if (account is null)
        {
            LastError = string.IsNullOrWhiteSpace(config.AccountId)
                ? "profile không có account nào"
                : $"không thấy account {config.AccountId} trong profile ({accounts.Count} account)";
            return null;
        }

        if (!account.IsApollo)
        {
            LastError = $"account {account.AccId} chạy engine {account.Engine}, chỉ hỗ trợ APOLLO";
            return null;
        }

        LastError = null;
        return account;
    }

    public async Task<TradeLockerAccountJwt?> GetAccountJwtAsync(string accId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_jwt is { } cached && _jwtAccId == accId && _utcNow() < cached.ExpiresUtc - RefreshMargin)
            {
                return cached;
            }

            var access = await GetAccessTokenCoreAsync(cancellationToken).ConfigureAwait(false);
            if (access is null)
            {
                return null;
            }

            var jwt = await _client.MintAccountJwtAsync(access, accId, cancellationToken).ConfigureAwait(false);
            if (jwt is null)
            {
                LastError = _client.LastError;
                LastErrorIsTransient = _client.LastErrorIsTransient;
                return null;
            }

            _jwt = jwt;
            _jwtAccId = accId;
            LastError = null;
            return jwt;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> GetAccessTokenCoreAsync(CancellationToken cancellationToken)
    {
        var session = _store.Load();
        var now = _utcNow();
        LastErrorIsTransient = false;
        if (session is null || !session.IsUsableAt(now))
        {
            LastError = session is null ? "chưa đăng nhập TradeLocker" : "refresh token TradeLocker đã hết hạn — đăng nhập lại";
            return null;
        }

        if (session.AccessExpiresUtc is { } accessExpiry && now < accessExpiry - RefreshMargin)
        {
            LastError = null;
            return session.AccessToken;
        }

        var tokens = await _client.RefreshAsync(session.RefreshToken, cancellationToken).ConfigureAwait(false);
        if (tokens is null)
        {
            LastError = _client.LastError;
            LastErrorIsTransient = _client.LastErrorIsTransient;
            return null;
        }

        // Cùng refresh token (Keycloak không xoay) ⇒ luôn lưu để giữ access token mới; khác thì không được lùi token.
        var refreshed = new TradeLockerSession(tokens.RefreshToken, tokens.AccessToken, now);
        var stored = _store.Load();
        if (stored is null
            || string.Equals(stored.RefreshToken, refreshed.RefreshToken, StringComparison.Ordinal)
            || TradeLockerSessionSyncDecision.ShouldSave(stored, refreshed))
        {
            _store.Save(refreshed);
        }

        LastError = null;
        return tokens.AccessToken;
    }
}
