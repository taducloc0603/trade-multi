// TickMemory4.mqh

#include "SharedMemoryBase4.mqh"
#include "BinaryHelper4.mqh"

#define TICK_MEMORY_SIZE 256
#define TICK_SYMBOL_SIZE 16

class CTickMemory : public CSharedMemoryBase {
private:
   double m_lastBid;
   double m_lastAsk;

public:
   CTickMemory(string memory_name) : CSharedMemoryBase(memory_name, TICK_MEMORY_SIZE) {  
      m_lastBid = 0;
      m_lastAsk = 0;
   }
   
   bool HasChanged() {
      double bid = Bid;
      double ask = Ask;
      return (bid != m_lastBid || ask != m_lastAsk);
   }
   
   void Update() {
      if(!IsValid()) return;
      
      double bid = Bid;
      double ask = Ask;
      double spread = ask - bid;
      datetime time = TimeCurrent();

      uchar buf[];
      ArrayResize(buf, TICK_MEMORY_SIZE);
      ArrayInitialize(buf, 0);

      int offset = 0;

      // Header - Windows time (ms) để detect stale
      CBinaryHelper::PackInt32(buf, offset, 1);
      offset += 4;

      CBinaryHelper::PackUInt64(buf, offset, (ulong)GetTickCount());
      offset += 8;

      offset += 4; // padding

      // Tick data
      CBinaryHelper::PackDouble(buf, offset, bid);
      offset += 8;

      CBinaryHelper::PackDouble(buf, offset, ask);
      offset += 8;

      CBinaryHelper::PackDouble(buf, offset, spread);
      offset += 8;

      // Server time (milliseconds) - MQ4 không có ms, xấp xỉ từ seconds
      CBinaryHelper::PackUInt64(buf, offset, (ulong)time * 1000);
      offset += 8;
      
      // Symbol (16 bytes)
      PackSymbol(buf, offset, Symbol());
      offset += TICK_SYMBOL_SIZE;

      CommitWrite(buf);

      m_lastBid = bid;
      m_lastAsk = ask;
   }

private:
   void PackSymbol(uchar &buf[], int offset, string symbol) {
      uchar tmp[];
      int len = StringToCharArray(symbol, tmp, 0, TICK_SYMBOL_SIZE - 1);
      ArrayResize(tmp, TICK_SYMBOL_SIZE);
      for(int i = len; i < TICK_SYMBOL_SIZE; i++) tmp[i] = 0;
      ArrayCopy(buf, tmp, offset, 0, TICK_SYMBOL_SIZE);
   }
};
