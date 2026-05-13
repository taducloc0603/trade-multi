using System.Text.Json;
using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Helpers;

public static class SansJsonHelper
{
    public static bool TryParseSans(string? sansJson, out string mapName1, out string mapName2)
    {
        if (TryParseSans(sansJson, out mapName1, out mapName2, out _))
        {
            return true;
        }

        mapName1 = string.Empty;
        mapName2 = string.Empty;
        return false;
    }

    public static bool TryParseSans(
        string? sansJson,
        out string mapName1,
        out string mapName2,
        out IReadOnlyList<ManualHwndColumnConfig> manualHwndColumns)
    {
        mapName1 = string.Empty;
        mapName2 = string.Empty;
        manualHwndColumns = [ManualHwndColumnConfig.Empty];

        if (string.IsNullOrWhiteSpace(sansJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(sansJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                mapName1 = doc.RootElement.GetArrayLength() > 0
                    ? (doc.RootElement[0].GetString() ?? string.Empty).Trim()
                    : string.Empty;

                mapName2 = doc.RootElement.GetArrayLength() > 1
                    ? (doc.RootElement[1].GetString() ?? string.Empty).Trim()
                    : string.Empty;

                manualHwndColumns = [ManualHwndColumnConfig.Empty];
                return !string.IsNullOrWhiteSpace(mapName1) || !string.IsNullOrWhiteSpace(mapName2);
            }

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (doc.RootElement.TryGetProperty("mapNames", out var mapNamesElement) &&
                mapNamesElement.ValueKind == JsonValueKind.Array)
            {
                mapName1 = mapNamesElement.GetArrayLength() > 0
                    ? (mapNamesElement[0].GetString() ?? string.Empty).Trim()
                    : string.Empty;

                mapName2 = mapNamesElement.GetArrayLength() > 1
                    ? (mapNamesElement[1].GetString() ?? string.Empty).Trim()
                    : string.Empty;
            }

            if (doc.RootElement.TryGetProperty("manualHwndColumns", out var columnsElement) &&
                columnsElement.ValueKind == JsonValueKind.Array)
            {
                var parsedColumns = new List<ManualHwndColumnConfig>();

                foreach (var item in columnsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var chartA = item.TryGetProperty("chartA", out var chartAElement) && chartAElement.ValueKind == JsonValueKind.String
                        ? chartAElement.GetString() ?? string.Empty
                        : string.Empty;
                    var tradeA = item.TryGetProperty("tradeA", out var tradeAElement) && tradeAElement.ValueKind == JsonValueKind.String
                        ? tradeAElement.GetString() ?? string.Empty
                        : string.Empty;
                    var chartB = item.TryGetProperty("chartB", out var chartBElement) && chartBElement.ValueKind == JsonValueKind.String
                        ? chartBElement.GetString() ?? string.Empty
                        : string.Empty;
                    var tradeB = item.TryGetProperty("tradeB", out var tradeBElement) && tradeBElement.ValueKind == JsonValueKind.String
                        ? tradeBElement.GetString() ?? string.Empty
                        : string.Empty;

                    parsedColumns.Add(new ManualHwndColumnConfig(chartA, tradeA, chartB, tradeB).Normalize());
                }

                manualHwndColumns = parsedColumns.Count > 0
                    ? parsedColumns
                    : [ManualHwndColumnConfig.Empty];
            }

            return !string.IsNullOrWhiteSpace(mapName1) || !string.IsNullOrWhiteSpace(mapName2);
        }
        catch
        {
            mapName1 = string.Empty;
            mapName2 = string.Empty;
            manualHwndColumns = [ManualHwndColumnConfig.Empty];
            return false;
        }
    }

    public static string BuildSans(string? mapName1, string? mapName2)
        => BuildSans(mapName1, mapName2, null);

    public static string BuildSans(
        string? mapName1,
        string? mapName2,
        IReadOnlyList<ManualHwndColumnConfig>? manualHwndColumns)
    {
        var normalizedColumns = (manualHwndColumns ?? [ManualHwndColumnConfig.Empty])
            .Select(x => (x ?? ManualHwndColumnConfig.Empty).Normalize())
            .ToList();

        if (normalizedColumns.Count == 0)
        {
            normalizedColumns.Add(ManualHwndColumnConfig.Empty);
        }

        var payload = new
        {
            version = 2,
            mapNames = new[]
            {
                mapName1?.Trim() ?? string.Empty,
                mapName2?.Trim() ?? string.Empty
            },
            manualHwndColumns = normalizedColumns.Select(x => new
            {
                chartA = x.ChartHwndA,
                tradeA = x.TradeHwndA,
                chartB = x.ChartHwndB,
                tradeB = x.TradeHwndB
            })
        };

        return JsonSerializer.Serialize(payload);
    }
}
