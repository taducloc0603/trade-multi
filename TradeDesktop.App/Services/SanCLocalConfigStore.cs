using System.IO;
using System.Text.Json;

namespace TradeDesktop.App.Services;

/// <summary>
/// Lưu cấu hình sàn C (monitor-only) ở local file — KHÔNG đụng DB/Supabase.
/// Sàn C không vào lệnh nên chỉ cần MapName + Platform. File nằm cùng chỗ log startup:
/// %LocalAppData%/TradeDesktop/sanC.json.
/// </summary>
public sealed record SanCLocalConfig(string MapName3, string PlatformC)
{
    public static SanCLocalConfig Empty { get; } = new(string.Empty, "mt5");
}

public static class SanCLocalConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private static string GetConfigPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(localAppData, "TradeDesktop");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "sanC.json");
    }

    public static SanCLocalConfig Load()
    {
        try
        {
            var path = GetConfigPath();
            if (!File.Exists(path))
            {
                return SanCLocalConfig.Empty;
            }

            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<SanCLocalConfig>(json);
            if (config is null)
            {
                return SanCLocalConfig.Empty;
            }

            var platform = string.IsNullOrWhiteSpace(config.PlatformC) ? "mt5" : config.PlatformC.Trim();
            return new SanCLocalConfig((config.MapName3 ?? string.Empty).Trim(), platform);
        }
        catch
        {
            // Config C là monitor-only, hỏng file không được làm chết app — fallback rỗng.
            return SanCLocalConfig.Empty;
        }
    }

    public static void Save(SanCLocalConfig config)
    {
        var normalized = new SanCLocalConfig(
            (config.MapName3 ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(config.PlatformC) ? "mt5" : config.PlatformC.Trim());

        var json = JsonSerializer.Serialize(normalized, SerializerOptions);
        File.WriteAllText(GetConfigPath(), json);
    }
}
