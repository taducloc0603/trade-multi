// HistoryMemory.mqh
#property strict

#include "SharedMemoryBase.mqh"
#include "BinaryHelper.mqh"

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
   ulong m_knownPosIds[];
   ulong m_knownEaTimes[];

public:
   CHistoryMemory(string memory_name) : CSharedMemoryBase(memory_name, HISTORY_MEMORY_SIZE) {
      m_lastCount = -1;
   }

   bool HasChanged() {
      HistorySelect(TimeCurrent() - HISTORY_PERIOD_SECONDS, TimeCurrent());
      return HistoryDealsTotal() != m_lastCount;
   }

   void Update() {
      if(!IsValid()) return;

      HistorySelect(TimeCurrent() - HISTORY_PERIOD_SECONDS, TimeCurrent());
      int total = HistoryDealsTotal();
      int maxRecords = (HISTORY_MEMORY_SIZE - HEADER_SIZE) / HISTORY_RECORD_SIZE;

      DealInfo deals[];
      ArrayResize(deals, MathMin(total, maxRecords));
      int count = 0;

      for(int i = 0; i < total; i++) {
         if(count >= maxRecords)
            break;

         ulong deal_ticket = HistoryDealGetTicket(i);

         if(HistoryDealGetInteger(deal_ticket, DEAL_ENTRY) != DEAL_ENTRY_OUT)
            continue;

         ulong pos_id = (ulong)HistoryDealGetInteger(deal_ticket, DEAL_POSITION_ID);
         int deal_type = (int)HistoryDealGetInteger(deal_ticket, DEAL_TYPE);

         // Position type is opposite of exit deal type
         deals[count].ticket = pos_id;
         deals[count].symbol = HistoryDealGetString(deal_ticket, DEAL_SYMBOL);
         deals[count].type = (deal_type == DEAL_TYPE_BUY) ? 1 : 0; // exit BUY = position was SELL
         deals[count].volume = HistoryDealGetDouble(deal_ticket, DEAL_VOLUME);
         deals[count].close_price = HistoryDealGetDouble(deal_ticket, DEAL_PRICE);
         deals[count].profit = HistoryDealGetDouble(deal_ticket, DEAL_PROFIT);
         deals[count].commission = HistoryDealGetDouble(deal_ticket, DEAL_COMMISSION);
         deals[count].close_time_msc = (ulong)HistoryDealGetInteger(deal_ticket, DEAL_TIME_MSC);
         deals[count].close_ea_time_local = GetOrSetEaTime(pos_id);

         // Find entry deal for open_price and open_time
         deals[count].open_price = 0;
         deals[count].open_time_msc = 0;
         deals[count].sl = 0;
         deals[count].tp = 0;

         for(int j = 0; j < total; j++) {
            ulong entry_ticket = HistoryDealGetTicket(j);
            if((ulong)HistoryDealGetInteger(entry_ticket, DEAL_POSITION_ID) == pos_id &&
               HistoryDealGetInteger(entry_ticket, DEAL_ENTRY) == DEAL_ENTRY_IN) {
               deals[count].open_price = HistoryDealGetDouble(entry_ticket, DEAL_PRICE);
               deals[count].open_time_msc = (ulong)HistoryDealGetInteger(entry_ticket, DEAL_TIME_MSC);
               deals[count].commission += HistoryDealGetDouble(entry_ticket, DEAL_COMMISSION);
               break;
            }
         }

         // Find SL/TP from last history order of this position
         int orders_total = HistoryOrdersTotal();
         for(int k = orders_total - 1; k >= 0; k--) {
            ulong order_ticket = HistoryOrderGetTicket(k);
            if((ulong)HistoryOrderGetInteger(order_ticket, ORDER_POSITION_ID) == pos_id) {
               deals[count].sl = HistoryOrderGetDouble(order_ticket, ORDER_SL);
               deals[count].tp = HistoryOrderGetDouble(order_ticket, ORDER_TP);
               break;
            }
         }

         count++;
      }

      ArrayResize(deals, count);

      SortByTimeAsc(deals);

      uchar buf[];
      ArrayResize(buf, HEADER_SIZE + count * HISTORY_RECORD_SIZE);
      ArrayInitialize(buf, 0);

      int offset = 0;

      // Header
      CBinaryHelper::PackInt32(buf, offset, count);
      offset += 4;

      CBinaryHelper::PackUInt64(buf, offset, GetTickCount64());
      offset += 8;

      offset += 4; // padding

      // Deals
      for(int i = 0; i < count; i++) {
         CBinaryHelper::PackUInt64(buf, offset, deals[i].ticket);
         offset += 8;

         CBinaryHelper::PackInt32(buf, offset, deals[i].type);
         offset += 4;

         CBinaryHelper::PackDouble(buf, offset, deals[i].volume);
         offset += 8;

         CBinaryHelper::PackDouble(buf, offset, deals[i].open_price);
         offset += 8;

         CBinaryHelper::PackDouble(buf, offset, deals[i].close_price);
         offset += 8;

         CBinaryHelper::PackDouble(buf, offset, deals[i].sl);
         offset += 8;

         CBinaryHelper::PackDouble(buf, offset, deals[i].tp);
         offset += 8;

         CBinaryHelper::PackDouble(buf, offset, deals[i].commission);
         offset += 8;

         CBinaryHelper::PackDouble(buf, offset, deals[i].profit);
         offset += 8;

         CBinaryHelper::PackUInt64(buf, offset, deals[i].open_time_msc);
         offset += 8;

         CBinaryHelper::PackUInt64(buf, offset, deals[i].close_time_msc);
         offset += 8;

         CBinaryHelper::PackUInt64(buf, offset, deals[i].close_ea_time_local);
         offset += 8;

         CBinaryHelper::PackString(buf, offset, deals[i].symbol, SYMBOL_SIZE);
         offset += SYMBOL_SIZE;
      }

      CommitWrite(buf);
      m_lastCount = total;
   }

private:
   ulong GetOrSetEaTime(ulong pos_id) {
      int n = ArraySize(m_knownPosIds);
      for(int i = 0; i < n; i++) {
         if(m_knownPosIds[i] == pos_id)
            return m_knownEaTimes[i];
      }
      ArrayResize(m_knownPosIds, n + 1);
      ArrayResize(m_knownEaTimes, n + 1);
      m_knownPosIds[n] = pos_id;
      m_knownEaTimes[n] = GetTickCount64();
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
