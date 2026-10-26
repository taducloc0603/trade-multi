namespace TradeDesktop.Application.Services.TradeLocker;

public sealed record TradeLockerSymbol(
    string Symbol,
    string TradableUnitId,
    string InstrumentId,
    string TickerId,
    int TradingStatus,
    decimal TickSize,
    decimal LotSize,
    decimal LotStep,
    decimal MinimumLot,
    decimal MaximumLot)
{
    // ProtoTradingStatus: 1 = ACTIVE; 2 RESTRICTED / 3 SUSPENDED ⇒ không mở lệnh.
    public bool IsTradingActive => TradingStatus == 1;
}

// symbol ↔ tradable unit / instrument / ticker từ `v3.tradable-unit-aggregates` (port SymbolIndex của scan\trade-locker).
// Khớp CHÍNH XÁC theo code hoặc name của instrument (không phân biệt hoa thường) — không khớp một phần, không đoán.
// Không thread-safe: session gọi trong lock của nó.
public sealed class TradeLockerSymbolIndex
{
    private readonly Dictionary<string, TradeLockerInstrument> _instruments = [];
    private readonly Dictionary<string, TradeLockerTicker> _tickers = [];
    private readonly Dictionary<string, TradeLockerTradableUnit> _units = [];

    public int Count => _units.Count;

    public void Apply(TradeLockerTradableUnitsEvent unitsEvent)
    {
        foreach (var i in unitsEvent.Instruments.Where(i => i.Id.Length > 0)) _instruments[i.Id] = i;
        foreach (var t in unitsEvent.Tickers.Where(t => t.Id.Length > 0)) _tickers[t.Id] = t;
        foreach (var u in unitsEvent.TradableUnits.Where(u => u.Id.Length > 0)) _units[u.Id] = u;
    }

    public void Clear()
    {
        _instruments.Clear();
        _tickers.Clear();
        _units.Clear();
    }

    public TradeLockerSymbol? Resolve(string? symbol)
    {
        var key = (symbol ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return null;
        }

        var matches = _units.Values
            .Select(Build)
            .Where(s => s is not null && (Equal(s.Value.Instrument.Code, key) || Equal(s.Value.Instrument.Name, key)))
            .Select(s => s!.Value)
            .ToList();
        // Nhiều tradable unit cùng một instrument (khác account group) ⇒ mơ hồ ⇒ không chọn.
        return matches.Count == 1 ? ToSymbol(matches[0]) : null;
    }

    public string? SymbolOfTradableUnit(string tradableUnitId)
        => Build(_units.GetValueOrDefault(tradableUnitId)) is { } s ? DisplayName(s.Instrument) : null;

    private (TradeLockerTradableUnit Unit, TradeLockerInstrument Instrument, TradeLockerTicker Ticker)? Build(TradeLockerTradableUnit? unit)
    {
        if (unit is null
            || !_instruments.TryGetValue(unit.InstrumentId, out var instrument)
            || !_tickers.TryGetValue(instrument.TickerId, out var ticker))
        {
            return null;
        }

        return (unit, instrument, ticker);
    }

    private static TradeLockerSymbol ToSymbol((TradeLockerTradableUnit Unit, TradeLockerInstrument Instrument, TradeLockerTicker Ticker) s)
        => new(
            DisplayName(s.Instrument),
            s.Unit.Id,
            s.Instrument.Id,
            s.Ticker.Id,
            s.Instrument.TradingStatus,
            s.Ticker.TickSize,
            s.Ticker.LotSize,
            s.Ticker.LotStep,
            s.Ticker.MinimumLot,
            s.Ticker.MaximumLot);

    private static string DisplayName(TradeLockerInstrument instrument)
        => string.IsNullOrEmpty(instrument.Code) ? instrument.Name : instrument.Code;

    private static bool Equal(string a, string b) => a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
