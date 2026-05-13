using System.Text.Json;

namespace TradeDesktop.App.Helpers;

public static class SansHelper
{
    public static (string MapName1, string MapName2) ParseSans(string? sansJson)
    {
        if (string.IsNullOrWhiteSpace(sansJson))
        {
            return (string.Empty, string.Empty);
        }

        try
        {
            using var doc = JsonDocument.Parse(sansJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return (string.Empty, string.Empty);
            }

            var mapName1 = doc.RootElement.GetArrayLength() > 0
                ? (doc.RootElement[0].GetString() ?? string.Empty).Trim()
                : string.Empty;

            var mapName2 = doc.RootElement.GetArrayLength() > 1
                ? (doc.RootElement[1].GetString() ?? string.Empty).Trim()
                : string.Empty;

            return (mapName1, mapName2);
        }
        catch
        {
            return (string.Empty, string.Empty);
        }
    }

    public static string BuildSans(string? mapName1, string? mapName2)
    {
        var sans = new[]
        {
            mapName1?.Trim() ?? string.Empty,
            mapName2?.Trim() ?? string.Empty
        };

        return JsonSerializer.Serialize(sans);
    }
}