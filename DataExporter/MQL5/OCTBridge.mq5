//+------------------------------------------------------------------+
//| OCTBridge.mq5                                                     |
//| MT5 execution bridge using SharedMem IPC                          |
//+------------------------------------------------------------------+
#property copyright "Copyright 2024"
#property link      ""
#property version   "2.02"
#property strict

#include "SharedMem.mqh"

input string InpRoomId              = "AUTO";
input bool   InpDebugLog            = false;
input ulong  InpMagicNumber         = 88005001;
input uint   InpMaxDeviationPoints  = 20;
input int    InpPendingTimeoutMs     = 10000;
input bool   InpAutoDpiScale        = true;
input int    InpBuyOffsetX          = 160;
input int    InpBuyOffsetY          = 62;
input int    InpSellOffsetX         = 50;
input int    InpSellOffsetY         = 62;
input int    InpVolumeOffsetX       = 105;
input int    InpVolumeOffsetY       = 33;
input double InpVolumeProbeValue    = 0.0;

#define BRIDGE_HEALTH_INTERVAL_MS 500
#define BRIDGE_MAX_PENDING        32
#define BRIDGE_DEDUP_CAPACITY     128
#define BRIDGE_MANUAL_PENDING     4
#define WM_MOUSEMOVE              0x0200
#define WM_LBUTTONDOWN            0x0201
#define WM_LBUTTONUP              0x0202
#define WM_KEYDOWN                0x0100
#define WM_KEYUP                  0x0101
#define WM_CHAR                   0x0102
#define MK_LBUTTON                0x0001
#define VK_CONTROL                0x0011
#define VK_A                      0x0041
#define VK_C                      0x0043
#define VK_RETURN                 0x000D
#define VK_ESCAPE                 0x001B
#define WM_SETTEXT                0x000C
#define EM_SETSEL                 0x00B1
#define KEYEVENTF_KEYUP           0x0002
#define GA_ROOT                   2
#define CF_UNICODETEXT            13

struct BridgeWindowRect
{
   int left;
   int top;
   int right;
   int bottom;
};

struct BridgeManualUiProbe
{
   bool panel_found;
   bool coordinates_measured;
   long chart_id;
   long chart_hwnd;
   long panel_hwnd;
   int dpi;
   int panel_width;
   int panel_height;
   int chart_width;
   int chart_height;
   int buy_x;
   int buy_y;
   int sell_x;
   int sell_y;
};

#import "user32.dll"
bool PostMessageW(long hWnd, uint Msg, ulong wParam, long lParam);
long FindWindowExW(long hwndParent, long hwndChildAfter,
                   string lpszClass, string lpszWindow);
bool GetWindowRect(long hWnd, BridgeWindowRect &lpRect);
bool ScreenToClient(long hWnd, int &lpPoint[]);
bool IsWindow(long hWnd);
long GetFocus();
long SendMessageW(long hWnd, uint Msg, ulong wParam, long lParam);
long SendMessageW(long hWnd, uint Msg, ulong wParam, string lParam);
long GetAncestor(long hWnd, uint gaFlags);
bool SetForegroundWindow(long hWnd);
long GetForegroundWindow();
bool BringWindowToTop(long hWnd);
bool ShowWindowAsync(long hWnd, int nCmdShow);
long SetFocus(long hWnd);
short VkKeyScanW(ushort ch);
void keybd_event(uchar virtualKey, uchar scanCode, uint flags, ulong extraInfo);
bool OpenClipboard(long hWndNewOwner);
long GetClipboardData(uint format);
bool EmptyClipboard();
bool CloseClipboard();
#import

#import "kernel32.dll"
ulong GlobalLock(long memoryHandle);
bool GlobalUnlock(long memoryHandle);
int lstrlenW(ulong value);
#import

struct BridgePendingExecution
{
   bool   active;
   string action;
   string request_id;
   string symbol;
   string side;
   double volume;
   uint   mt_request_id;
   ulong  order_ticket;
   ulong  deal_ticket;
   ulong  position_ticket;
   ulong  dispatched_ms;
   bool   timeout_reported;
};

struct BridgeCachedResult
{
   bool   used;
   string request_id;
   string status;
   ulong  ticket;
   ulong  deal;
   double price;
   double volume;
   uint   retcode;
   string detail;
};

struct BridgeManualOpenExecution
{
   bool   active;
   bool   click_dispatched;
   string request_id;
   string symbol;
   string side;
   double volume;
   long   chart_id;
   long   chart_hwnd;
   int    click_x;
   int    click_y;
   int    baseline_count;
   double baseline_volume;
   ulong  baseline_hash;
   ulong  prepared_ms;
   ulong  dispatched_ms;
   datetime dispatched_server_time;
};

CSharedMemRing   g_shm;
string           g_roomId;
ulong            g_generation = 0;
ulong            g_lastHealthPublish = 0;
long             g_accountLogin = 0;
BridgePendingExecution g_pending[BRIDGE_MAX_PENDING];
BridgeCachedResult g_dedup[BRIDGE_DEDUP_CAPACITY];
int              g_dedupNext = 0;
string           g_lastManualUiCode = "";
BridgeManualOpenExecution g_manualOpen[BRIDGE_MANUAL_PENDING];
bool             g_volumeCacheValid = false;
string           g_volumeCacheSymbol = "";
double           g_volumeCacheValue = 0;

uint BuildWriterUid(const string identity)
{
   uint hash = 2166136261;
   for(int i = 0; i < StringLen(identity); i++)
     {
      hash ^= (uint)StringGetCharacter(identity, i);
      hash *= 16777619;
     }
   return (hash == 0) ? 1 : hash;
}

int OnInit()
{
   string roomId = InpRoomId;
   StringTrimLeft(roomId);
   StringTrimRight(roomId);

   if(roomId == "" || roomId == "AUTO" || roomId == "0")
     {
      long login = AccountInfoInteger(ACCOUNT_LOGIN);
      roomId = (login > 0) ? IntegerToString(login) : "OCTBridge";
     }

   g_roomId = roomId;
   g_accountLogin = AccountInfoInteger(ACCOUNT_LOGIN);
   string identity = "EA_OCTBridge_" + g_roomId + "_" + IntegerToString(ChartID());
   uint writerUid = BuildWriterUid(identity);
   g_generation = GetTickCount64();
   if(g_generation == 0) g_generation = 1;

   if(!g_shm.Open(g_roomId, writerUid, g_generation))
     {
      Print("ERROR: Cannot open shared memory room: ", g_roomId);
      return INIT_FAILED;
     }

   EventSetMillisecondTimer(50);
   PublishBridgeState("bridge_ready");
   Print("MT5 Bridge ready: room=", g_roomId, " account=", g_accountLogin,
         " generation=", g_generation, " lane=", g_shm.MyLane());
   if(InpDebugLog) LogManualUiProbe(ChartID());
   if(InpDebugLog && InpVolumeProbeValue > 0)
      ProbeSetManualUiVolume(ChartID(), InpVolumeProbeValue);
   return INIT_SUCCEEDED;
}

void OnDeinit(const int reason)
{
   EventKillTimer();
   g_shm.Close();
   if(InpDebugLog) Print("MT5 Bridge stopped");
}

void OnTimer()
{
   if(!g_shm.IsReady()) return;

   ulong now = GetTickCount64();
   if((now - g_lastHealthPublish) >= BRIDGE_HEALTH_INTERVAL_MS)
      PublishBridgeState("heartbeat");

   string messages = g_shm.Receive("");
   if(StringLen(messages) > 0)
     {
      string lines[];
      int count = StringSplit(messages, '\n', lines);
      for(int i = 0; i < count; i++)
         if(StringLen(lines[i]) > 0)
            ProcessRequest(lines[i]);
     }

   // Request handling may perform UI work and update dispatch timestamps.
   // Refresh now so unsigned elapsed calculations cannot underflow.
   now = GetTickCount64();
   ReconcileManualOpenDeals();
   SweepPendingTimeouts(now);
   SweepManualOpenTimeouts(now);
}

void ProcessRequest(const string json_msg)
{
   int version = (int)StringToInteger(ExtractJsonString(json_msg, "v"));
   string type = ExtractJsonString(json_msg, "type");

   if(version != 1)
     {
      string request_id = ExtractJsonString(json_msg, "request_id");
      if(StringLen(request_id) > 0)
         SendExecutionResult(request_id, "invalid", 0, 0, 0, 0, 0,
                             "unsupported_protocol_version");
      return;
     }

   if(type == "ping")
     {
      ProcessPing(json_msg);
      return;
     }

   if(type == "open")
     {
      ProcessOpen(json_msg);
      return;
     }

   if(type == "close")
     {
      ProcessClose(json_msg);
      return;
     }

   string request_id = ExtractJsonString(json_msg, "request_id");
   if(StringLen(request_id) > 0)
      SendExecutionResult(request_id, "invalid", 0, 0, 0, 0, 0,
                          "unsupported_command");
}

void ProcessPing(const string json_msg)
{
   string request_id = ExtractJsonString(json_msg, "request_id");
   if(!IsSafeIdentifier(request_id)) return;

   string pong = StringFormat(
      "{\"v\":1,\"type\":\"pong\",\"request_id\":\"%s\",\"account\":%I64d,\"generation\":%I64u,\"ea_ms\":%I64u}",
      request_id, g_accountLogin, g_generation, GetTickCount64());
   if(!g_shm.Send(pong))
      Print("ERROR: Cannot publish pong, err=", g_shm.GetLastSocketError());
}

void ProcessOpen(const string json_msg)
{
   string request_id = ExtractJsonString(json_msg, "request_id");
   if(!IsSafeIdentifier(request_id))
     {
      if(StringLen(request_id) > 0)
         SendExecutionResult(request_id, "invalid", 0, 0, 0, 0, 0,
                             "invalid_request_id");
      return;
     }

   int cached = FindCached(request_id);
   if(cached >= 0)
     {
      SendCachedResult(cached);
      return;
     }

   if(FindPendingByRequestId(request_id) >= 0 ||
      FindManualOpenByRequestId(request_id) >= 0)
     {
      SendExecutionResult(request_id, "duplicate", 0, 0, 0, 0, 0,
                          "request_in_progress");
      return;
     }

   long account = StringToInteger(ExtractJsonString(json_msg, "account"));
   string symbol = ExtractJsonString(json_msg, "symbol");
   string side = ExtractJsonString(json_msg, "side");
   double volume = StringToDouble(ExtractJsonString(json_msg, "volume"));
   ulong expires_ms = (ulong)StringToInteger(ExtractJsonString(json_msg, "expires_ms"));

   int manual_index = -1;
   string error = PrepareManualOpenExecution(
      request_id, account, symbol, side, volume, expires_ms, manual_index);
   if(StringLen(error) > 0)
     {
      string status = (error == "request_expired") ? "expired" : "invalid";
      FinalizeImmediate(request_id, status, 0, 0, 0, volume, 0, error);
      return;
     }

   SendExecutionResult(request_id, "received", 0, 0, 0, volume, 0, "accepted");
   error = DispatchPreparedManualOpen(manual_index);
   if(StringLen(error) > 0)
     {
      ResetManualOpenExecution(manual_index);
      FinalizeImmediate(request_id, "rejected", 0, 0, 0, volume, 0, error);
      return;
     }

   SendExecutionResult(request_id, "dispatched", 0, 0, 0, volume, 0,
                       "manual_ui_click_dispatched");
}

string ValidateOpen(const long account, const string symbol, const string side,
                    const double volume, const ulong expires_ms)
{
   if(account != g_accountLogin) return "account_mismatch";
   if(expires_ms == 0 || expires_ms <= GetTickCount64()) return "request_expired";
   if(side != "BUY" && side != "SELL") return "invalid_side";
   if(StringLen(symbol) == 0 || !SymbolSelect(symbol, true)) return "invalid_symbol";
   if(!TerminalInfoInteger(TERMINAL_TRADE_ALLOWED) ||
      !MQLInfoInteger(MQL_TRADE_ALLOWED) ||
      !AccountInfoInteger(ACCOUNT_TRADE_ALLOWED))
      return "trading_not_allowed";

   double min_volume = SymbolInfoDouble(symbol, SYMBOL_VOLUME_MIN);
   double max_volume = SymbolInfoDouble(symbol, SYMBOL_VOLUME_MAX);
   double step = SymbolInfoDouble(symbol, SYMBOL_VOLUME_STEP);
   if(volume <= 0 || step <= 0 || volume < min_volume || volume > max_volume)
      return "invalid_volume";

   double steps = MathRound((volume - min_volume) / step);
   double normalized = min_volume + steps * step;
   if(MathAbs(normalized - volume) > step * 0.000001)
      return "invalid_volume_step";

   long trade_mode = SymbolInfoInteger(symbol, SYMBOL_TRADE_MODE);
   if(trade_mode == SYMBOL_TRADE_MODE_DISABLED ||
      trade_mode == SYMBOL_TRADE_MODE_CLOSEONLY)
      return "symbol_not_openable";
   if(side == "BUY" && trade_mode == SYMBOL_TRADE_MODE_SHORTONLY)
      return "symbol_short_only";
   if(side == "SELL" && trade_mode == SYMBOL_TRADE_MODE_LONGONLY)
      return "symbol_long_only";
   return "";
}

void ProcessClose(const string json_msg)
{
   string request_id = ExtractJsonString(json_msg, "request_id");
   if(!IsSafeIdentifier(request_id))
     {
      if(StringLen(request_id) > 0)
         SendExecutionResult(request_id, "invalid", 0, 0, 0, 0, 0,
                             "invalid_request_id");
      return;
     }

   int cached = FindCached(request_id);
   if(cached >= 0)
     {
      SendCachedResult(cached);
      return;
     }
   if(FindPendingByRequestId(request_id) >= 0)
     {
      SendExecutionResult(request_id, "duplicate", 0, 0, 0, 0, 0,
                          "request_in_progress");
      return;
     }

   long account = StringToInteger(ExtractJsonString(json_msg, "account"));
   ulong position_ticket = (ulong)StringToInteger(ExtractJsonString(json_msg, "ticket"));
   string requested_symbol = ExtractJsonString(json_msg, "symbol");
   double requested_volume = StringToDouble(ExtractJsonString(json_msg, "volume"));
   ulong expires_ms = (ulong)StringToInteger(ExtractJsonString(json_msg, "expires_ms"));

   if(account != g_accountLogin)
     {
      FinalizeImmediate(request_id, "invalid", position_ticket, 0, 0, 0, 0,
                        "account_mismatch");
      return;
     }
   if(expires_ms == 0 || expires_ms <= GetTickCount64())
     {
      FinalizeImmediate(request_id, "expired", position_ticket, 0, 0, 0, 0,
                        "request_expired");
      return;
     }
   if(position_ticket == 0)
     {
      FinalizeImmediate(request_id, "invalid", 0, 0, 0, 0, 0,
                        "invalid_ticket");
      return;
     }
   // Idempotent close: a missing position means the requested end state is
   // already reached. A duplicate request ID will also replay this result.
   if(!PositionSelectByTicket(position_ticket))
     {
      FinalizeImmediate(request_id, "already_closed", position_ticket, 0,
                        0, 0, 0, "position_not_found");
      return;
     }

   if(!TerminalInfoInteger(TERMINAL_TRADE_ALLOWED) ||
      !MQLInfoInteger(MQL_TRADE_ALLOWED) ||
      !AccountInfoInteger(ACCOUNT_TRADE_ALLOWED))
     {
      FinalizeImmediate(request_id, "invalid", position_ticket, 0, 0, 0, 0,
                        "trading_not_allowed");
      return;
     }

   string symbol = PositionGetString(POSITION_SYMBOL);
   if(StringLen(requested_symbol) > 0 && requested_symbol != symbol)
     {
      FinalizeImmediate(request_id, "invalid", position_ticket, 0, 0, 0, 0,
                        "position_symbol_mismatch");
      return;
     }
   if(!SymbolSelect(symbol, true))
     {
      FinalizeImmediate(request_id, "invalid", position_ticket, 0, 0, 0, 0,
                        "invalid_symbol");
      return;
     }

   double position_volume = PositionGetDouble(POSITION_VOLUME);
   double close_volume = (requested_volume <= 0) ? position_volume : requested_volume;
   string volume_error = ValidateCloseVolume(symbol, close_volume, position_volume);
   if(StringLen(volume_error) > 0)
     {
      FinalizeImmediate(request_id, "invalid", position_ticket, 0, 0,
                        close_volume, 0, volume_error);
      return;
     }

   int pending_index = AllocatePending();
   if(pending_index < 0)
     {
      FinalizeImmediate(request_id, "rejected", position_ticket, 0, 0,
                        close_volume, 0, "pending_queue_full");
      return;
     }

   SendExecutionResult(request_id, "received", position_ticket, 0, 0,
                       close_volume, 0, "accepted");

   ENUM_POSITION_TYPE position_type =
      (ENUM_POSITION_TYPE)PositionGetInteger(POSITION_TYPE);
   ENUM_ORDER_TYPE close_type =
      (position_type == POSITION_TYPE_BUY) ? ORDER_TYPE_SELL : ORDER_TYPE_BUY;

   MqlTradeRequest request;
   MqlTradeResult result;
   MqlTradeCheckResult check;
   ZeroMemory(request);
   ZeroMemory(result);
   ZeroMemory(check);
   request.action = TRADE_ACTION_DEAL;
   request.magic = InpMagicNumber;
   request.position = position_ticket;
   request.symbol = symbol;
   request.volume = close_volume;
   request.deviation = InpMaxDeviationPoints;
   request.type = close_type;
   request.price = (close_type == ORDER_TYPE_BUY)
                   ? SymbolInfoDouble(symbol, SYMBOL_ASK)
                   : SymbolInfoDouble(symbol, SYMBOL_BID);
   request.type_time = ORDER_TIME_GTC;
   request.type_filling = ResolveFillingMode(symbol);
   request.comment = "OCTC:" + StringSubstr(request_id, 0, 20);

   if(!OrderCheck(request, check))
     {
      FinalizeImmediate(request_id, "rejected", position_ticket, 0, 0,
                        close_volume, check.retcode, SanitizeDetail(check.comment));
      return;
     }

   g_pending[pending_index].active = true;
   g_pending[pending_index].action = "close";
   g_pending[pending_index].request_id = request_id;
   g_pending[pending_index].symbol = symbol;
   g_pending[pending_index].side =
      (close_type == ORDER_TYPE_BUY) ? "BUY" : "SELL";
   g_pending[pending_index].volume = close_volume;
   g_pending[pending_index].position_ticket = position_ticket;
   g_pending[pending_index].dispatched_ms = GetTickCount64();
   g_pending[pending_index].timeout_reported = false;

   if(!OrderSendAsync(request, result))
     {
      ReleasePending(pending_index);
      FinalizeImmediate(request_id, "rejected", position_ticket, 0, 0,
                        close_volume, result.retcode, SanitizeDetail(result.comment));
      return;
     }

   g_pending[pending_index].mt_request_id = result.request_id;
   g_pending[pending_index].order_ticket = result.order;
   g_pending[pending_index].deal_ticket = result.deal;
   SendExecutionResult(request_id, "dispatched", position_ticket, result.deal,
                       result.price, close_volume, result.retcode,
                       "close_order_send_async");
}

string ValidateCloseVolume(const string symbol, const double close_volume,
                           const double position_volume)
{
   double min_volume = SymbolInfoDouble(symbol, SYMBOL_VOLUME_MIN);
   double step = SymbolInfoDouble(symbol, SYMBOL_VOLUME_STEP);
   if(close_volume <= 0 || step <= 0 || close_volume > position_volume)
      return "invalid_close_volume";

   // A full close is always permitted at the current position volume. For a
   // partial close, enforce broker minimum and step.
   if(MathAbs(close_volume - position_volume) <= step * 0.000001)
      return "";
   if(close_volume < min_volume)
      return "close_volume_below_minimum";

   double steps = MathRound((close_volume - min_volume) / step);
   double normalized = min_volume + steps * step;
   if(MathAbs(normalized - close_volume) > step * 0.000001)
      return "invalid_close_volume_step";

   double remaining = position_volume - close_volume;
   if(remaining > step * 0.000001 && remaining < min_volume)
      return "remaining_volume_below_minimum";
   return "";
}

ENUM_ORDER_TYPE_FILLING ResolveFillingMode(const string symbol)
{
   long flags = SymbolInfoInteger(symbol, SYMBOL_FILLING_MODE);
   if((flags & SYMBOL_FILLING_FOK) == SYMBOL_FILLING_FOK)
      return ORDER_FILLING_FOK;
   if((flags & SYMBOL_FILLING_IOC) == SYMBOL_FILLING_IOC)
      return ORDER_FILLING_IOC;
   return ORDER_FILLING_RETURN;
}

void OnTradeTransaction(const MqlTradeTransaction &trans,
                        const MqlTradeRequest &request,
                        const MqlTradeResult &result)
{
   if(trans.type == TRADE_TRANSACTION_DEAL_ADD &&
      TryConfirmManualOpenFromDeal(trans.deal))
      return;

   int index = -1;
   if(result.request_id > 0)
      index = FindPendingByMtRequestId(result.request_id);
   if(index < 0 && trans.order > 0)
      index = FindPendingByOrder(trans.order);
   if(index < 0) return;

   if(result.order > 0) g_pending[index].order_ticket = result.order;
   if(result.deal > 0) g_pending[index].deal_ticket = result.deal;

   if(trans.type == TRADE_TRANSACTION_REQUEST)
     {
      if(!IsAcceptedRetcode(result.retcode))
        {
         ulong ticket = ResolveExecutionTicket(index, result.order, result.deal);
         FinalizePending(index, "rejected", ticket, result.deal,
                         result.price, result.volume, result.retcode,
                         SanitizeDetail(result.comment));
         return;
        }

      if(result.deal > 0 &&
         (result.retcode == TRADE_RETCODE_DONE ||
          result.retcode == TRADE_RETCODE_DONE_PARTIAL))
        {
         ulong ticket = ResolveExecutionTicket(index, result.order, result.deal);
         FinalizePending(index, "confirmed", ticket, result.deal,
                         result.price, result.volume, result.retcode, "deal_confirmed");
        }
      return;
     }

   if(trans.type == TRADE_TRANSACTION_DEAL_ADD)
     {
      double price = trans.price;
      double volume = trans.volume;
      ulong deal = trans.deal;
      ulong order = trans.order;
      ulong ticket = ResolveExecutionTicket(index, order, deal);
      string detail = (g_pending[index].action == "close")
                      ? "position_close_deal_added"
                      : "position_open_deal_added";
      FinalizePending(index, "confirmed", ticket, deal, price, volume,
                      TRADE_RETCODE_DONE, detail);
     }
}

ulong ResolveExecutionTicket(const int index, const ulong order,
                             const ulong deal)
{
   if(index >= 0 && index < BRIDGE_MAX_PENDING &&
      g_pending[index].action == "close")
      return g_pending[index].position_ticket;

   if(deal > 0 && HistoryDealSelect(deal))
     {
      ulong position_id = (ulong)HistoryDealGetInteger(deal, DEAL_POSITION_ID);
      if(position_id > 0) return position_id;
     }
   return order;
}

bool IsAcceptedRetcode(const uint retcode)
{
   return retcode == TRADE_RETCODE_PLACED ||
          retcode == TRADE_RETCODE_DONE ||
          retcode == TRADE_RETCODE_DONE_PARTIAL;
}

void SweepPendingTimeouts(const ulong now)
{
   for(int i = 0; i < BRIDGE_MAX_PENDING; i++)
     {
      if(!g_pending[i].active) continue;
      ulong age = now - g_pending[i].dispatched_ms;
      if(!g_pending[i].timeout_reported && age > (ulong)InpPendingTimeoutMs)
        {
         g_pending[i].timeout_reported = true;
         ulong ticket = ResolveExecutionTicket(i, g_pending[i].order_ticket,
                                               g_pending[i].deal_ticket);
         CacheResult(g_pending[i].request_id, "timeout",
                     ticket, g_pending[i].deal_ticket,
                     0, g_pending[i].volume, 0, "trade_transaction_timeout");
         SendExecutionResult(g_pending[i].request_id, "timeout",
                             ticket, g_pending[i].deal_ticket,
                             0, g_pending[i].volume, 0, "trade_transaction_timeout");
        }

      // Keep correlation alive for late broker transactions. Retire only after
      // five additional minutes to avoid exhausting the bounded pending table.
      if(g_pending[i].timeout_reported &&
         age > (ulong)InpPendingTimeoutMs + 300000)
         ReleasePending(i);
     }
}

void FinalizePending(const int index, const string status, const ulong ticket,
                     const ulong deal, const double price, const double volume,
                     const uint retcode, const string detail)
{
   if(index < 0 || index >= BRIDGE_MAX_PENDING || !g_pending[index].active) return;
   string request_id = g_pending[index].request_id;
   CacheResult(request_id, status, ticket, deal, price, volume, retcode, detail);
   SendExecutionResult(request_id, status, ticket, deal, price, volume, retcode, detail);
   ReleasePending(index);
}

void FinalizeImmediate(const string request_id, const string status,
                       const ulong ticket, const ulong deal, const double price,
                       const double volume, const uint retcode, const string detail)
{
   CacheResult(request_id, status, ticket, deal, price, volume, retcode, detail);
   SendExecutionResult(request_id, status, ticket, deal, price, volume, retcode, detail);
}

void SendExecutionResult(const string request_id, const string status,
                         const ulong ticket, const ulong deal, const double price,
                         const double volume, const uint retcode, const string detail)
{
   string message = "{\"v\":1,\"type\":\"execution_result\"" +
                    ",\"request_id\":\"" + request_id + "\"" +
                    ",\"status\":\"" + status + "\"" +
                    ",\"ticket\":" + IntegerToString(ticket) +
                    ",\"deal\":" + IntegerToString(deal) +
                    ",\"price\":" + DoubleToString(price, 10) +
                    ",\"volume\":" + DoubleToString(volume, 8) +
                    ",\"retcode\":" + IntegerToString(retcode) +
                    ",\"detail\":\"" + SanitizeDetail(detail) + "\"}";
   if(!g_shm.Send(message))
      Print("ERROR: Cannot publish execution result for ", request_id,
            ", err=", g_shm.GetLastSocketError());
}

void CacheResult(const string request_id, const string status,
                 const ulong ticket, const ulong deal, const double price,
                 const double volume, const uint retcode, const string detail)
{
   int index = FindCached(request_id);
   if(index < 0)
     {
      index = g_dedupNext;
      g_dedupNext = (g_dedupNext + 1) % BRIDGE_DEDUP_CAPACITY;
     }
   g_dedup[index].used = true;
   g_dedup[index].request_id = request_id;
   g_dedup[index].status = status;
   g_dedup[index].ticket = ticket;
   g_dedup[index].deal = deal;
   g_dedup[index].price = price;
   g_dedup[index].volume = volume;
   g_dedup[index].retcode = retcode;
   g_dedup[index].detail = detail;
}

int FindCached(const string request_id)
{
   for(int i = 0; i < BRIDGE_DEDUP_CAPACITY; i++)
      if(g_dedup[i].used && g_dedup[i].request_id == request_id)
         return i;
   return -1;
}

void SendCachedResult(const int index)
{
   SendExecutionResult(g_dedup[index].request_id, g_dedup[index].status,
                       g_dedup[index].ticket, g_dedup[index].deal,
                       g_dedup[index].price, g_dedup[index].volume,
                       g_dedup[index].retcode, g_dedup[index].detail);
}

int AllocatePending()
{
   for(int i = 0; i < BRIDGE_MAX_PENDING; i++)
      if(!g_pending[i].active) return i;
   return -1;
}

void ReleasePending(const int index)
{
   if(index < 0 || index >= BRIDGE_MAX_PENDING) return;
   g_pending[index].active = false;
   g_pending[index].action = "";
   g_pending[index].request_id = "";
   g_pending[index].mt_request_id = 0;
   g_pending[index].order_ticket = 0;
   g_pending[index].deal_ticket = 0;
   g_pending[index].position_ticket = 0;
   g_pending[index].timeout_reported = false;
}

int FindPendingByRequestId(const string request_id)
{
   for(int i = 0; i < BRIDGE_MAX_PENDING; i++)
      if(g_pending[i].active && g_pending[i].request_id == request_id)
         return i;
   return -1;
}

int FindPendingByMtRequestId(const uint request_id)
{
   for(int i = 0; i < BRIDGE_MAX_PENDING; i++)
      if(g_pending[i].active && g_pending[i].mt_request_id == request_id)
         return i;
   return -1;
}

int FindPendingByOrder(const ulong order)
{
   for(int i = 0; i < BRIDGE_MAX_PENDING; i++)
      if(g_pending[i].active && g_pending[i].order_ticket == order)
         return i;
   return -1;
}

void PublishBridgeState(const string message_type)
{
   if(!g_shm.IsReady()) return;
   ulong now = GetTickCount64();
   BridgeManualUiProbe probe;
   string manual_ui_code = EvaluateManualUiReadiness(ChartID(), probe);
   bool manual_ui_ready = (manual_ui_code == "ready");
   string chart_symbol = SanitizeDetail(ChartSymbol(ChartID()));
   string coordinate_source = probe.coordinates_measured
      ? "measured_child_window"
      : (probe.panel_found ? "scaled_chart_property" : "unavailable");
   if(InpDebugLog && manual_ui_code != g_lastManualUiCode)
     {
      PrintFormat(
         "[MANUAL_UI][HEALTH] ready=%s code=%s symbol=%s chartHwnd=%I64d " +
         "panelFound=%s dpi=%d source=%s",
         manual_ui_ready ? "true" : "false", manual_ui_code,
         chart_symbol, probe.chart_hwnd,
         probe.panel_found ? "true" : "false", probe.dpi,
         coordinate_source);
      g_lastManualUiCode = manual_ui_code;
     }
   string message = StringFormat(
      "{\"v\":1,\"type\":\"%s\",\"account\":%I64d,\"generation\":%I64u,\"ea_ms\":%I64u,\"writer_uid\":%u,\"lane\":%d," +
      "\"manual_ui_ready\":%s,\"manual_ui_code\":\"%s\",\"chart_symbol\":\"%s\"," +
      "\"chart_hwnd\":%I64d,\"panel_found\":%s,\"dpi\":%d,\"coordinate_source\":\"%s\"}",
      message_type, g_accountLogin, g_generation, now,
      g_shm.WriterUid(), g_shm.MyLane(),
      manual_ui_ready ? "true" : "false", manual_ui_code, chart_symbol,
      probe.chart_hwnd, probe.panel_found ? "true" : "false", probe.dpi,
      coordinate_source);
   if(g_shm.Send(message)) g_lastHealthPublish = now;
}

bool IsSafeIdentifier(const string value)
{
   int length = StringLen(value);
   if(length < 1 || length > 64) return false;
   for(int i = 0; i < length; i++)
     {
      ushort ch = StringGetCharacter(value, i);
      bool safe = (ch >= 'a' && ch <= 'z') ||
                  (ch >= 'A' && ch <= 'Z') ||
                  (ch >= '0' && ch <= '9') ||
                  ch == '-' || ch == '_' || ch == ':';
      if(!safe) return false;
     }
   return true;
}

string SanitizeDetail(string value)
{
   StringReplace(value, "\\", "/");
   StringReplace(value, "\"", "'");
   StringReplace(value, "\r", " ");
   StringReplace(value, "\n", " ");
   if(StringLen(value) > 160) value = StringSubstr(value, 0, 160);
   return value;
}

string ExtractJsonString(const string json, const string key)
{
   string search = "\"" + key + "\":";
   int start = StringFind(json, search);
   if(start < 0) return "";
   start += StringLen(search);

   while(start < StringLen(json))
     {
      ushort ch = StringGetCharacter(json, start);
      if(ch != ' ' && ch != '\t' && ch != '\r' && ch != '\n') break;
      start++;
     }
   if(start >= StringLen(json)) return "";

   if(StringGetCharacter(json, start) == '"')
     {
      start++;
      int end_quote = StringFind(json, "\"", start);
      if(end_quote < 0) return "";
      return StringSubstr(json, start, end_quote - start);
     }

   int comma = StringFind(json, ",", start);
   int brace = StringFind(json, "}", start);
   int end = (comma >= 0 && (brace < 0 || comma < brace)) ? comma : brace;
   if(end < 0) end = StringLen(json);
   string value = StringSubstr(json, start, end - start);
   StringTrimLeft(value);
   StringTrimRight(value);
   return value;
}

long FindChartBySymbol(const string symbol)
{
   long chart_id = ChartFirst();
   while(chart_id >= 0)
     {
      if(ChartSymbol(chart_id) == symbol) return chart_id;
      chart_id = ChartNext(chart_id);
     }
   return 0;
}

int ScaleManualUiOffset(const int value, const int dpi)
{
   if(!InpAutoDpiScale || dpi <= 0) return value;
   return (int)MathRound((double)value * (double)dpi / 96.0);
}

bool ProbeManualUi(const long chart_id, BridgeManualUiProbe &probe)
{
   ZeroMemory(probe);
   probe.chart_id = chart_id;
   probe.dpi = (int)TerminalInfoInteger(TERMINAL_SCREEN_DPI);
   probe.chart_hwnd = ChartGetInteger(chart_id, CHART_WINDOW_HANDLE);
   if(probe.chart_hwnd == 0 || !IsWindow(probe.chart_hwnd)) return false;
   probe.chart_width = (int)ChartGetInteger(chart_id, CHART_WIDTH_IN_PIXELS);
   probe.chart_height = (int)ChartGetInteger(chart_id, CHART_HEIGHT_IN_PIXELS);

   probe.panel_hwnd = FindWindowExW(probe.chart_hwnd, 0, "#32770", NULL);
   if(probe.panel_hwnd != 0 && IsWindow(probe.panel_hwnd))
     {
      BridgeWindowRect rect;
      if(GetWindowRect(probe.panel_hwnd, rect))
        {
         int point[2];
         point[0] = rect.left;
         point[1] = rect.top;
         if(ScreenToClient(probe.chart_hwnd, point))
           {
            probe.panel_width = rect.right - rect.left;
            probe.panel_height = rect.bottom - rect.top;
            int panel_left = (point[0] > 0) ? point[0] : 0;
            int panel_top = (point[1] > 0) ? point[1] : 0;
            probe.sell_x = panel_left + (int)MathRound(probe.panel_width * 0.15);
            probe.buy_x = panel_left + (int)MathRound(probe.panel_width * 0.82);
            probe.sell_y = panel_top + (int)MathRound(probe.panel_height * 0.65);
            probe.buy_y = probe.sell_y;
            probe.panel_found = true;
            probe.coordinates_measured = true;
            return true;
           }
        }
     }

   // Diagnostic fallback only. No trading action uses these coordinates in
   // phase 1; later phases must fail closed unless readiness is verified.
   probe.buy_x = ScaleManualUiOffset(InpBuyOffsetX, probe.dpi);
   probe.buy_y = ScaleManualUiOffset(InpBuyOffsetY, probe.dpi);
   probe.sell_x = ScaleManualUiOffset(InpSellOffsetX, probe.dpi);
   probe.sell_y = ScaleManualUiOffset(InpSellOffsetY, probe.dpi);
   probe.panel_found = (bool)ChartGetInteger(chart_id, CHART_SHOW_ONE_CLICK);

   bool coordinates_inside_chart =
      probe.buy_x >= 0 && probe.buy_y >= 0 &&
      probe.sell_x >= 0 && probe.sell_y >= 0 &&
      probe.buy_x < probe.chart_width && probe.sell_x < probe.chart_width &&
      probe.buy_y < probe.chart_height && probe.sell_y < probe.chart_height;
   return probe.panel_found && coordinates_inside_chart;
}

string EvaluateManualUiReadiness(const long chart_id, BridgeManualUiProbe &probe)
{
   bool geometry_ready = ProbeManualUi(chart_id, probe);
   if(!TerminalInfoInteger(TERMINAL_TRADE_ALLOWED) ||
      !AccountInfoInteger(ACCOUNT_TRADE_ALLOWED))
      return "trading_not_allowed";
   if(!MQLInfoInteger(MQL_TRADE_ALLOWED))
      return "algo_trading_not_allowed";
   if(!MQLInfoInteger(MQL_DLLS_ALLOWED))
      return "dll_not_allowed";
   if(probe.chart_hwnd == 0 || !IsWindow(probe.chart_hwnd))
      return "chart_invalid";
   if(!probe.panel_found)
      return "oct_panel_not_found";
   if(!geometry_ready)
      return "coordinates_outside_chart";
   return "ready";
}

void LogManualUiProbe(const long chart_id)
{
   BridgeManualUiProbe probe;
   bool chart_ready = ProbeManualUi(chart_id, probe);
   PrintFormat(
      "[MANUAL_UI][PROBE] chart=%I64d symbol=%s chartHwnd=%I64d valid=%s " +
      "panelFound=%s panelHwnd=%I64d panel=%dx%d chart=%dx%d dpi=%d " +
      "buy=(%d,%d) sell=(%d,%d) source=%s",
      chart_id, ChartSymbol(chart_id), probe.chart_hwnd,
      chart_ready ? "true" : "false",
      probe.panel_found ? "true" : "false", probe.panel_hwnd,
      probe.panel_width, probe.panel_height,
      probe.chart_width, probe.chart_height, probe.dpi,
      probe.buy_x, probe.buy_y, probe.sell_x, probe.sell_y,
      probe.coordinates_measured ? "measured_child_window" :
         (probe.panel_found ? "scaled_chart_property" : "fallback_diagnostic"));
}

int ResolveVolumeDigits(const double step)
{
   for(int digits = 0; digits <= 8; digits++)
      if(MathAbs(step - NormalizeDouble(step, digits)) < 0.000000001)
         return digits;
   return 8;
}

string ValidateManualUiVolume(const string symbol, const double volume)
{
   if(StringLen(symbol) == 0 || !SymbolSelect(symbol, true))
      return "invalid_symbol";
   double min_volume = SymbolInfoDouble(symbol, SYMBOL_VOLUME_MIN);
   double max_volume = SymbolInfoDouble(symbol, SYMBOL_VOLUME_MAX);
   double step = SymbolInfoDouble(symbol, SYMBOL_VOLUME_STEP);
   if(volume <= 0 || step <= 0 || volume < min_volume || volume > max_volume)
      return "invalid_volume";
   double steps = MathRound((volume - min_volume) / step);
   double normalized = min_volume + steps * step;
   if(MathAbs(normalized - volume) > step * 0.000001)
      return "invalid_volume_step";
   return "";
}

void PostManualUiKey(const long hwnd, const uint message, const uint key)
{
   PostMessageW(hwnd, message, key, 0);
}

void SendPhysicalKey(const uchar virtual_key)
{
   keybd_event(virtual_key, 0, 0, 0);
   keybd_event(virtual_key, 0, KEYEVENTF_KEYUP, 0);
}

void SendPhysicalControlKey(const uchar virtual_key)
{
   keybd_event(VK_CONTROL, 0, 0, 0);
   SendPhysicalKey(virtual_key);
   keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
}

bool SendPhysicalText(const string value)
{
   for(int index = 0; index < StringLen(value); index++)
     {
      short mapped = VkKeyScanW(StringGetCharacter(value, index));
      if(mapped == -1) return false;
      uchar virtual_key = (uchar)(mapped & 0xFF);
      uchar modifiers = (uchar)((mapped >> 8) & 0xFF);
      if((modifiers & 1) != 0) keybd_event(0x10, 0, 0, 0); // SHIFT
      SendPhysicalKey(virtual_key);
      if((modifiers & 1) != 0) keybd_event(0x10, 0, KEYEVENTF_KEYUP, 0);
     }
   return true;
}

bool ReadClipboardUnicodeText(string &value)
{
   value = "";
   bool opened = false;
   for(int attempt = 0; attempt < 5 && !opened; attempt++)
     {
      opened = OpenClipboard(0);
      if(!opened) Sleep(20);
     }
   if(!opened) return false;

   bool success = false;
   long memory_handle = GetClipboardData(CF_UNICODETEXT);
   if(memory_handle != 0)
     {
      ulong pointer = GlobalLock(memory_handle);
      if(pointer != 0)
        {
         int length = lstrlenW(pointer);
         if(length >= 0 && length <= 64)
           {
            uchar bytes[];
            ArrayResize(bytes, length * 2);
            if(length > 0)
               kernel32::RtlMoveMemory(bytes, pointer, length * 2);
            for(int index = 0; index < length; index++)
              {
               ushort character =
                  (ushort)((uint)bytes[index * 2] |
                           ((uint)bytes[index * 2 + 1] << 8));
               value += ShortToString(character);
              }
            success = true;
           }
         GlobalUnlock(memory_handle);
        }
     }
   CloseClipboard();
   return success;
}

bool ClearClipboardText()
{
   bool opened = false;
   for(int attempt = 0; attempt < 5 && !opened; attempt++)
     {
      opened = OpenClipboard(0);
      if(!opened) Sleep(20);
     }
   if(!opened) return false;
   bool cleared = EmptyClipboard();
   CloseClipboard();
   return cleared;
}

bool AcquireManualUiFocus(const long chart_hwnd,
                          long &root_hwnd,
                          long &input_hwnd,
                          ulong &focus_elapsed_ms)
{
   root_hwnd = GetAncestor(chart_hwnd, GA_ROOT);
   input_hwnd = 0;
   ulong started_ms = GetTickCount64();
   if(root_hwnd == 0 || !IsWindow(root_hwnd))
     {
      focus_elapsed_ms = GetTickCount64() - started_ms;
      return false;
     }

   for(int attempt = 0; attempt < 5; attempt++)
     {
      ShowWindowAsync(root_hwnd, 9); // SW_RESTORE
      BringWindowToTop(root_hwnd);
      SetForegroundWindow(root_hwnd);
      SetFocus(chart_hwnd);
      Sleep(40);

      long foreground_hwnd = GetForegroundWindow();
      input_hwnd = GetFocus();
      long input_root = (input_hwnd != 0)
         ? GetAncestor(input_hwnd, GA_ROOT) : 0;
      if(foreground_hwnd == root_hwnd &&
         (input_hwnd == chart_hwnd || input_root == root_hwnd))
        {
         focus_elapsed_ms = GetTickCount64() - started_ms;
         return true;
        }
      Sleep(20);
     }

   focus_elapsed_ms = GetTickCount64() - started_ms;
   return false;
}

bool IsManualUiFocusOwned(const long chart_hwnd)
{
   long root_hwnd = GetAncestor(chart_hwnd, GA_ROOT);
   if(root_hwnd == 0 || GetForegroundWindow() != root_hwnd) return false;
   long input_hwnd = GetFocus();
   if(input_hwnd == 0) return false;
   return input_hwnd == chart_hwnd ||
      GetAncestor(input_hwnd, GA_ROOT) == root_hwnd;
}

bool ReadBackManualUiVolume(const long chart_hwnd,
                            const long mouse_param,
                            const double expected_volume,
                            const double volume_step,
                            string &actual_text)
{
   actual_text = "";
   PostMessageW(chart_hwnd, WM_MOUSEMOVE, 0, mouse_param);
   PostMessageW(chart_hwnd, WM_LBUTTONDOWN, MK_LBUTTON, mouse_param);
   Sleep(40);
   PostMessageW(chart_hwnd, WM_LBUTTONUP, 0, mouse_param);
   Sleep(60);
   if(!IsManualUiFocusOwned(chart_hwnd)) return false;
   SendPhysicalControlKey(VK_A);
   if(!ClearClipboardText())
     {
      SendPhysicalKey(VK_ESCAPE);
      return false;
     }
   SendPhysicalControlKey(VK_C);
   Sleep(60);
   bool copied = ReadClipboardUnicodeText(actual_text);
   SendPhysicalKey(VK_ESCAPE);
   if(!copied) return false;

   StringTrimLeft(actual_text);
   StringTrimRight(actual_text);
   string normalized_text = actual_text;
   StringReplace(normalized_text, ",", ".");
   double actual_volume = StringToDouble(normalized_text);
   double tolerance = MathMax(0.000000001, volume_step * 0.000001);
   return actual_volume > 0 &&
          MathAbs(actual_volume - expected_volume) <= tolerance;
}

bool ProbeSetManualUiVolume(const long chart_id, const double requested_volume)
{
   ulong volume_probe_started_ms = GetTickCount64();
   BridgeManualUiProbe probe;
   string readiness = EvaluateManualUiReadiness(chart_id, probe);
   if(readiness != "ready")
     {
      PrintFormat("[MANUAL_UI][VOLUME_PROBE] success=false code=%s", readiness);
      return false;
     }

   string symbol = ChartSymbol(chart_id);
   string validation = ValidateManualUiVolume(symbol, requested_volume);
   if(StringLen(validation) > 0)
     {
      PrintFormat(
         "[MANUAL_UI][VOLUME_PROBE] success=false code=%s symbol=%s requested=%.8f",
         validation, symbol, requested_volume);
      return false;
     }

   int volume_x = ScaleManualUiOffset(InpVolumeOffsetX, probe.dpi);
   int volume_y = ScaleManualUiOffset(InpVolumeOffsetY, probe.dpi);
   if(volume_x < 0 || volume_y < 0 ||
      volume_x >= probe.chart_width || volume_y >= probe.chart_height)
     {
      Print("[MANUAL_UI][VOLUME_PROBE] success=false code=coordinates_outside_chart");
      return false;
     }

   long mouse_param = (volume_y << 16) | (volume_x & 0xFFFF);
   long root_hwnd;
   long input_hwnd;
   ulong focus_elapsed_ms;
   bool focus_verified = AcquireManualUiFocus(
      probe.chart_hwnd, root_hwnd, input_hwnd, focus_elapsed_ms);
   if(!focus_verified)
     {
      PrintFormat(
         "[MANUAL_UI][VOLUME_PROBE] success=false code=terminal_focus_not_acquired " +
         "symbol=%s requested=%.8f rootHwnd=%I64d inputHwnd=%I64d " +
         "foreground=false focusVerified=false focusAcquireMs=%I64u",
         symbol, requested_volume, root_hwnd, input_hwnd,
         focus_elapsed_ms);
      return false;
     }

   PostMessageW(probe.chart_hwnd, WM_MOUSEMOVE, 0, mouse_param);
   PostMessageW(probe.chart_hwnd, WM_LBUTTONDOWN, MK_LBUTTON, mouse_param);
   Sleep(40);
   PostMessageW(probe.chart_hwnd, WM_LBUTTONUP, 0, mouse_param);
   Sleep(40);

   double step = SymbolInfoDouble(symbol, SYMBOL_VOLUME_STEP);
   int digits = ResolveVolumeDigits(step);
   string volume_text = DoubleToString(requested_volume, digits);

   double cache_tolerance = MathMax(0.000000001, step * 0.000001);
   bool cache_candidate = g_volumeCacheValid &&
      g_volumeCacheSymbol == symbol &&
      MathAbs(g_volumeCacheValue - requested_volume) <= cache_tolerance;
   if(cache_candidate)
     {
      string cached_actual_text;
      bool cache_verified = ReadBackManualUiVolume(
         probe.chart_hwnd, mouse_param, requested_volume, step,
         cached_actual_text);
      if(cache_verified)
        {
         PrintFormat(
            "[MANUAL_UI][VOLUME_PROBE] success=true symbol=%s requested=%s " +
            "actual=%s verified=true fastPath=true at=(%d,%d) " +
            "rootHwnd=%I64d inputHwnd=%I64d foreground=true " +
            "focusVerified=true focusAcquireMs=%I64u volumeVerifyMs=%I64u " +
            "textSent=false",
            symbol, volume_text, cached_actual_text,
            volume_x, volume_y, root_hwnd, input_hwnd,
            focus_elapsed_ms, GetTickCount64() - volume_probe_started_ms);
         return true;
        }
      g_volumeCacheValid = false;
     }

   // The MT5 OCT volume field is custom-drawn on several broker builds and
   // does not expose a child Edit HWND. Physical key state is therefore
   // required; PostMessage(Ctrl+A) does not update GetKeyState in MT5.
   if(!IsManualUiFocusOwned(probe.chart_hwnd))
     {
      g_volumeCacheValid = false;
      PrintFormat(
         "[MANUAL_UI][VOLUME_PROBE] success=false code=terminal_focus_lost " +
         "symbol=%s requested=%s rootHwnd=%I64d inputHwnd=%I64d",
         symbol, volume_text, root_hwnd, GetFocus());
      return false;
     }
   SendPhysicalControlKey(VK_A);
   bool text_result = SendPhysicalText(volume_text);
   SendPhysicalKey(VK_RETURN);
   Sleep(80);

   string actual_text;
   bool verified = text_result &&
      ReadBackManualUiVolume(probe.chart_hwnd, mouse_param,
                             requested_volume, step, actual_text);
   if(verified)
     {
      g_volumeCacheValid = true;
      g_volumeCacheSymbol = symbol;
      g_volumeCacheValue = requested_volume;
     }
   else
     {
      g_volumeCacheValid = false;
     }

   PrintFormat(
      "[MANUAL_UI][VOLUME_PROBE] success=%s symbol=%s requested=%s actual=%s " +
      "verified=%s fastPath=false at=(%d,%d) rootHwnd=%I64d inputHwnd=%I64d " +
      "foreground=true focusVerified=true focusAcquireMs=%I64u " +
      "volumeVerifyMs=%I64u textSent=%s",
      verified ? "true" : "false",
      symbol, volume_text, actual_text, verified ? "true" : "false",
      volume_x, volume_y, root_hwnd, input_hwnd, focus_elapsed_ms,
      GetTickCount64() - volume_probe_started_ms,
      text_result ? "true" : "false");
   return verified;
}

void ResetManualOpenExecution(const int index)
{
   if(index < 0 || index >= BRIDGE_MANUAL_PENDING) return;
   g_manualOpen[index].active = false;
   g_manualOpen[index].click_dispatched = false;
   g_manualOpen[index].request_id = "";
   g_manualOpen[index].symbol = "";
   g_manualOpen[index].side = "";
   g_manualOpen[index].volume = 0;
   g_manualOpen[index].chart_id = 0;
   g_manualOpen[index].chart_hwnd = 0;
   g_manualOpen[index].click_x = 0;
   g_manualOpen[index].click_y = 0;
   g_manualOpen[index].baseline_count = 0;
   g_manualOpen[index].baseline_volume = 0;
   g_manualOpen[index].baseline_hash = 0;
   g_manualOpen[index].prepared_ms = 0;
   g_manualOpen[index].dispatched_ms = 0;
   g_manualOpen[index].dispatched_server_time = 0;
}

int FindManualOpenByRequestId(const string request_id)
{
   for(int index = 0; index < BRIDGE_MANUAL_PENDING; index++)
      if(g_manualOpen[index].active &&
         g_manualOpen[index].request_id == request_id)
         return index;
   return -1;
}

void SweepManualOpenTimeouts(const ulong now)
{
   ulong timeout_ms = (ulong)MathMax(1000, InpPendingTimeoutMs);
   for(int index = 0; index < BRIDGE_MANUAL_PENDING; index++)
     {
      if(!g_manualOpen[index].active) continue;
      ulong started = g_manualOpen[index].click_dispatched
         ? g_manualOpen[index].dispatched_ms
         : g_manualOpen[index].prepared_ms;
      if(started == 0 || now < started || now - started < timeout_ms) continue;
      if(InpDebugLog)
         PrintFormat(
            "[MANUAL_UI][OPEN_CANDIDATE] request=%s status=timeout " +
            "clickDispatched=%s elapsedMs=%I64u",
            g_manualOpen[index].request_id,
            g_manualOpen[index].click_dispatched ? "true" : "false",
            now - started);
      CacheResult(g_manualOpen[index].request_id, "timeout", 0, 0, 0,
                  g_manualOpen[index].volume, 0,
                  "manual_trade_confirmation_timeout");
      SendExecutionResult(g_manualOpen[index].request_id, "timeout", 0, 0, 0,
                          g_manualOpen[index].volume, 0,
                          "manual_trade_confirmation_timeout");
      ResetManualOpenExecution(index);
     }
}

int AllocateManualOpenExecution()
{
   // UI actions on one terminal must never overlap. Capacity is retained for
   // future completed-result correlation, but only one active action is
   // allowed during dispatch.
   for(int index = 0; index < BRIDGE_MANUAL_PENDING; index++)
      if(g_manualOpen[index].active) return -1;
   for(int index = 0; index < BRIDGE_MANUAL_PENDING; index++)
     {
      ResetManualOpenExecution(index);
      return index;
     }
   return -1;
}

void CaptureManualOpenBaseline(const string symbol,
                               int &position_count,
                               double &total_volume,
                               ulong &ticket_hash)
{
   position_count = 0;
   total_volume = 0;
   ticket_hash = 1469598103934665603;
   int total = PositionsTotal();
   for(int position_index = 0; position_index < total; position_index++)
     {
      ulong ticket = PositionGetTicket(position_index);
      if(ticket == 0 || PositionGetString(POSITION_SYMBOL) != symbol) continue;
      position_count++;
      total_volume += PositionGetDouble(POSITION_VOLUME);
      ticket_hash ^= ticket;
      ticket_hash *= 1099511628211;
     }
}

string PrepareManualOpenExecution(const string request_id,
                                  const long account,
                                  const string symbol,
                                  const string side,
                                  const double volume,
                                  const ulong expires_ms,
                                  int &execution_index)
{
   execution_index = -1;
   if(!IsSafeIdentifier(request_id)) return "invalid_request_id";
   if(FindManualOpenByRequestId(request_id) >= 0) return "duplicate_request";

   string validation = ValidateOpen(account, symbol, side, volume, expires_ms);
   if(StringLen(validation) > 0) return validation;

   long chart_id = FindChartBySymbol(symbol);
   if(chart_id == 0) return "chart_not_found";
   BridgeManualUiProbe probe;
   string readiness = EvaluateManualUiReadiness(chart_id, probe);
   if(readiness != "ready") return readiness;

   int index = AllocateManualOpenExecution();
   if(index < 0) return "manual_ui_busy";

   g_manualOpen[index].active = true;
   g_manualOpen[index].request_id = request_id;
   g_manualOpen[index].symbol = symbol;
   g_manualOpen[index].side = side;
   g_manualOpen[index].volume = volume;
   g_manualOpen[index].chart_id = chart_id;
   g_manualOpen[index].chart_hwnd = probe.chart_hwnd;
   g_manualOpen[index].click_x = (side == "BUY") ? probe.buy_x : probe.sell_x;
   g_manualOpen[index].click_y = (side == "BUY") ? probe.buy_y : probe.sell_y;
   g_manualOpen[index].prepared_ms = GetTickCount64();
   CaptureManualOpenBaseline(
      symbol,
      g_manualOpen[index].baseline_count,
      g_manualOpen[index].baseline_volume,
      g_manualOpen[index].baseline_hash);
   execution_index = index;
   return "";
}

string DispatchPreparedManualOpen(const int index)
{
   if(index < 0 || index >= BRIDGE_MANUAL_PENDING ||
      !g_manualOpen[index].active)
      return "manual_request_not_found";
   if(g_manualOpen[index].click_dispatched)
      return "manual_click_already_dispatched";

   BridgeManualUiProbe probe;
   string readiness =
      EvaluateManualUiReadiness(g_manualOpen[index].chart_id, probe);
   if(readiness != "ready") return readiness;
   if(probe.chart_hwnd != g_manualOpen[index].chart_hwnd)
      return "chart_hwnd_changed";

   if(!ProbeSetManualUiVolume(g_manualOpen[index].chart_id,
                              g_manualOpen[index].volume))
      return "oct_volume_not_verified";

   // Volume verification may take focus; resolve geometry again immediately
   // before the single allowed BUY/SELL click.
   readiness = EvaluateManualUiReadiness(g_manualOpen[index].chart_id, probe);
   if(readiness != "ready") return readiness;
   int click_x = (g_manualOpen[index].side == "BUY")
      ? probe.buy_x : probe.sell_x;
   int click_y = (g_manualOpen[index].side == "BUY")
      ? probe.buy_y : probe.sell_y;
   if(click_x < 0 || click_y < 0 ||
      click_x >= probe.chart_width || click_y >= probe.chart_height)
      return "manual_click_outside_chart";

   long mouse_param = (click_y << 16) | (click_x & 0xFFFF);
   // Arm confirmation before dispatching the click. A fast broker can emit
   // OnTradeTransaction before PostMessage returns to this function.
   g_manualOpen[index].click_x = click_x;
   g_manualOpen[index].click_y = click_y;
   g_manualOpen[index].click_dispatched = true;
   g_manualOpen[index].dispatched_ms = GetTickCount64();
   g_manualOpen[index].dispatched_server_time = TimeCurrent();

   bool moved = PostMessageW(probe.chart_hwnd, WM_MOUSEMOVE, 0, mouse_param);
   bool pressed = PostMessageW(probe.chart_hwnd, WM_LBUTTONDOWN,
                               MK_LBUTTON, mouse_param);
   Sleep(40);
   bool released = PostMessageW(probe.chart_hwnd, WM_LBUTTONUP, 0, mouse_param);
   if(!moved || !pressed || !released)
      return "manual_click_dispatch_failed";

   if(InpDebugLog)
      PrintFormat(
         "[MANUAL_UI][OPEN_CANDIDATE] request=%s symbol=%s side=%s volume=%.8f " +
         "baselineCount=%d baselineVolume=%.8f baselineHash=%I64u " +
         "click=(%d,%d) dispatched=true confirmation=pending",
         g_manualOpen[index].request_id, g_manualOpen[index].symbol,
         g_manualOpen[index].side, g_manualOpen[index].volume,
         g_manualOpen[index].baseline_count,
         g_manualOpen[index].baseline_volume,
         g_manualOpen[index].baseline_hash,
         click_x, click_y);
   return "";
}

ulong FindLivePositionTicketByIdentifier(const ulong position_identifier)
{
   if(position_identifier == 0) return 0;
   int total = PositionsTotal();
   for(int index = 0; index < total; index++)
     {
      ulong ticket = PositionGetTicket(index);
      if(ticket == 0) continue;
      ulong identifier =
         (ulong)PositionGetInteger(POSITION_IDENTIFIER);
      if(identifier == position_identifier) return ticket;
     }
   return 0;
}

void FinalizeManualOpen(const int index, const ulong ticket,
                        const ulong deal, const double price,
                        const double volume, const string detail)
{
   if(index < 0 || index >= BRIDGE_MANUAL_PENDING ||
      !g_manualOpen[index].active) return;
   string request_id = g_manualOpen[index].request_id;
   CacheResult(request_id, "confirmed", ticket, deal, price, volume,
               TRADE_RETCODE_DONE, detail);
   SendExecutionResult(request_id, "confirmed", ticket, deal, price, volume,
                       TRADE_RETCODE_DONE, detail);
   if(InpDebugLog)
      PrintFormat(
         "[MANUAL_UI][OPEN_CONFIRMED] request=%s ticket=%I64u deal=%I64u " +
         "price=%.10f volume=%.8f reason=client",
         request_id, ticket, deal, price, volume);
   ResetManualOpenExecution(index);
}

bool TryConfirmManualOpenFromDeal(const ulong deal)
{
   if(deal == 0 || !HistoryDealSelect(deal)) return false;
   ENUM_DEAL_REASON reason =
      (ENUM_DEAL_REASON)HistoryDealGetInteger(deal, DEAL_REASON);
   if(reason != DEAL_REASON_CLIENT) return false;

   string symbol = HistoryDealGetString(deal, DEAL_SYMBOL);
   ENUM_DEAL_TYPE deal_type =
      (ENUM_DEAL_TYPE)HistoryDealGetInteger(deal, DEAL_TYPE);
   ENUM_DEAL_ENTRY entry =
      (ENUM_DEAL_ENTRY)HistoryDealGetInteger(deal, DEAL_ENTRY);
   if(entry != DEAL_ENTRY_IN && entry != DEAL_ENTRY_INOUT) return false;
   double volume = HistoryDealGetDouble(deal, DEAL_VOLUME);
   double price = HistoryDealGetDouble(deal, DEAL_PRICE);
   datetime deal_time =
      (datetime)HistoryDealGetInteger(deal, DEAL_TIME);
   ulong position_identifier =
      (ulong)HistoryDealGetInteger(deal, DEAL_POSITION_ID);

   for(int index = 0; index < BRIDGE_MANUAL_PENDING; index++)
     {
      if(!g_manualOpen[index].active ||
         !g_manualOpen[index].click_dispatched ||
         g_manualOpen[index].symbol != symbol)
         continue;
      // Do not let an older/manual deal with the same shape confirm this
      // request. A small allowance covers broker/server second rounding.
      if(g_manualOpen[index].dispatched_server_time <= 0 ||
         deal_time + 2 < g_manualOpen[index].dispatched_server_time)
         continue;
      bool side_matches =
         (g_manualOpen[index].side == "BUY" && deal_type == DEAL_TYPE_BUY) ||
         (g_manualOpen[index].side == "SELL" && deal_type == DEAL_TYPE_SELL);
      if(!side_matches) continue;
      double step = SymbolInfoDouble(symbol, SYMBOL_VOLUME_STEP);
      double tolerance = MathMax(0.000000001, step * 0.000001);
      if(MathAbs(volume - g_manualOpen[index].volume) > tolerance) continue;

      ulong ticket = FindLivePositionTicketByIdentifier(position_identifier);
      if(ticket == 0) ticket = position_identifier;
      if(ticket == 0) continue;
      FinalizeManualOpen(index, ticket, deal, price, volume,
                         "manual_client_deal_confirmed");
      return true;
     }
   return false;
}

void ReconcileManualOpenDeals()
{
   bool has_candidate = false;
   for(int index = 0; index < BRIDGE_MANUAL_PENDING; index++)
      if(g_manualOpen[index].active &&
         g_manualOpen[index].click_dispatched)
        {
         has_candidate = true;
         break;
        }
   if(!has_candidate) return;

   datetime now = TimeCurrent();
   if(!HistorySelect(now - 60, now + 1)) return;
   int total = HistoryDealsTotal();
   if(total <= 0) return;

   // HistoryDealSelect changes the selected list on some MT5 builds, so
   // snapshot tickets before matching them.
   ulong deals[];
   ArrayResize(deals, total);
   for(int deal_index = 0; deal_index < total; deal_index++)
      deals[deal_index] = HistoryDealGetTicket(deal_index);
   for(int deal_index = total - 1; deal_index >= 0; deal_index--)
      if(TryConfirmManualOpenFromDeal(deals[deal_index])) return;
}
//+------------------------------------------------------------------+
