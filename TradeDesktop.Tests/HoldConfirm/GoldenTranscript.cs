using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using TradeDesktop.Application.Abstractions;

namespace TradeDesktop.Tests.HoldConfirm;

/// <summary>
/// So một transcript (log + kết quả) với file golden trong <c>HoldConfirm/Golden</c>.
/// Golden được sinh từ code TRƯỚC khi thêm hold_confirm_mode (Phase 0) để chứng minh mode ms
/// giữ nguyên từng ký tự. Chỉ sinh lại khi CHỦ Ý đổi hành vi: đặt biến môi trường
/// <c>HOLD_CONFIRM_GOLDEN_UPDATE=1</c> rồi chạy test.
/// </summary>
internal static partial class GoldenTranscript
{
    private const string UpdateVariable = "HOLD_CONFIRM_GOLDEN_UPDATE";

    public static void AssertMatches(string name, string actual, [CallerFilePath] string callerPath = "")
    {
        var directory = Path.Combine(Path.GetDirectoryName(callerPath)!, "Golden");
        var path = Path.Combine(directory, name + ".txt");
        var normalizedActual = NormalizeIds(actual).Replace("\r\n", "\n", StringComparison.Ordinal);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, normalizedActual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        Assert.True(File.Exists(path), $"Thiếu golden {path}. Sinh bằng {UpdateVariable}=1.");
        var expected = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!string.Equals(expected, normalizedActual, StringComparison.Ordinal))
        {
            var actualPath = Path.Combine(Path.GetTempPath(), $"golden-actual-{name}.txt");
            File.WriteAllText(actualPath, normalizedActual);
            Assert.Fail($"Transcript {name} khác golden ({FirstDifference(expected, normalizedActual)}). Bản thực tế: {actualPath}");
        }
    }

    /// <summary>Id cycle/signal là Guid "N" ngẫu nhiên; thay bằng ID1, ID2... theo thứ tự xuất hiện.</summary>
    private static string NormalizeIds(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        return HexId().Replace(text, match =>
        {
            if (!map.TryGetValue(match.Value, out var replacement))
            {
                replacement = $"ID{map.Count + 1}";
                map[match.Value] = replacement;
            }

            return replacement;
        });
    }

    private static string FirstDifference(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length); i++)
        {
            var e = i < expectedLines.Length ? expectedLines[i] : "<EOF>";
            var a = i < actualLines.Length ? actualLines[i] : "<EOF>";
            if (!string.Equals(e, a, StringComparison.Ordinal))
            {
                return $"dòng {i + 1}: expected `{e}` actual `{a}`";
            }
        }

        return "không rõ";
    }

    [GeneratedRegex("\\b[0-9a-f]{32}\\b")]
    private static partial Regex HexId();
}

/// <summary>
/// Logger ghi MỌI kênh vào một danh sách theo đúng thứ tự gọi, có tiền tố kênh.
/// Dòng <c>[GAP_STABILITY_RAW][CONFIG]</c> bị khử trùng lặp bằng state TĨNH dùng chung toàn
/// process (test khác có thể <c>ResetSummaries</c> song song), nên tách riêng thành tập giá trị
/// khác nhau thay vì đưa vào transcript theo thứ tự.
/// </summary>
internal sealed class TranscriptLogger : ISlotLogger, IGapStabilityRawLogger
{
    private readonly List<string> _lines = [];
    private readonly SortedSet<string> _rawConfigLines = new(StringComparer.Ordinal);

    public void Log(string message) => _lines.Add("LOG " + message);

    public void LogVerbose(string message) => _lines.Add("VERBOSE " + message);

    public void LogGapStabilityRaw(string message)
    {
        if (message.StartsWith("[GAP_STABILITY_RAW][CONFIG]", StringComparison.Ordinal))
        {
            _rawConfigLines.Add(message);
            return;
        }

        _lines.Add("RAW " + message);
    }

    public void Note(string text) => _lines.Add("# " + text);

    public string Build()
    {
        var builder = new StringBuilder();
        foreach (var line in _lines)
        {
            builder.Append(line).Append('\n');
        }

        builder.Append("## RAW_CONFIG (distinct)\n");
        foreach (var line in _rawConfigLines)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }
}
