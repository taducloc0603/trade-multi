// TickMemory.mqh
#property strict

#include "SharedMemoryBase.mqh"
#include "BinaryHelper.mqh"

#define TICK_MEMORY_SIZE 256
#define TICK_RECORD_SIZE 48 // bid(8) + ask(8) + spread(8) + time_msc(8) + symbol(16)

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
      double bid = SymbolInfoDouble(_Symbol, SYMBOL_BID);
      double ask = SymbolInfoDouble(_Symbol, SYMBOL_ASK);
      return (bid != m_lastBid || ask != m_lastAsk);
   }
   
   void Update() {
      if(!IsValid()) return;

      MqlTick tick;
      if(!SymbolInfoTick(_Symbol, tick)) return;

      double bid = tick.bid;
      double ask = tick.ask;
      double spread = ask - bid;

      uchar buf[];
      ArrayResize(buf, TICK_MEMORY_SIZE);
      ArrayInitialize(buf, 0);

      int offset = 0;

      // Header - Windows time (ms) để detect stale
      CBinaryHelper::PackInt32(buf, offset, 1);
      offset += 4;

      CBinaryHelper::PackUInt64(buf, offset, GetTickCount64());
      offset += 8;

      offset += 4; // padding

      // Tick data
      CBinaryHelper::PackDouble(buf, offset, bid);
      offset += 8;

      CBinaryHelper::PackDouble(buf, offset, ask);
      offset += 8;

      CBinaryHelper::PackDouble(buf, offset, spread);
      offset += 8;

      // Server time (milliseconds)
      CBinaryHelper::PackUInt64(buf, offset, tick.time_msc);
      offset += 8;
      
      // Symbol (16 bytes)
      PackSymbol(buf, offset, _Symbol);
      offset += 16;

      CommitWrite(buf);

      m_lastBid = bid;
      m_lastAsk = ask;
   }
   
private:
   void PackSymbol(uchar &buf[], int offset, string symbol) {
      uchar tmp[];
      int len = StringToCharArray(symbol, tmp, 0, 15);
      if(len < 16) {
         ArrayResize(tmp, 16);
         for(int i = len; i < 16; i++) tmp[i] = 0;
      }
      ArrayCopy(buf, tmp, offset, 0, 16);
   }
};