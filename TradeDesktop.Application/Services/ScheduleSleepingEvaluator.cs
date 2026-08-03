using System.Globalization;
using System.Text.Json;

namespace TradeDesktop.Application.Services;

public static class ScheduleSleepingEvaluator
{
    public static bool IsOpenBlocked(string? scheduleSleepingJson, DateTime localNow)
    {
        if (string.IsNullOrWhiteSpace(scheduleSleepingJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(scheduleSleepingJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("enabled", out var enabled)
                || enabled.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("blockedRanges", out var ranges)
                || ranges.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var currentMinute = localNow.Hour * 60 + localNow.Minute;
            foreach (var range in ranges.EnumerateArray())
            {
                if (range.ValueKind != JsonValueKind.Object
                    || !range.TryGetProperty("start", out var startElement)
                    || !range.TryGetProperty("end", out var endElement)
                    || startElement.ValueKind != JsonValueKind.String
                    || endElement.ValueKind != JsonValueKind.String
                    || !TryParseMinute(startElement.GetString(), out var startMinute)
                    || !TryParseMinute(endElement.GetString(), out var endMinute))
                {
                    continue;
                }

                // Equal endpoints represent one exact minute, not a full-day block.
                if (startMinute == endMinute)
                {
                    if (currentMinute == startMinute)
                    {
                        return true;
                    }

                    continue;
                }

                // End is inclusive because schedules use minute precision; 22:00-23:59
                // therefore blocks through the final minute of the local day.
                var isBlocked = startMinute < endMinute
                    ? currentMinute >= startMinute && currentMinute <= endMinute
                    : currentMinute >= startMinute || currentMinute <= endMinute;

                if (isBlocked)
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Invalid optional configuration must not stop trading unexpectedly.
        }

        return false;
    }

    private static bool TryParseMinute(string? value, out int minuteOfDay)
    {
        minuteOfDay = 0;
        if (!TimeOnly.TryParseExact(
                value,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            return false;
        }

        minuteOfDay = time.Hour * 60 + time.Minute;
        return true;
    }
}
