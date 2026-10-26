# TradeLocker — dữ liệu nền (từ source `scan\trade-locker`)

> Nguồn: `C:\Users\laptop\source\tranminhman\scan\trade-locker` (Node.js/TypeScript, không có git, file mới nhất
> 2026-10-08), đặc biệt là `docs/tradelocker-findings.md` (quét 2026-10-07 trên **OPEN_DEMO**, web TradeLocker 3.95.2).
> Rà lại ngày 2026-10-26. Mọi mục ghi "chưa biết" đều được đưa vào [phase-0-spike.md](phase-0-spike.md).
>
> ⚠ Đây là nền tảng **mới** `app.tradelocker.com` (engine **APOLLO**), KHÁC với TradeLocker Public API cũ
> (`live.tradelocker.com/backend-api`, REST có tài liệu). Giao thức dưới đây là **reverse-engineer từ bundle web**,
> không có tài liệu chính thức.

---

## 1. Source có gì

| File | Vai trò | Port sang C# ở |
|---|---|---|
| `src/auth/keycloak.ts` | Lưu/refresh token Keycloak (refresh trước 60 s) | Phase 2 `KeycloakClient` |
| `src/auth/kerber.ts` | Danh sách account, mint account JWT (cache, gia hạn trước 60 s) | Phase 2 `KerberClient` |
| `src/scripts/login.ts` + `src/ui/browser.ts` | Mở Chrome, chờ user đăng nhập, đọc sessionStorage | Phase 2 Chrome + CDP |
| `src/proto/registry.ts` + `descriptors.json` | 268 `FileDescriptorProto` (base64) → encode/decode | Phase 3 .proto subset + C# sinh sẵn |
| `src/hermes/connection.ts` | RSocket over WebSocket (thư viện `rsocket-core` 1.0.0-alpha.3) | Phase 3 frame codec + Phase 4 connection |
| `src/hermes/state.ts` | `SymbolIndex` (symbol ↔ id), `PositionStore` (ghép position + order) | Phase 3 |
| `src/rest/hera.ts` | Báo cáo closed positions / order history (phân trang) | Phase 6 |
| `src/client.ts` | `TradeLockerClient`: start, subscribe quotes, open/close, `waitFor`, reconnect | Phase 4 + 7A |
| `src/ui/clicker.ts` | Chế độ click UI qua Playwright | **Không port** (app dùng đường API) |

## 2. Xác thực (3 tầng)

| Tầng | Endpoint | Token | Hạn |
|---|---|---|---|
| User | Keycloak `https://auth.tradelocker.com/realms/tradelocker/protocol/openid-connect/token`, client `frontend-app`, `grant_type=refresh_token` | access + refresh (role `offline_access`) | access ~30 phút; refresh: **chưa biết** (Q10) |
| Danh sách account | `GET https://api.tradelocker.com/kerber-api/v1/accounts` + header **`X-ODA-Brand: PRDTL`** (thiếu ⇒ 400) | Keycloak access | — |
| Account JWT | `POST https://api.tradelocker.com/kerber-api/v1/auth/accounts/jwt` body `{"accountId":"<accId số>"}` (KHÔNG phải `PRDTL#O#…`) | JWT `aud: apollo`, response có `expireDate` | **5 phút** |

- Account: `id` = `PRDTL#O#<accId>`, `accId` = chuỗi số (~1.79e18), `type`, `engine` (chỉ hỗ trợ `APOLLO`),
  `environmentInfo.cluster` (vd `apl0`) ⇒ host `api.<cluster>.tradelocker.com`.
- Web app giữ token ở sessionStorage key `tlx-oidc:user:https://auth.tradelocker.com/realms/tradelocker:frontend-app`.
- Đăng nhập Google: Chrome do Playwright launch bị Google chặn ⇒ phải mở **Chrome thường** với `--remote-debugging-port`
  rồi `connectOverCDP`. Đây đúng là mô hình `PrimeXbtChromeBrowserService` đang dùng.
- Refresh Keycloak từ ngoài trình duyệt: đã chạy được (login.ts gọi `kc.refresh()` ngay sau khi lưu).

## 3. Realtime: `wss://api.<cluster>.tradelocker.com/hermes`

- **RSocket over WebSocket**, data `application/x-protobuf`, metadata `message/x.rsocket.composite-metadata.v0`.
- SETUP: metadata = bearer auth (`message/x.rsocket.authentication.v0`) chứa **account JWT**; keepAlive 5 s, lifetime 15 s.
  Auth chỉ gửi lúc SETUP. Socket có bị cắt khi JWT 5 phút hết hạn không thì **chưa biết** (Q1).
- Mỗi request mang routing metadata (well-known `0x7E`, route có tiền tố độ dài).
- Kết nối từ Node bằng `ws` + header `Origin: https://app.tradelocker.com`: OK.
- Stream: client xin `REQUEST_N = 100` credit, nhận đủ 100 thì xin thêm 100 (giống web).
- Lỗi: frame ERROR có message = base64(`ProtoError { code, description }`).

### Route

| Route | Kiểu | Message |
|---|---|---|
| `v3.tickers.{tickerId}.quotes` | stream | `ProtoQuote` |
| `v3.position-aggregates` | stream | `ProtoPositionAggregateEvent` |
| `v3.tradable-unit-aggregates` | stream | instruments / tickers / tradable units |
| `v3.account-statements` | stream | balance, pnl, margin |
| `v3.notifications` | stream | `ProtoNotification` (filled / rejected / SL-TP…) |
| `v3.positions:open` | request-response | `ProtoPositionOpenRequest` → `ProtoCommandAck` |
| `v3.positions.{positionId}:close` | request-response | payload rỗng → `ProtoCommandAck` |
| `v3.positions:close-all` | request-response | **CẤM dùng trong app** (grep A6) |

### Schema đã decode (package `com.tradelocker.protohermes`, giải mã từ `descriptors.json` ngày 2026-10-25)

```proto
message ProtoQuote { string ask = 1; string bid = 2; }                  // KHÔNG có timestamp sàn

message ProtoPositionOpenRequest { ProtoSide side = 1; string tradable_unit_id = 2; repeated ProtoOrderRequest orders = 3; }
message ProtoOrderRequest { oneof { ProtoMarketOrderRequest market_order = 1; /* limit=2, stop=3, stop_limit=4, trailing=5, mit=6 */ } }
message ProtoMarketOrderRequest {
  ProtoSide side = 1; string quantity = 2; ProtoTimeInForce time_in_force = 3;
  optional string time_in_force_gtd_timestamp = 4; ProtoOrderIntent intent = 5;
}                                                                        // KHÔNG có client order id ⇒ không idempotent
message ProtoCommandAck { string correlation_id = 1; }

message ProtoPosition {
  string id = 1; string account_id = 2; string tradable_unit_id = 3; ProtoSide side = 4;
  ProtoPositionStatus status = 5; string created_at = 6; string total_commission = 7; string total_swap = 8;
}
message ProtoMarketOrder {
  ProtoSide side = 1; string requested_quantity = 2; ProtoTimeInForce time_in_force = 3; string position_id = 5;
  ProtoOrderStatus status = 6; string last_correlation_id = 7; string filled_quantity = 8; string average_price = 9;
  string created_at = 11; string modified_at = 12; string id = 13; ProtoOrderIntent intent = 15;
}
message ProtoPositionAggregateEvent { oneof { ProtoPositionsAggregate positions_aggregate = 1; ProtoPositionsDeleted positions_deleted = 2; } }
message ProtoPositionsDeleted { repeated string ids = 1; }

message ProtoTicker { string id = 1; string name = 2; string tick_size = 8; string lot_size = 9; string lot_step = 10;
                      string minimum_lot = 11; string maximum_lot = 12; /* ... */ }
message ProtoInstrument { string id = 1; string code = 2; string name = 3; ProtoTradingStatus trading_status = 6; string ticker_id = 7; /* ... */ }
message ProtoTradableUnit { string id = 1; string instrument_id = 4; /* plan ids ... */ }

message ProtoNotification { string correlation_id = 1; string title = 5; string body = 6;
  oneof { order_filled = 2; order_rejected = 3; position_opened_market_order_filled = 7;
          position_closed_market_order_filled = 10; stop_out_executed = 41; /* ... 47 loại */ } }

enum ProtoSide { SIDE_UNSPECIFIED = 0; SIDE_BUY = 1; SIDE_SELL = 2; }
enum ProtoTimeInForce { UNSPECIFIED = 0; GTC = 1; IOC = 2; FOK = 3; DAY = 4; GTD = 5; }
enum ProtoOrderIntent { UNSPECIFIED = 0; INCREASE = 1; DECREASE = 2; }
// PositionStatus: 1 NEW, 2 OPEN, 3 CLOSING, 4 CLOSED, 5 TERMINATED, 6 ABORTING, 7 ABORTED
// OrderStatus 8..16 = filled/canceled/rejected/expired/terminated (không còn "live")
```

Ghi chú khi port `PositionStore.view()`:
- qty ròng = Σ filled(INCREASE) − Σ filled(DECREASE).
- Giá vào = VWAP của các order INCREASE.
- SL/TP lấy từ order DECREASE còn live: stop ⇒ SL, limit ⇒ TP.
- Hiện **mọi giá/số lượng trên dây là `string`** ⇒ parse bằng `decimal` invariant culture.

## 4. Đã kiểm chứng trên demo (2026-10-07, SPX500 0.01 lot)

- **Frame BUY**: `REQUEST_RESPONSE|M` (`0x11 0x00`), route `v3.positions:open`; body side=BUY, tradable_unit_id,
  market_order{side=BUY, quantity="0.01", IOC, INCREASE}.
- Sau gửi lệnh:
  - ~450 ms: position NEW
  - ~725 ms: OPEN, `average_price` có giá trị
  - ~780 ms: notification "Position opened"
- **Close**: route `v3.positions.<id>:close`, body rỗng.
  - ~440 ms: order DECREASE, status CLOSING
  - ~690 ms: `positions_deleted { ids:[<id>] }`
- `:open` / `:close` trả `ProtoCommandAck { correlation_id }`, nhưng order trên `v3.position-aggregates` luôn có
  `last_correlation_id = "-1"` ⇒ **không ghép được theo correlation**.
  - Khớp Open bằng: position MỚI (chưa thấy trước khi gửi), đúng tradable unit + side, chuyển OPEN, qty > 0.
  - Khớp Close bằng: position biến mất.
  - Bắt reject bằng `v3.notifications` (có `correlation_id` thật, case `orderRejected`).
- Đo từ Node (API):

  | Thao tác | Thời gian |
  |---|---|
  | BUY → ack | 245 ms |
  | BUY → OPEN | 504 ms |
  | Close → bị xoá | 641 ms |

- Nhịp quote SPX500 ~100–500 ms/tick (push theo thay đổi giá).
- Frame stream id 0 type `0x03` là KEEPALIVE; frame nhận `<sid> 28 20` = PAYLOAD|NEXT.

## 5. REST hera (Bearer = account JWT)

- `GET https://api.<cluster>.tradelocker.com/hera/api/v1/reports/closed-positions-history` trả
  `{data:[{positionId, instrumentName, positionSide, closedAmount, averageOpenPrice, closePrice, openDateTime,
  closeDateTime, commission, swap, pnl, closeOrderId, ...}], links:{next}}`, phân trang qua `links.next`.
- Khác: `order-history-report`, `trades-report`, `balance-history-report`, `total-profit?from=`.

## 6. Hệ quả cho thiết kế (đã đối chiếu code trade-multi 2026-10-26)

| Sự thật | Hệ quả |
|---|---|
| Quote không có timestamp sàn | Latency B = **tuổi tick**, giống cTrader/PrimeXBT; guard so với `confirmLatencyB` riêng |
| Không có client order id | Timeout/rớt socket sau khi gửi ⇒ `TIMEOUT_UNCERTAIN`, **không gửi lại**; mỗi symbol+side chỉ một Open in-flight |
| Id là chuỗi số cỡ 1e18 (accId 1.79e18 dùng tới bit 60; tradable unit 8.07e17). Độ lớn position id thật chưa được ghi lại ⇒ Q4 | Ticket namespace **bit 63** (`0x8000…`). Ticket `ulong` ở mọi tầng: `TradeSharedRecord`, `HistorySharedRecord`, `PositionSlot`, `SlotPersistence`; `current_tick_b` lưu string, parse bằng `ulong.TryParse` |
| Đóng theo đúng một id | Planner chỉ có PlanOpen / PlanClose(id); `close-all` bị grep A6 chặn |
| Quantity = lot (string), `lot_size` ở ticker | Volume B cấu hình bằng **lot** (không phải oz như PrimeXBT); `contractSizeB` lấy từ `lot_size` |
| Chưa có Protobuf trong solution | Thêm `Google.Protobuf` 🛑 |
| Không có thư viện RSocket .NET đáng tin | Tự viết framing tối thiểu |

## 7. Chưa biết (chuyển sang Phase 0)

1. JWT 5 phút hết hạn có cắt socket không.
2. Có hedge thật không (Buy + Sell cùng symbol, nhiều position song song).
3. Có XAUUSD không, kèm `lot_size` / `tick_size` / `lot_step` / `minimum_lot`.
4. Position id có luôn < 2^63 không.
5. Server có fragment frame (FOLLOWS) không.
6. Hành vi khi timeout hoặc gửi trùng lệnh.
7. Nhịp quote XAUUSD.
8. Các mã `ProtoError`.
9. Rate limit.
10. Tuổi thọ refresh token.
11. Broker live có chạy APOLLO không.
12. T&C của broker về bot/arbitrage.

Xem [phase-0-spike.md](phase-0-spike.md).
