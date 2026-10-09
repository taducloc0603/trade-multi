# PrimeXBT — Kết quả quét (2026-10-09)

> Quét bằng Playwright MCP trên tài khoản **DEMO PXTrader 2.0** (USD, HEDGE mode), symbol **XAU/USD**.
> User tự đăng nhập; mọi token được che khi in ra. Có **2 lệnh demo đã được user đồng ý** (Buy→Close,
> Sell→Close, 0.01 oz) để bắt frame giao dịch. Kết thúc phiên quét: tài khoản flat, không tạo tài khoản mới.
>
> Mẫu JSON đã che danh tính: [`fixtures/`](fixtures/). Account id thật được thay bằng `D0000000`.

[Index](README.md)

---

## 1. Sản phẩm của PrimeXBT — hai đường có thể dùng cho sàn B

| Đường | Mô tả | Hệ quả cho app |
|---|---|---|
| **W — PXTrader 2.0 (web)** | Nền tảng web riêng của PrimeXBT, CFD + crypto futures, TradingView chart. Tài khoản hiện có: Demo `#D…`, Real `#L…` (USD và USDT). | Phải tích hợp mới qua WebSocket JSON (reverse-engineered, **không phải API công khai**). |
| **M — MetaTrader 5 của PrimeXBT** | Hộp thoại "Create new account" cho chọn platform **MetaTrader 5** (Real/Demo). JWT có feature `product.mt5`, `mt5-pro`, `mt5-zso`. | Dùng **ngay** đường MT5 hiện có của sàn B (EA DataExporter + click HWND). Gần như không cần code. |

Chi tiết lựa chọn: [README §2](README.md#2-hai-track--quyết-định-ở-cổng-phase-0).

---

## 2. Xác thực (PXTrader 2.0)

| Thành phần | Chi tiết |
|---|---|
| Access JWT | HS256, lưu ở `localStorage["prm-token"]` (chuỗi JWT thuần). Claims: `sub` (clientId), `cip` (IP client), `sid`, `fts` (feature flags), `aud:"primexbt"`. **Hạn 7 ngày** (`exp - iat = 604800`). |
| Refresh | `GET https://api.primexbt.com/v2/auth/refresh` với cookie **httpOnly** `refresh_token` (domain `.api.primexbt.com`, hạn ~30 ngày) + header `x-auth-guard`, `x-client-version`, `x-client-time`. |
| Cookie khác | `ws_token`, `fws_token`, `bws_token` (httpOnly, `.api.primexbt.com`, ~30 ngày). **Phase 0: WS `fws` BẮT BUỘC `jwt=` + cookie `fws_token`.** Mỗi cookie có **path riêng**: `fws_token=/v2/fws`, `ws_token=/v2/pws`, `bws_token=/v2/bws`, `refresh_token=/v2/auth/refresh` (+ bản `/v2/auth/check`) — hỏi cookie theo URL gốc sẽ không thấy (bẫy đã gặp ở Phase 2). |
| `auth-guard` | base64 của 32 byte, **sinh từ device fingerprint** (`this.fp$` trong bundle). Được gắn vào cả URL WS (`&auth-guard=`) lẫn header REST. **Chưa biết server có kiểm tra khớp với session hay không** → câu hỏi chặn của Phase 0. |
| `client-version` | `34866` — build của web app. Đổi khi PrimeXBT deploy. |
| Đăng nhập | **Chưa bắt được** (user đăng nhập trước khi gắn listener). Có dấu hiệu Google One Tap (`g_state`). `twoFactorEnabled:false` trên tài khoản quét. |
| Đăng xuất | `POST /v2/auth/sign-out` (jwt + credentials). |

## 3. Realtime — WebSocket JSON

Hai socket, đều `wss://api.primexbt.com/v2/...?jwt=<JWT>&client-version=<n>&auth-guard=<AG>`:

| Socket | URL | Vai trò |
|---|---|---|
| `pws` | `/v2/pws/` | Platform: `client`, `pfx/accounts2`, `wallet`, `rates`, `trading/activity`, `notification/events`… |
| `fws` | `/v2/fws/?accountId=<id thường, vd d0000000>` | **Futures/CFD theo tài khoản**: giá, vị thế, lệnh, báo cáo. **Đây là socket duy nhất app cần.** |

### Envelope

```json
// client → server
{"type":"SUBSCRIPTION"|"REQUEST","rid":<int tăng dần>,"action":"<route>","body":{...}}
// server → client
{"type":"RESPONSE","action":"<route>","body":{...},"rid":<rid>,"sid":<subscription id>}   // trả lời
{"type":"EVENT","action":"<route>","body":{...},"sid":<sid>,"aid":<seq>}                   // push
```

- `rid` do client đánh số; RESPONSE mang lại đúng `rid`. EVENT gắn với subscription qua `sid`; `aid` tăng dần theo từng EVENT của subscription.
- **Heartbeat**: client gửi `REQUEST time` mỗi **20 s**, timeout **16 s** → quá hạn thì client tự đóng & reconnect (`startHeartbeat({interval:2e4,timeoutTime:16e3})`). `time` trả `{"time": <epoch ms server>}` → đo được lệch đồng hồ.
- Một số request có cờ `shouldRepeatRequestOnDisconnect` (report, impact…). **Lệnh đặt/đóng KHÔNG có cờ này** — tức là client web không tự gửi lại khi rớt kết nối.

### Route trên `fws` (đã thấy trong traffic hoặc bundle)

| Route | Kiểu | Body | Ghi chú |
|---|---|---|---|
| `markets2` | SUBSCRIPTION | `{}` | Snapshot toàn bộ symbol (có `symbolId`, `priceScale`, `isMarketOpen`) rồi EVENT delta giá |
| `fx/market` | SUBSCRIPTION | `{"symbolId":1019}` | **Nguồn giá cho app.** EVENT `{"symId","a","b","lp","s","pr","st",…}` — **không có timestamp** |
| `trade-settings` | SUBSCRIPTION | `{"symbol":"XAU/USD"}` | min/max/step lệnh, `lotUnit`, giờ giao dịch, HMR |
| `market/detail` | SUB/REQ | `{"symId":1019}` | `isMarketOpen`, `nextOpenTime`, `nextCloseTime`, lịch TradingView |
| `metrics` | SUBSCRIPTION | `{}` | Equity, margin, **`positionMode":"HEDGE"`**, `netting:false`, `demo`, `openPositions` |
| `positions` | SUBSCRIPTION | `{}` | **Snapshot đầy đủ ~mỗi 1 s** (không phải delta) |
| `orders/active2` | SUBSCRIPTION | `{}` | Lệnh chờ |
| `orders/market/place` | REQUEST | `{"qty":0.01,"side":"BUY"\|"SELL","symbol":"XAU/USD"}` (+ `takeProfit`,`stopLoss` tuỳ chọn) | Trả `{"id":<orderId>,"error":null}` |
| `positions/id/close` | REQUEST | `{"positionId":<subPositionId>,"qty":0.01}` | Trả `{}` |
| `positions/close` | REQUEST | aggregated | **Không dùng** — đóng theo dòng gộp |
| `positions/close/all` | REQUEST | — | **CẤM dùng** trong app |
| `orders/limit/place`, `orders/stop/place`, `orders/modify2`, `orders/cancel`, `orders/cancel-all` | REQUEST | — | Không cần |
| `report/orders2` | REQUEST | `{"offset","limit","orderBy":"id","orderDir":"desc"}` hoặc `{"fromExecutedAt","toExecutedAt","symbol"}` | Lịch sử lệnh, có `positionId`, `rpl` cho lệnh đóng |
| `report/fills2` | REQUEST | `{"offset","limit","orderBy":"time","orderDir":"desc"}` | Fill theo `orderId` |
| `time` | REQUEST | `{}` | Heartbeat + giờ server |
| `bars` / `bar` | REQ / SUB | — | Nến, không cần |

## 4. Giá XAU/USD

- `symbolId = 1019`, `priceScale = 2` → **digits 2, point 0.01**. Cùng symbol còn có `XAU/USD.24` (bản 24h) — **không dùng nhầm**.
- `lotUnit = "ounces"`; `minOrderSize 0.01`, `orderStep 0.01`, `maxOrderSize 5000`, `defaultLeverage 1000` (CROSS).
  → **qty tính bằng ounce, KHÔNG phải lot.** 1 lot MT XAUUSD = 100 oz ⇒ `ContractSizeB = 100`, `0.01 lot MT = 1 oz`.
- Giờ giao dịch: `22:01 Sun - 20:59 Fri (except 20:59 - 22:01 daily)` (giờ UTC theo `nextOpenTime/nextCloseTime`). HMR (đòn bẩy giảm còn 100): `Mon-Thu 20:45-22:02, Fri 20:00-Sun 22:02`.
- Nhịp tick đo trong 33 s (86 EVENT `fx/market`): khoảng cách **min 152 ms, p50 258 ms, p90 772 ms, max 1030 ms**; 10/86 tick lặp lại y hệt bid/ask.
- Spread quan sát: 0.19 (19 point).
- **Không có server timestamp trong tick** ⇒ latency chân B chỉ đo được bằng **tuổi tick** (giống cTrader). Với p90 772 ms / max ~1 s, ngưỡng latency B < ~1000 ms sẽ chặn signal thường xuyên ngay cả khi feed khoẻ.

## 5. Vị thế (HEDGE mode)

- EVENT `positions` là **snapshot đầy đủ**, đều đặn **~950–1050 ms**. Mỗi phần tử là **dòng gộp theo symbol** (`id:0`, `side` ròng, `qty` ròng — Buy 0.01 + Sell 0.01 ⇒ `qty:0`, side "SELL", UI hiện "Neutral").
- Vị thế thật nằm trong **`subPositions[]`**: `id` (vd `10680072`, int ~10⁷), `side`, `qty`, `openPrice`, `openTime` (ISO ms), `upl` (USD), `markPrice`, `closeable`, SL/TP…
- ⚠️ `openPrice` của dòng gộp **khác** của sub-position (4179.42 vs 4179.61) — **chỉ đọc sub-position**.
- **Order ack không chứa positionId.** Liên kết order → sub-position bằng:
  `subPosition.openTime == order.executedAt` (trùng tới ms: `02:57:39.202Z`) **và** `openPrice == executedPrice` **và** side, qty, symbol.
  Bản ghi `report/orders2` của lệnh MỞ có `positionId:null`; lệnh ĐÓNG có `positionId` + `openReason:"CLOSE_POSITION"`.
- **Không có client order id** (không tìm thấy `clientOrderId` trong bundle) ⇒ không idempotent: timeout lúc đặt lệnh phải đối soát bằng `report/orders2` + `positions`, **không được gửi lại mù**.

## 6. Thời gian giao dịch đo trên demo (click UI → frame)

| Hành động | Gửi lệnh | RESPONSE (ack) | `trading/activity` "filled" | Xuất hiện / biến mất trong snapshot `positions` |
|---|---|---|---|---|
| Buy 0.01 | +58 ms | +313 ms (**~255 ms**) | +341 ms | **+1076 ms** |
| Sell 0.01 (hedge) | +42 ms | +335 ms (~290 ms) | +383 ms | +1792 ms |
| Close Buy (`positions/id/close`) | +60 ms | +312 ms (~250 ms) | +450 ms | biến mất +1327 ms |
| Close Sell | +39 ms | +314 ms (~275 ms) | +364 ms | biến mất +1689 ms |

→ Ack ~250–300 ms; **xác nhận qua snapshot vị thế mất 1.0–1.8 s** (do nhịp snapshot ~1 s).

## 7. Lịch sử

- `report/orders2` (mới nhất trước): `id, type, status:"EXECUTED", side, placedAt, executedAt, qty, executedPrice, executedQty, rpl, fee, positionId, openReason ("CLIENT"|"CLOSE_POSITION"), closeReason, releasedMargin`.
- `rpl` (lãi/lỗ thực hiện) **làm tròn theo `currencyScale=2` USD** — với 0.01 oz, lỗ −0.001 USD hiện `rpl:0`. Muốn chính xác thì tự tính `(closePrice − openPrice) × qty × dấu`.
- `report/fills2`: `id, orderId, symbolId, side, type:"TAKER", price, qty, time, fee`.
- `fee` = 0 trên demo; có financing qua đêm (`financingLongPct 0.0191`, `financingShortPct -0.005`, chu kỳ H24, triple vào thứ Tư).

## 8. DOM (chỉ để đối chiếu, không dùng làm đường giao dịch)

- Nút: `button "<giá> Sell"`, `button "<giá> Buy"`; ô `textbox "Amount"` (đơn vị ounces); switch `"One-click"` (đang TẮT → có panel xác nhận "Place market order" với nút `"Buy Market at …"`).
- Bảng Positions: dòng gộp có nút `"Close"` (đóng **cả hai** chiều!) — phải mở rộng dòng mới thấy từng sub-position với nút `"Close"` riêng → modal "Close position" → nút `"Close position"`.
- **UI không hiển thị position id**; chỉ phân biệt được bằng Side + Entry price.

## 9. Câu hỏi còn mở (chuyển sang Phase 0)

| # | Câu hỏi | Vì sao chặn |
|---|---|---|
| Q1 | Client .NET (`ClientWebSocket`) có mở được `fws` bằng JWT + `auth-guard` lấy từ trình duyệt? Bỏ `auth-guard` thì sao? Header `Origin` có bắt buộc? Có Cloudflare challenge không? | Không mở được socket ngoài trình duyệt ⇒ Track W bất khả |
| Q2 | `auth/refresh` gọi ngoài trình duyệt có được không (cookie + auth-guard)? JWT mới có đổi `sid` không? | App phải chạy nhiều ngày; JWT chỉ 7 ngày |
| Q3 | Mở socket thứ hai (app) có làm rớt phiên web của user, hoặc ngược lại? | Vận hành song song web để giám sát |
| Q4 | Dạng `error` khi lệnh bị từ chối (qty sai, market đóng, margin) | Executor phải map lỗi |
| Q5 | Lệnh đặt khi socket rớt giữa chừng: có khớp không, phát hiện thế nào | Không idempotent |
| Q6 | Rate limit | Chưa thấy tài liệu |
| Q7 | Giá MT5-PrimeXBT có cùng feed với PXTrader 2.0 không (Track M) | So sánh hai track |
| Q8 | API sign-in (email/password/2FA/captcha) | Chọn chiến lược đăng nhập |
| Q9 | Điều khoản sử dụng có cấm client tự động qua API web không | Rủi ro khoá tài khoản |
