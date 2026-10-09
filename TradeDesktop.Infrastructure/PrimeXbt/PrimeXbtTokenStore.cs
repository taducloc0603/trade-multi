using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.Infrastructure.PrimeXbt;

// Lưu phiên PrimeXBT cục bộ bằng DPAPI (CurrentUser) — quyết định D5 của docs/plans/primexbt: token xoay vòng và gắn
// máy (claim `cip`), không đưa lên Supabase. File: %LOCALAPPDATA%\TradeDesktop\primexbt\{hostname}.bin.
// Mọi lỗi (file hỏng, khác user Windows) ⇒ Load trả null, không throw; không bao giờ log nội dung.
public sealed class PrimeXbtTokenStore : IPrimeXbtTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TradeDesktop.PrimeXbt.v1");
    private readonly string _filePath;
    private readonly object _sync = new();

    public PrimeXbtTokenStore(IMachineIdentityService machineIdentityService)
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TradeDesktop",
            "primexbt",
            SafeFileName(machineIdentityService.GetHostName()) + ".bin"))
    {
    }

    // Cho test: chỉ định thẳng đường dẫn file.
    public PrimeXbtTokenStore(string filePath)
    {
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    public PrimeXbtSession? Load()
    {
        lock (_sync)
        {
            try
            {
                // DPAPI chỉ có trên Windows (app WPF luôn chạy Windows; guard để analyzer CA1416 và môi trường khác).
                if (!OperatingSystem.IsWindows() || !File.Exists(_filePath))
                {
                    return null;
                }

                var plain = ProtectedData.Unprotect(File.ReadAllBytes(_filePath), Entropy, DataProtectionScope.CurrentUser);
                var dto = JsonSerializer.Deserialize<SessionDto>(plain);
                if (dto is null || string.IsNullOrWhiteSpace(dto.Jwt))
                {
                    return null;
                }

                return new PrimeXbtSession(
                    dto.Jwt,
                    new Dictionary<string, string>(dto.ApiCookies ?? new(), StringComparer.Ordinal),
                    DateTime.SpecifyKind(dto.SavedUtc, DateTimeKind.Utc));
            }
            catch
            {
                return null;
            }
        }
    }

    public void Save(PrimeXbtSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("PrimeXbtTokenStore cần DPAPI (Windows).");
        }

        lock (_sync)
        {
            var dto = new SessionDto
            {
                Jwt = session.Jwt,
                ApiCookies = session.ApiCookies.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
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
        public string Jwt { get; set; } = string.Empty;
        public Dictionary<string, string>? ApiCookies { get; set; }
        public DateTime SavedUtc { get; set; }
    }
}
