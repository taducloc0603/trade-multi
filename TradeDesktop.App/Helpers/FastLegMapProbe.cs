using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace TradeDesktop.App.Helpers;

/// <summary>
/// Đọc một lần (CHỈ ĐỌC) map tick của EA kênh C cho nút Check trong cửa sổ Config.
/// Layout TickMemory của DataExporter: version@0, ts@4 (GetTickCount64), bid@16, ask@24, symbol@48..63.
/// Không dùng cho quyết định giao dịch — đường đó là FastLegMmfSampler.
/// </summary>
public static class FastLegMapProbe
{
    private const int RecordSize = 64;
    private const int SymbolOffset = 48;
    private const int SymbolSize = 16;

    /// <param name="Status">Dòng hiển thị cạnh nút Check.</param>
    /// <param name="Symbol">Symbol đọc được từ map; null khi không đọc được record hợp lệ (dùng để tự điền ô Symbol).</param>
    public sealed record Result(string Status, string? Symbol);

    public static Result Check(string? mapName, string? expectedSymbol)
    {
        var name = (mapName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return new Result("✖ Chưa nhập Map Name", null);
        }

        try
        {
            using var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, RecordSize, MemoryMappedFileAccess.Read);
            var version = view.ReadInt32(0);
            var ageMs = Math.Max(0L, Environment.TickCount64 - (long)view.ReadUInt64(4));
            var bid = view.ReadDouble(16);
            var ask = view.ReadDouble(24);
            var symbolBytes = new byte[SymbolSize];
            view.ReadArray(SymbolOffset, symbolBytes, 0, SymbolSize);
            var zero = Array.IndexOf(symbolBytes, (byte)0);
            var symbol = Encoding.ASCII.GetString(symbolBytes, 0, zero < 0 ? SymbolSize : zero).Trim();

            if (version != 1)
            {
                return new Result($"✖ Map có nhưng version={version} (cần 1)", null);
            }

            var prices = string.Create(CultureInfo.InvariantCulture, $"{symbol} {bid:0.#####}/{ask:0.#####}, tuổi {ageMs} ms");
            var readSymbol = symbol.Length > 0 ? symbol : null;
            var expected = (expectedSymbol ?? string.Empty).Trim();
            if (expected.Length > 0 && !string.Equals(symbol, expected, StringComparison.OrdinalIgnoreCase))
            {
                return new Result($"⚠ Symbol trên map là '{symbol}', khác '{expected}' — {prices}", readSymbol);
            }

            return new Result(ageMs > 5000 ? $"⚠ Map có nhưng tick cũ — {prices}" : $"✔ {prices}", readSymbol);
        }
        catch (FileNotFoundException)
        {
            return new Result("✖ Map không tồn tại (EA kênh C chưa chạy?)", null);
        }
        catch (Exception ex)
        {
            return new Result($"✖ Không đọc được map: {ex.Message}", null);
        }
    }
}
