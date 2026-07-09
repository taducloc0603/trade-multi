// TradesMemory.mqh
#property strict

#include "SharedMemoryBase.mqh"
#include "BinaryHelper.mqh"

#define TRADE_MEMORY_SIZE 4096 // 4KB
#define TRADE_RECORD_SIZE 100 // ticket(8) + lot(8) + price(8) + sl(8) + tp(8) + profit(8) + type(4) + time_msc(8) + open_ea_time_local(8) + symbol(32)
#define SYMBOL_SIZE 32

struct TradeInfo {
   ulong ticket;
   double lot;
   double price;
   double sl;
   double tp;
   double profit;
   int type;
   ulong time_msc;
   ulong open_ea_time_local;
   string symbol;
};

class CTradesMemory : public CSharedMemoryBase {
private:
   int m_lastCount;
   ulong m_knownTickets[];
   ulong m_knownEaTimes[];

public:
   CTradesMemory(string memory_name) : CSharedMemoryBase(memory_name, TRADE_MEMORY_SIZE) {
      m_lastCount = -1;
   }
   
   bool HasChanged() {
      return PositionsTotal() != m_lastCount;
   }
   
   void Update() {
      if(!IsValid()) return;
      
      int rawCount = PositionsTotal();
      int maxRecords = (TRADE_MEMORY_SIZE - HEADER_SIZE) / TRADE_RECORD_SIZE;
      int count = MathMin(rawCount, maxRecords);
      
      TradeInfo trades[];
      ArrayResize(trades, count);
      
      for(int i = 0; i < count; i++) {
         ulong ticket = PositionGetTicket(i);
         PositionSelectByTicket(ticket);
         
         trades[i].ticket = ticket;
         trades[i].lot = PositionGetDouble(POSITION_VOLUME);
         trades[i].price = PositionGetDouble(POSITION_PRICE_OPEN);
         trades[i].sl = PositionGetDouble(POSITION_SL);
         trades[i].tp = PositionGetDouble(POSITION_TP);
         trades[i].profit = PositionGetDouble(POSITION_PROFIT);
         trades[i].type = (int)PositionGetInteger(POSITION_TYPE);
         trades[i].time_msc = (ulong)PositionGetInteger(POSITION_TIME_MSC);
         trades[i].open_ea_time_local = GetOrSetEaTime(ticket);
         trades[i].symbol = PositionGetString(POSITION_SYMBOL);
      }

      if(rawCount <= maxRecords)
         CleanupKnownTickets(trades, count);
      
      SortByTimeAsc(trades);
      
      uchar buf[];
      ArrayResize(buf, HEADER_SIZE + count * TRADE_RECORD_SIZE);
      ArrayInitialize(buf, 0);
      
      int offset = 0;
      
      // Header
      CBinaryHelper::PackInt32(buf, offset, count);
      offset += 4;
      
      CBinaryHelper::PackUInt64(buf, offset, GetTickCount64());
      offset += 8;

      // Connected flag (offset 12) — thay cho padding. Tool đọc để bật kill-switch.
      // Ghi mỗi Update (OnTimer/OnTick/OnTrade) nên vẫn tươi kể cả khi mất kết nối.
      CBinaryHelper::PackInt32(buf, offset, (int)TerminalInfoInteger(TERMINAL_CONNECTED));
      offset += 4;
      
      // Trades (đã sorted)
      for(int i = 0; i < count; i++) {
         CBinaryHelper::PackUInt64(buf, offset, trades[i].ticket);
         offset += 8;
         
         CBinaryHelper::PackDouble(buf, offset, trades[i].lot);
         offset += 8;
         
         CBinaryHelper::PackDouble(buf, offset, trades[i].price);
         offset += 8;
         
         CBinaryHelper::PackDouble(buf, offset, trades[i].sl);
         offset += 8;
         
         CBinaryHelper::PackDouble(buf, offset, trades[i].tp);
         offset += 8;
         
         CBinaryHelper::PackDouble(buf, offset, trades[i].profit);
         offset += 8;
         
         CBinaryHelper::PackInt32(buf, offset, trades[i].type);
         offset += 4;

         CBinaryHelper::PackUInt64(buf, offset, trades[i].time_msc);
         offset += 8;

         CBinaryHelper::PackUInt64(buf, offset, trades[i].open_ea_time_local);
         offset += 8;

         CBinaryHelper::PackString(buf, offset, trades[i].symbol, SYMBOL_SIZE);
         offset += SYMBOL_SIZE;
      }
      
      CommitWrite(buf);
      m_lastCount = rawCount;
   }
   
private:
   ulong GetOrSetEaTime(ulong ticket) {
      int n = ArraySize(m_knownTickets);
      for(int i = 0; i < n; i++) {
         if(m_knownTickets[i] == ticket)
            return m_knownEaTimes[i];
      }
      ArrayResize(m_knownTickets, n + 1);
      ArrayResize(m_knownEaTimes, n + 1);
      m_knownTickets[n] = ticket;
      m_knownEaTimes[n] = GetTickCount64();
      return m_knownEaTimes[n];
   }

   void CleanupKnownTickets(TradeInfo &active[], int activeCount) {
      int n = ArraySize(m_knownTickets);
      for(int i = n - 1; i >= 0; i--) {
         bool found = false;
         for(int j = 0; j < activeCount; j++) {
            if(active[j].ticket == m_knownTickets[i]) { found = true; break; }
         }
         if(!found) {
            for(int k = i; k < n - 1; k++) {
               m_knownTickets[k] = m_knownTickets[k + 1];
               m_knownEaTimes[k] = m_knownEaTimes[k + 1];
            }
            n--;
            ArrayResize(m_knownTickets, n);
            ArrayResize(m_knownEaTimes, n);
         }
      }
   }

   void SortByTimeAsc(TradeInfo &arr[]) {
      int n = ArraySize(arr);
      for(int i = 0; i < n - 1; i++) {
         for(int j = i + 1; j < n; j++) {
            if(arr[i].time_msc > arr[j].time_msc) {  // Cũ lên trước
               TradeInfo temp = arr[i];
               arr[i] = arr[j];
               arr[j] = temp;
            }
         }
      }
   }
};
