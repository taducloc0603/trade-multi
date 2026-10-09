using System.Text.Json;

namespace TradeDesktop.Tests.PrimeXbt;

// Đọc frame thật (đã che danh tính) quét ở 00-scan-findings / Phase 0. Đường dẫn: file + chuỗi key lồng nhau.
internal static class PrimeXbtFixtures
{
    public static JsonElement Get(string file, params string[] path)
    {
        var full = Path.Combine(AppContext.BaseDirectory, "PrimeXbt", "Fixtures", file);
        using var doc = JsonDocument.Parse(File.ReadAllText(full));
        var element = doc.RootElement;
        foreach (var key in path)
        {
            element = element.GetProperty(key);
        }

        return element.Clone();
    }

    public static string Raw(string file, params string[] path) => Get(file, path).GetRawText();

    public static JsonElement Body(string file, params string[] path) => Get(file, path).GetProperty("body");

    public static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
