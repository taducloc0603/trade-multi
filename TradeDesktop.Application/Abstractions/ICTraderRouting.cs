namespace TradeDesktop.Application.Abstractions;

// Cờ tường minh "sàn B đang là cTrader" thay cho việc sniff tiền tố trong tên map.
// Phase 2 chỉ định nghĩa; decorator đọc trades/history (Phase 5/6) mới dùng.
public interface ICTraderRouting
{
    bool IsCTraderExchangeB { get; }
    bool IsCTraderTradeMap(string? mapName);
    bool IsCTraderHistoryMap(string? mapName);
}
