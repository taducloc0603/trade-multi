using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.Infrastructure.TradeLocker;

// Lưu phiên TradeLocker cục bộ bằng DPAPI (CurrentUser), cùng khuôn PrimeXbtTokenStore: không đưa lên Supabase.
// File: %LOCALAPPDATA%\TradeDesktop\tradelocker\{hostname}.bin. Mọi lỗi ⇒ Load trả null, không throw, không log nội dung.
public sealed class TradeLockerTokenStore : ITradeLockerTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TradeDesktop.TradeLocker.v1");
    private readonly string _filePath;
    private readonly object _sync = new();

    public TradeLockerTokenStore(IMachineIdentityService machineIdentityService)
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TradeDesktop",
            "tradelocker",
            SafeFileName(machineIdentityService.GetHostName()) + ".bin"))
    {
    }

    // Cho test: chỉ định thẳng đường dẫn file.
    public TradeLockerTokenStore(string filePath)
    {
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    public TradeLockerSession? Load()
    {
        lock (_sync)
        {
            try
            {
                if (!OperatingSystem.IsWindows() || !File.Exists(_filePath))
                {
                    return null;
                }

                var plain = ProtectedData.Unprotect(File.ReadAllBytes(_filePath), Entropy, DataProtectionScope.CurrentUser);
                var dto = JsonSerializer.Deserialize<SessionDto>(plain);
                if (dto is null || string.IsNullOrWhiteSpace(dto.RefreshToken))
                {
                    return null;
                }

                return new TradeLockerSession(
                    dto.RefreshToken,
                    dto.AccessToken ?? string.Empty,
                    DateTime.SpecifyKind(dto.SavedUtc, DateTimeKind.Utc));
            }
            catch
            {
                return null;
            }
        }
    }

    public void Save(TradeLockerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("TradeLockerTokenStore cần DPAPI (Windows).");
        }

        lock (_sync)
        {
            var dto = new SessionDto
            {
                RefreshToken = session.RefreshToken,
                AccessToken = session.AccessToken,
                SavedUtc = session.SavedUtc
            };
            var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(dto), Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var temp = _filePath + ".tmp";
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, _filePath, overwrite: true);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    File.Delete(_filePath);
                }
            }
            catch
            {
                // Không xoá được thì lần Load sau vẫn đọc được — chấp nhận, không throw ra UI.
            }
        }
    }

    private static string SafeFileName(string? hostName)
    {
        var name = string.IsNullOrWhiteSpace(hostName) ? "default" : hostName.Trim().ToLowerInvariant();
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }

    private sealed class SessionDto
    {
        public string RefreshToken { get; set; } = string.Empty;
        public string? AccessToken { get; set; }
        public DateTime SavedUtc { get; set; }
    }
}
