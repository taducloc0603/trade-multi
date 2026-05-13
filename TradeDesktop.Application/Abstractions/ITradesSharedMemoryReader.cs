using TradeDesktop.Application.Models;

namespace TradeDesktop.Application.Abstractions;

public interface ITradesSharedMemoryReader
{
    SharedMapReadResult<TradeSharedRecord> ReadTrades(string mapName);
}