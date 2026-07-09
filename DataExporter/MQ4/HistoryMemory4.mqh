// HistoryMemory4.mqh

#include "SharedMemoryBase4.mqh"
#include "BinaryHelper4.mqh"

#define HISTORY_MEMORY_SIZE 65536     // 64KB — chứa tới 528 deal/24h
// ticket(8) + type(4) + volume(8) + open_price(8) + close_price(8) + sl(8) + tp(8) + commission(8) + profit(8) + open_time_msc(8) + close_time_msc(8) + close_ea_time_local(8) + symbol(32) = 124
#define HISTORY_RECORD_SIZE 124

#ifndef SYMBOL_SIZE
#define SYMBOL_SIZE 32
#endif

struct DealInfo {
   ulong ticket;
   int type;
   double volume;
   double open_price;
   double close_price;
   double sl;
   double tp;
   double commission;
   double profit;
   ulong open_time_msc;
   ulong close_time_msc;
   ulong close_ea_time_local;
   string symbol;
};

class CHistoryMemory : public CSharedMemoryBase {
private:
   int m_lastCount;
   int m_lastOrdersHistTotal;   // tracking riêng cho HasChanged() — không trộn với m_lastCount đã filter
   ulong m_knownTickets[];
   ulong m_knownEaTimes[];

public:
   CHistoryMemory(string memory_name) : CSharedMemoryBase(memory_name, HISTORY_MEMORY_SIZE) {
      m_lastCount = -1;
      m_lastOrdersHistTotal = -1;
   }

   bool HasChanged() {
      // So sánh OrdersHistoryTotal() (raw, unfiltered) với snapshot lần update gần nhất.
      // Trước đây so với m_lastCount (đã filter 24h + skip pending) — luôn sai khác ⇒
      // HasChanged() luôn true ⇒ Update chạy mọi tick (đúng kết quả nhưng tốn CPU).
      return OrdersHistoryTotal() != m_lastOrdersHistTotal;
   }

   void Update() {
      if(!IsValid()) return;

      int total = OrdersHistoryTotal();
      int maxRecords = (HISTORY_MEMORY_SIZE - HEADER_SIZE) / HISTORY_RECORD_SIZE;
      DealInfo deals[];
      ArrayResize(deals, 0);
      int count = 0;

      datetime startTime = TimeCurrent() - HISTORY_PERIOD_SECONDS;

      for(int i = 0; i < total; i++) {
         if(!OrderSelect(i, SELECT_BY_POS, MODE_HISTORY)) continue;
         if(OrderCloseTime() < startTime) continue;
         if(OrderType() > OP_SELL) continue;
         if(count >= maxRecords) break; // chặn ghi vượt view HISTORY_MEMORY_SIZE

         ArrayResize(deals, count + 1);

         deals[count].ticket = (ulong)OrderTicket();
         deals[count].symbol = OrderSymbol();
         deals[count].type = OrderType();
         deals[count].volume = OrderLots();
         deals[count].open_price = OrderOpenPrice();
         deals[count].close_price = OrderClosePrice();
         deals[count].sl = OrderStopLoss();
         deals[count].tp = OrderTakeProfit();
         deals[count].commission = OrderCommission();
         deals[count].profit = OrderProfit();
         deals[count].open_time_msc = (ulong)OrderOpenTime() * 1000; // MQ4 không có ms, xấp xỉ
         deals[count].close_time_msc = (ulong)OrderCloseTime() * 1000;
         deals[count].close_ea_time_local = GetOrSetEaTime((ulong)OrderTicket());

         count++;
      }

      SortByTimeAsc(deals);

      uchar buf[];
      ArrayResize(buf, HEADER_SIZE + count * HISTORY_RECORD_SIZE);
      ArrayInitialize(buf, 0);

      int offset = 0;

      // Header
      CBinaryHelper::PackInt32(buf, offset, count);
      offset += 4;

      CBinaryHelper::PackUInt64(buf, offset, (ulong)GetTickCount());
      offset += 8;

      offset += 4; // padding

      // Deals
      for(int j = 0; j < count; j++) {
         CBinaryHelper::PackUInt64(buf, offset, deals[j].ticket);
         offset += 8;

         CBinaryHelper::PackInt32(buf, offset, deals[j].type);
         offset += 4;

         CBinaryHelper::PackDouble(buf, offset, deals[j].volume);
         offset += 8;

         CBinaryHelper::PackDouble(buf, offset, deals[j].open_price);
         offset += 8;

         CBinaryHelper::PackDouble(buf, offset, deals[j].close_price);
         offset += 8;

         CBinaryHelper::PackDouble(buf, offset, deals[j].sl);
         offset += 8;

         CBinaryHelper::PackDouble(buf, offset, deals[j].tp);
         offset += 8;

         CBinaryHelper::PackDouble(buf, offset, deals[j].commission);
         offset += 8;

         CBinaryHelper::PackDouble(buf, offset, deals[j].profit);
         offset += 8;

         CBinaryHelper::PackUInt64(buf, offset, deals[j].open_time_msc);
         offset += 8;

         CBinaryHelper::PackUInt64(buf, offset, deals[j].close_time_msc);
         offset += 8;

         CBinaryHelper::PackUInt64(buf, offset, deals[j].close_ea_time_local);
         offset += 8;

         CBinaryHelper::PackString(buf, offset, deals[j].symbol, SYMBOL_SIZE);
         offset += SYMBOL_SIZE;
      }

      CommitWrite(buf);
      m_lastCount = count;
      m_lastOrdersHistTotal = OrdersHistoryTotal();
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

   void SortByTimeAsc(DealInfo &arr[]) {
      int n = ArraySize(arr);
      for(int i = 0; i < n - 1; i++) {
         for(int j = i + 1; j < n; j++) {
            if(arr[i].close_time_msc > arr[j].close_time_msc) {
               DealInfo temp = arr[i];
               arr[i] = arr[j];
               arr[j] = temp;
            }
         }
      }
   }
};
