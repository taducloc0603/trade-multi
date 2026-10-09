using System.Text.Json;

namespace TradeDesktop.Application.Services.PrimeXbt;

public sealed record PrimeXbtQuote(decimal Bid, decimal Ask, decimal? Last);

public sealed record PrimeXbtSymbolInfo(int SymbolId, string Symbol, int PriceScale, bool IsMarketOpen)
{
    // point = 10^-priceScale (XAU/USD: priceScale 2 ⇒ 0.01). Đối chiếu với CurrentPoint qua digits.
    public int Digits => PriceScale;
}

public enum PrimeXbtSymbolResolveStatus
{
    Resolved,
    NotFound,
    Ambiguous,
    Invalid
}

public sealed record PrimeXbtSymbolResolveResult(PrimeXbtSymbolResolveStatus Status, PrimeXbtSymbolInfo? Info);

public static class PrimeXbtSymbolResolver
{
    // Từ body RESPONSE `markets2` ({data:[…]}): chọn đúng 1 phần tử có `symbol` TRÙNG CHÍNH XÁC (Ordinal) và có
    // `symbolId`. "XAU/USD" ≠ "XAU/USD.24" (P10). Không khớp đúng 1 ⇒ fail-closed.
    public static PrimeXbtSymbolResolveResult Resolve(JsonElement markets2Body, string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol) ||
            markets2Body.ValueKind != JsonValueKind.Object ||
            !markets2Body.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return new PrimeXbtSymbolResolveResult(PrimeXbtSymbolResolveStatus.Invalid, null);
        }

        var wanted = symbol.Trim();
        var matches = data.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Object &&
                        string.Equals(PrimeXbtJson.ReadString(x, "symbol"), wanted, StringComparison.Ordinal) &&
                        PrimeXbtJson.ReadLong(x, "symbolId") is not null)
            .ToList();

        if (matches.Count == 0)
        {
            return new PrimeXbtSymbolResolveResult(PrimeXbtSymbolResolveStatus.NotFound, null);
        }

        if (matches.Count > 1)
        {
            return new PrimeXbtSymbolResolveResult(PrimeXbtSymbolResolveStatus.Ambiguous, null);
        }

        var m = matches[0];
        var priceScale = (int)(PrimeXbtJson.ReadLong(m, "priceScale") ?? -1);
        if (priceScale < 0)
        {
            return new PrimeXbtSymbolResolveResult(PrimeXbtSymbolResolveStatus.Invalid, null);
        }

        var isOpen = m.TryGetProperty("isMarketOpen", out var open) && open.ValueKind == JsonValueKind.True;
        return new PrimeXbtSymbolResolveResult(
            PrimeXbtSymbolResolveStatus.Resolved,
            new PrimeXbtSymbolInfo((int)PrimeXbtJson.ReadLong(m, "symbolId")!.Value, wanted, priceScale, isOpen));
    }
}

public static class PrimeXbtQuoteParser
{
    // body của `fx/market` (RESPONSE hoặc EVENT): {symId, a, b, lp, …}. Tick không có timestamp (P7) — caller tự stamp.
    // Bỏ tick khi: sai symId, thiếu a/b, giá ≤ 0, hoặc ask < bid.
    public static bool TryParse(JsonElement body, int expectedSymbolId, out PrimeXbtQuote quote)
    {
        quote = new PrimeXbtQuote(0m, 0m, null);
        if (body.ValueKind != JsonValueKind.Object ||
            PrimeXbtJson.ReadLong(body, "symId") != expectedSymbolId)
        {
            return false;
        }

        var bid = PrimeXbtJson.ReadDecimal(body, "b");
        var ask = PrimeXbtJson.ReadDecimal(body, "a");
        if (bid is not > 0m || ask is not > 0m || ask < bid)
        {
            return false;
        }

        quote = new PrimeXbtQuote(bid.Value, ask.Value, PrimeXbtJson.ReadDecimal(body, "lp"));
        return true;
    }
}
