using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Abstractions;

public interface IHistorySharedMemoryReader
{
    SharedMapReadResult<HistorySharedRecord> ReadHistory(string mapName);
}