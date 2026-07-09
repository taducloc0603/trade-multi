// TradesMemory4.mqh

#include "SharedMemoryBase4.mqh"
#include "BinaryHelper4.mqh"

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
      return OrdersTotal() != m_lastCount;
   }
   
   void Update() {
      if(!IsValid()) return;
      
      int total = OrdersTotal();
      int maxRecords = (TRADE_MEMORY_SIZE - HEADER_SIZE) / TRADE_RECORD_SIZE;
      TradeInfo trades[];
      ArrayResize(trades, 0);
      int count = 0;

      // Lặp qua tất cả các đơn hàng mở
      for(int i = 0; i < total; i++) {
         if(!OrderSelect(i, SELECT_BY_POS, MODE_TRADES)) continue;
         if(OrderSymbol() != Symbol()) continue;     // Lọc theo symbol hiện tại
         if(OrderType() > OP_SELL) continue;         // Bỏ qua pending orders (LIMIT/STOP) — đồng bộ với MT5 PositionsTotal()
         if(count >= maxRecords) break;              // chặn ghi vượt view TRADE_MEMORY_SIZE

         // Mở rộng mảng
         ArrayResize(trades, count + 1);
         
         trades[count].ticket = (ulong)OrderTicket();
         trades[count].lot = OrderLots();
         trades[count].price = OrderOpenPrice();
         trades[count].sl = OrderStopLoss();
         trades[count].tp = OrderTakeProfit();
         trades[count].profit = OrderProfit();
         trades[count].type = OrderType();
         trades[count].time_msc = (ulong)OrderOpenTime() * 1000; // MQ4 không có ms, xấp xỉ
         trades[count].open_ea_time_local = GetOrSetEaTime((ulong)OrderTicket());
         trades[count].symbol = OrderSymbol();

         count++;
      }

      CleanupKnownTickets(trades, count);
      SortByTimeAsc(trades);
      
      uchar buf[];
      ArrayResize(buf, HEADER_SIZE + count * TRADE_RECORD_SIZE);
      ArrayInitialize(buf, 0);
      
      int offset = 0;
      
      // Header
      CBinaryHelper::PackInt32(buf, offset, count);
      offset += 4;
      
      CBinaryHelper::PackUInt64(buf, offset, (ulong)GetTickCount());
      offset += 8;

      // Connected flag (offset 12) — thay cho padding. Tool đọc để bật kill-switch.
      // Ghi mỗi Update (OnTimer/OnTick/OnTrade) nên vẫn tươi kể cả khi mất kết nối.
      CBinaryHelper::PackInt32(buf, offset, (int)TerminalInfoInteger(TERMINAL_CONNECTED));
      offset += 4;
      
      // Trades (đã sorted)
      for(int j = 0; j < count; j++) {
         CBinaryHelper::PackUInt64(buf, offset, trades[j].ticket);
         offset += 8;
         
         CBinaryHelper::PackDouble(buf, offset, trades[j].lot);
         offset += 8;
         
         CBinaryHelper::PackDouble(buf, offset, trades[j].price);
         offset += 8;
         
         CBinaryHelper::PackDouble(buf, offset, trades[j].sl);
         offset += 8;
         
         CBinaryHelper::PackDouble(buf, offset, trades[j].tp);
         offset += 8;
         
         CBinaryHelper::PackDouble(buf, offset, trades[j].profit);
         offset += 8;
         
         CBinaryHelper::PackInt32(buf, offset, trades[j].type);
         offset += 4;

         CBinaryHelper::PackUInt64(buf, offset, trades[j].time_msc);
         offset += 8;

         CBinaryHelper::PackUInt64(buf, offset, trades[j].open_ea_time_local);
         offset += 8;

         CBinaryHelper::PackString(buf, offset, trades[j].symbol, SYMBOL_SIZE);
         offset += SYMBOL_SIZE;
      }
      
      CommitWrite(buf);
      m_lastCount = count;
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
      m_knownEaTimes[n] = (ulong)GetTickCount();
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
