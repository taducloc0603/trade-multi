using QuickFix;

namespace TradeDesktop.Infrastructure.CTrader;

public sealed record CTraderSymbolInfo(int SymbolId, string SymbolName, int Digits);

// SecurityList (35=y): nguồn sự thật DUY NHẤT cho tên symbol (1007) và digits (1008) của sàn B.
// Đọc 1007/1008 bằng số tag thô — lớp Tags sinh sẵn của QuickFIX/n đặt tên hai tag này là
// SideReasonCd/SideTrdSubTyp (FIX 5.0), rất dễ đọc nhầm. Phase 0 câu 3/10: 55=41 → XAUUSD, digits 2,
// trả lời được cả trên QUOTE lẫn TRADE session.
public sealed class CTraderSecurityCatalog
{
    private readonly Dictionary<int, CTraderSymbolInfo> _symbols = [];

    public int Count => _symbols.Count;

    public bool TryGet(int symbolId, out CTraderSymbolInfo info)
        => _symbols.TryGetValue(symbolId, out info!);

    public bool IsResolved(int symbolId) => _symbols.ContainsKey(symbolId);

    // Thay toàn bộ catalog bằng SecurityList mới (re-issue mỗi lần logon). Trả số symbol đọc được.
    public int Apply(Message message)
    {
        if (FixFieldReader.MsgType(message) != "y")
        {
            return 0;
        }

        var parsed = new Dictionary<int, CTraderSymbolInfo>();
        foreach (var group in FixFieldReader.Groups(message, 146))
        {
            var id = FixFieldReader.Int(group, 55);
            if (id is null)
            {
                continue;
            }

            parsed[id.Value] = new CTraderSymbolInfo(
                id.Value,
                FixFieldReader.String(group, 1007) ?? string.Empty,
                FixFieldReader.Int(group, 1008) ?? -1);
        }

        _symbols.Clear();
        foreach (var (id, info) in parsed)
        {
            _symbols[id] = info;
        }

        return parsed.Count;
    }

    public void Clear() => _symbols.Clear();
}
