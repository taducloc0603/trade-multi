#property copyright "2026 Hoang Anh"
#property description "Lấy Tick, Trades, History từ MT4"

#include "Configs4.mqh"
#include "TradesMemory4.mqh"
#include "HistoryMemory4.mqh"
#include "TickMemory4.mqh" 

CTradesMemory* g_trades = NULL;
CHistoryMemory* g_history = NULL;
CTickMemory* g_tick = NULL;

input string EA_CHANNEL_ID = "A";
input int UPDATE_INTERVAL_MS = 50;  // fallback quiet-market; >=10ms để MT4 không reject timer (tick vẫn realtime qua OnTick)

string TRADES_MEMORY_NAME = StringFormat("Local\\MT_%s_Trades", EA_CHANNEL_ID);
string HISTORY_MEMORY_NAME = StringFormat("Local\\MT_%s_History", EA_CHANNEL_ID);
string TICK_MEMORY_NAME = StringFormat("Local\\MT_%s_Tick", EA_CHANNEL_ID);

int OnInit() {
   g_trades = new CTradesMemory(TRADES_MEMORY_NAME);
   if(!g_trades.Init()) {
      Print(StringFormat("[X] Tạo Trade memory thất bại: %s ", TRADES_MEMORY_NAME));
      delete g_trades;
      return INIT_FAILED;
   }
   
   g_history = new CHistoryMemory(HISTORY_MEMORY_NAME);
   if(!g_history.Init()) {
      Print(StringFormat("[X] Tạo History memory thất bại: %s ", HISTORY_MEMORY_NAME));
      delete g_trades;
      delete g_history;
      return INIT_FAILED;
   }

   g_tick = new CTickMemory(TICK_MEMORY_NAME); 
   if(!g_tick.Init()) {
      Print(StringFormat("[X] Tạo Tick memory thất bại: %s ", TICK_MEMORY_NAME));
      delete g_trades;
      delete g_history;
      delete g_tick;
      return INIT_FAILED;
   }
   
   g_trades.Update();
   g_history.Update();
   g_tick.Update();
   
   EventSetMillisecondTimer(UPDATE_INTERVAL_MS);
   Print("[OK] Khởi động thành công. Channel ID: ", EA_CHANNEL_ID);
   return INIT_SUCCEEDED;
}

void OnTick() {
   g_tick.Update();
   // Update trades mọi tick để bắt thay đổi profit/SL/TP/partial-close —
   // HasChanged() chỉ so sánh count nên sẽ miss các trường hợp này.
   if(g_trades) g_trades.Update();
   if(g_history && g_history.HasChanged()) g_history.Update();
}

void OnTrade() {
   // Bắt buộc Update khi có sự kiện trade (close/open/modify) — tránh
   // shared memory đứng yên khi không có tick + timer không fire (xảy ra
   // khi UPDATE_INTERVAL_MS < 10 thì MT4 reject silently).
   if(g_trades) g_trades.Update();
   if(g_history) g_history.Update();
}

void OnTimer() {
   // Fallback khi không có tick — đảm bảo trades/history không stale.
   if(g_trades) g_trades.Update();
   if(g_history && g_history.HasChanged()) g_history.Update();
}

void OnDeinit(const int reason) {
   EventKillTimer();

   if(g_trades) delete g_trades;
   if(g_history) delete g_history;
   if(g_tick) delete g_tick;
   
   Print("[OK] Dọn dẹp xong.");
}
