//+------------------------------------------------------------------+
//| OCTBridge.mq5                                                     |
//| MT5 execution bridge using SharedMem IPC                          |
//+------------------------------------------------------------------+
#property copyright "Copyright 2024"
#property link      ""
#property version   "1.20"
#property strict

#include "SharedMem.mqh"

input string InpRoomId              = "AUTO";
input bool   InpDebugLog            = false;
input ulong  InpMagicNumber         = 88005001;
input uint   InpMaxDeviationPoints  = 20;
input int    InpPendingTimeoutMs     = 10000;

#define BRIDGE_HEALTH_INTERVAL_MS 500
#define BRIDGE_MAX_PENDING        32
#define BRIDGE_DEDUP_CAPACITY     128

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

CSharedMemRing   g_shm;
string           g_roomId;
ulong            g_generation = 0;
ulong            g_lastHealthPublish = 0;
long             g_accountLogin = 0;
BridgePendingExecution g_pending[BRIDGE_MAX_PENDING];
BridgeCachedResult g_dedup[BRIDGE_DEDUP_CAPACITY];
int              g_dedupNext = 0;

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

   SweepPendingTimeouts(now);
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

   if(FindPendingByRequestId(request_id) >= 0)
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

   string error = ValidateOpen(account, symbol, side, volume, expires_ms);
   if(StringLen(error) > 0)
     {
      string status = (error == "request_expired") ? "expired" : "invalid";
      FinalizeImmediate(request_id, status, 0, 0, 0, volume, 0, error);
      return;
     }

   int pending_index = AllocatePending();
   if(pending_index < 0)
     {
      FinalizeImmediate(request_id, "rejected", 0, 0, 0, volume, 0,
                        "pending_queue_full");
      return;
     }

   SendExecutionResult(request_id, "received", 0, 0, 0, volume, 0, "accepted");

   MqlTradeRequest request;
   MqlTradeResult result;
   MqlTradeCheckResult check;
   ZeroMemory(request);
   ZeroMemory(result);
   ZeroMemory(check);

   request.action = TRADE_ACTION_DEAL;
   request.magic = InpMagicNumber;
   request.symbol = symbol;
   request.volume = volume;
   request.deviation = InpMaxDeviationPoints;
   request.type = (side == "BUY") ? ORDER_TYPE_BUY : ORDER_TYPE_SELL;
   request.price = (side == "BUY")
                   ? SymbolInfoDouble(symbol, SYMBOL_ASK)
                   : SymbolInfoDouble(symbol, SYMBOL_BID);
   request.type_time = ORDER_TIME_GTC;
   request.type_filling = ResolveFillingMode(symbol);
   request.comment = "OCTB:" + StringSubstr(request_id, 0, 20);

   if(!OrderCheck(request, check))
     {
      ReleasePending(pending_index);
      FinalizeImmediate(request_id, "rejected", 0, 0, 0, volume,
                        check.retcode, SanitizeDetail(check.comment));
      return;
     }

   g_pending[pending_index].active = true;
   g_pending[pending_index].action = "open";
   g_pending[pending_index].request_id = request_id;
   g_pending[pending_index].symbol = symbol;
   g_pending[pending_index].side = side;
   g_pending[pending_index].volume = volume;
   g_pending[pending_index].position_ticket = 0;
   g_pending[pending_index].dispatched_ms = GetTickCount64();
   g_pending[pending_index].timeout_reported = false;

   if(!OrderSendAsync(request, result))
     {
      ReleasePending(pending_index);
      FinalizeImmediate(request_id, "rejected", 0, 0, 0, volume,
                        result.retcode, SanitizeDetail(result.comment));
      return;
     }

   g_pending[pending_index].mt_request_id = result.request_id;
   g_pending[pending_index].order_ticket = result.order;
   g_pending[pending_index].deal_ticket = result.deal;

   SendExecutionResult(request_id, "dispatched", result.order, result.deal,
                       result.price, volume, result.retcode, "order_send_async");
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
   string message = StringFormat(
      "{\"v\":1,\"type\":\"%s\",\"account\":%I64d,\"generation\":%I64u,\"ea_ms\":%I64u,\"writer_uid\":%u,\"lane\":%d}",
      message_type, g_accountLogin, g_generation, now,
      g_shm.WriterUid(), g_shm.MyLane());
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
//+------------------------------------------------------------------+
