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
        => TryParseSans(sansJson, out mapName1, out mapName2, out manualHwndColumns, out _);

    public static bool TryParseSans(
        string? sansJson,
        out string mapName1,
        out string mapName2,
        out IReadOnlyList<ManualHwndColumnConfig> manualHwndColumns,
        out CTraderFixConfig ctraderFix)
    {
        mapName1 = string.Empty;
        mapName2 = string.Empty;
        manualHwndColumns = [ManualHwndColumnConfig.Empty];
        ctraderFix = CTraderFixConfig.Empty;

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

            // Khối ctraderFix hỏng/thiếu không được làm hỏng mapNames/manualHwndColumns: parse riêng, lỗi thì Empty.
            if (doc.RootElement.TryGetProperty("ctraderFix", out var ctraderElement) &&
                ctraderElement.ValueKind == JsonValueKind.Object)
            {
                ctraderFix = ParseCTraderFix(ctraderElement);
            }

            return !string.IsNullOrWhiteSpace(mapName1) || !string.IsNullOrWhiteSpace(mapName2);
        }
        catch
        {
            mapName1 = string.Empty;
            mapName2 = string.Empty;
            manualHwndColumns = [ManualHwndColumnConfig.Empty];
            ctraderFix = CTraderFixConfig.Empty;
            return false;
        }
    }

    private static CTraderFixConfig ParseCTraderFix(JsonElement element)
    {
        try
        {
            return new CTraderFixConfig(
                ReadEndpoint(element, "quote"),
                ReadEndpoint(element, "trade"),
                ReadBool(element, "useSsl", CTraderFixConfig.Empty.UseSsl),
                ReadString(element, "senderCompId"),
                ReadString(element, "targetCompId"),
                ReadString(element, "password"),
                ReadString(element, "username"),
                (int)ReadDecimal(element, "symbolId"),
                ReadString(element, "symbolName"),
                ReadDecimal(element, "volumeBUnits"),
                ReadDecimal(element, "contractSizeB"),
                ReadDecimal(element, "volumeALots")).Normalize();
        }
        catch
        {
            return CTraderFixConfig.Empty;
        }
    }

    private static CTraderEndpoint ReadEndpoint(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return CTraderEndpoint.Empty;
        }

        return new CTraderEndpoint(
            ReadString(element, "host"),
            (int)ReadDecimal(element, "portSsl"),
            (int)ReadDecimal(element, "portPlain"));
    }

    private static string ReadString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static bool ReadBool(JsonElement parent, string name, bool fallback)
        => parent.TryGetProperty(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : fallback;

    private static decimal ReadDecimal(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var element) &&
           element.ValueKind == JsonValueKind.Number &&
           element.TryGetDecimal(out var value)
            ? value
            : 0m;

    // Che mọi giá trị "password" trước khi đưa sans_json vào log. JSON hỏng vẫn che bằng regex, không throw.
    public static string Redact(string? sansJson)
    {
        if (string.IsNullOrEmpty(sansJson) ||
            sansJson.IndexOf("password", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return sansJson ?? string.Empty;
        }

        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(sansJson);
            if (node is not null)
            {
                RedactNode(node);
                return node.ToJsonString();
            }
        }
        catch
        {
            // Rơi xuống nhánh regex.
        }

        return System.Text.RegularExpressions.Regex.Replace(
            sansJson,
            "(\"password\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"",
            "$1\"***\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static void RedactNode(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).ToList())
            {
                if (string.Equals(key, "password", StringComparison.OrdinalIgnoreCase))
                {
                    obj[key] = "***";
                }
                else if (obj[key] is { } child)
                {
                    RedactNode(child);
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                {
                    RedactNode(child);
                }
            }
        }
    }

    public static string BuildSans(string? mapName1, string? mapName2)
        => BuildSans(mapName1, mapName2, null);

    public static string BuildSans(
        string? mapName1,
        string? mapName2,
        IReadOnlyList<ManualHwndColumnConfig>? manualHwndColumns)
        => BuildSans(mapName1, mapName2, manualHwndColumns, null);

    public static string BuildSans(
        string? mapName1,
        string? mapName2,
        IReadOnlyList<ManualHwndColumnConfig>? manualHwndColumns,
        CTraderFixConfig? ctraderFix)
    {
        var normalizedColumns = (manualHwndColumns ?? [ManualHwndColumnConfig.Empty])
            .Select(x => (x ?? ManualHwndColumnConfig.Empty).Normalize())
            .ToList();

        if (normalizedColumns.Count == 0)
        {
            normalizedColumns.Add(ManualHwndColumnConfig.Empty);
        }

        var mapNames = new[]
        {
            mapName1?.Trim() ?? string.Empty,
            mapName2?.Trim() ?? string.Empty
        };
        var columns = normalizedColumns.Select(x => new
        {
            chartA = x.ChartHwndA,
            tradeA = x.TradeHwndA,
            chartB = x.ChartHwndB,
            tradeB = x.TradeHwndB
        });

        var fix = ctraderFix?.Normalize();
        if (fix is null || fix == CTraderFixConfig.Empty)
        {
            // Máy MT-MT: payload giữ nguyên như trước Phase 2, không có key ctraderFix.
            return JsonSerializer.Serialize(new
            {
                version = 2,
                mapNames,
                manualHwndColumns = columns
            });
        }

        return JsonSerializer.Serialize(new
        {
            version = 2,
            mapNames,
            manualHwndColumns = columns,
            ctraderFix = new
            {
                quote = new { host = fix.Quote.Host, portSsl = fix.Quote.PortSsl, portPlain = fix.Quote.PortPlain },
                trade = new { host = fix.Trade.Host, portSsl = fix.Trade.PortSsl, portPlain = fix.Trade.PortPlain },
                useSsl = fix.UseSsl,
                senderCompId = fix.SenderCompId,
                targetCompId = fix.TargetCompId,
                password = fix.Password,
                username = fix.Username,
                symbolId = fix.SymbolId,
                symbolName = fix.SymbolName,
                volumeBUnits = fix.VolumeBUnits,
                contractSizeB = fix.ContractSizeB,
                volumeALots = fix.VolumeALots
            }
        });
    }
}
