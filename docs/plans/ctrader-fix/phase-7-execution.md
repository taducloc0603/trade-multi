# Phase 7 — PHIÊN TIỀN THẬT duy nhất: spike 721 → nghiệm thu 5/6 có position → thực thi

> **Phase duy nhất chạm tiền thật.** Mọi thứ trước đây là read-only / tài khoản trống.
> Cổng vào phase này là chặt nhất trong cả kế hoạch.
>
> **Tái cấu trúc 2026-09-16:** FxPro tắt FIX cho demo → tài khoản duy nhất là **live 8220816**. Để nạp
> tiền và trả spread **một lần**, phase này gom **ba bước** vốn rải ở Phase 0/5/6/7 thành **một phiên**:
> **A)** spike 721 bằng ConsoleSample (câu 4/1/5/6 cũ của Phase 0) · **B)** nghiệm thu Phase 5/6 Lớp 2
> (cần position thật) · **C)** executor thật. Thứ tự A → B → C là bắt buộc: A là cổng chặn R1, B dùng
> position mở tay để kiểm chiều đọc trước khi app được phép tự đặt lệnh ở C.

[← Phase 6](phase-6-history.md) · [Index](README.md) · Phase sau: [Phase 8](phase-8-hardening.md)

---

## Mục tiêu

1. **Bước A:** chứng minh bằng log raw rằng market order ngược chiều + `721` đóng position trên tài khoản
   hedged (R1), đo contract size thật (`38=1` → 0.01 lot), format reject tag 58, partial fill.
2. **Bước B:** với position mở tay, chiều đọc của app (Phase 5/6) đúng: ticket mã hoá, R2 khi restart,
   R3 version, R4 decode, history khi đóng.
3. **Bước C:** thay `NullCTraderTradeExecutor` bằng executor thật. Một cặp A(MT)/B(cTrader) được mở bởi
   **auto signal**, giữ, rồi đóng sạch cả hai chân.

---

## Phụ thuộc phase trước

- Phase 4, 5, 6 đã pass **Lớp 1** và đã soak trên tài khoản trống. Toàn bộ chiều đọc đã đúng.
- **Đã đóng P5-D1 và P5-O1** ([Phase 5 "Cổng sang Phase 6"](phase-5-open-positions.md)): soak ≥ 2 ngày liên tục qua cuối tuần trên bản build cuối, kết luận QUOTE logout lặp.
- **Đã đóng các mục Phase 4 hoãn P4-D1…D7** ([bảng](phase-4-quote-feed.md)): soak + giờ nghỉ, kill mạng với kiểm tra sống,
  cServer trả lời TestRequest, tổng hợp R8, logout khi đóng app. P4-D7 (price-freeze + so log, cần Start) làm ở Bước B.
- **Task R8-B (`confirm_latency_ms_b`) đã merge** và giá trị B đã đặt theo số liệu soak Phase 4 ([README R8](README.md)). Thiếu task này thì chân B bị skip/chặn `LATENCY` khi đặt lệnh thật.
- Phase 0 **GO-READ** (câu 2/3/7/8/9/10) đã có log raw; `C:\tmp\ctrader-spike` còn build được.
- **Tiền:** live 8220816 đã nạp **≥ $30–50** (margin 1 oz XAUUSD @1:500 ≈ $8.7; Bước C với
  `max_total_opens=1` cần thêm chân A trên MT). Ước tính chi phí cả phiên: ~6–10 vòng mở/đóng 1 oz ×
  ($0.24 spread + commission) ≈ **$3–5**.
- **Giờ:** trong giờ XAUUSD mở (05:00 → 03:59:45 UTC+7). Toàn bộ A+B+C nên nằm trong **một ngày**.
- **Người thao tác:** chủ dự án gõ mọi lệnh ConsoleSample và bấm mở/đóng tay trên cTrader Web (policy
  chặn Claude gửi lệnh trên tài khoản thật). Claude phân tích output đã che 554, đọc tab Positions/History
  qua Playwright (chỉ đọc), ghi memo.

---

## Bước A — Spike 721 bằng ConsoleSample (trước khi bật executor)

Dùng `C:\tmp\ctrader-spike\ConsoleSample` với `Config-dev.LIVE-TRADE.cfg` (chủ dự án điền mật khẩu).
Thứ tự và tiêu chí **y hệt** [phase-0-spike.md](phase-0-spike.md) câu 4 → **1** → 5 → 6:

| Câu | Gõ | Kỳ vọng | Ghi memo Phase 0 §5 |
|---|---|---|---|
| 4 | `1\|spike-open-1\|41\|buy\|market\|1` | Hai `35=8`: `150=0/39=0` rồi `150=F/39=2`, ghi `721=<posId>`. cTrader Web tab Positions: Quantity **0.01**, Position ID = `721` | contract size = 100 |
| 5 | *(quan sát câu 4)* | Số `35=8` có `150=F` cho cùng `11`; `14 CumQty` | partial fill |
| **1** | `1\|spike-close-1\|41\|sell\|market\|1\|<posId>` → `7\|spike-pos-1` | Fill với `721` = posId cũ; AP **`728=2`**. Web tab Positions = 0 | **GO/NO-GO R1** |
| 6 | `1\|spike-rej\|41\|buy\|market\|999999999` | `150=8`/`39=8`, đọc nguyên văn `58`, `103=0` | format reject |
| — | `7\|spike-pos-final` | `728=2` | không mồ côi |

> **NO-GO ở câu 1 → DỪNG phase.** Giữ nguyên Phase 1–6. Làm lại **chỉ** chiều đóng của
> `CTraderTradeExecutor` bằng Open API `ProtoOAClosePositionReq(positionId, volume)`; Bước B/C chạy sau.

## Bước B — Nghiệm thu Phase 5/6 Lớp 2 (position mở tay, app chỉ quan sát)

`NullCTraderTradeExecutor` **vẫn tại chỗ**. Chủ dự án mở/đóng tay trên cTrader Web (tài khoản 8220816).

- [ ] Mở tay 1 position 0.01 lot XAUUSD → app tab Trade: ticket mã hoá, symbol, type, open price đúng;
      profit đúng chiều. R4: `TryDecode(ticket)` = Position ID trên web.
- [ ] **Restart app khi đang có position** → cửa sổ chưa sync `MapUnavailableOrParseError`, **không**
      `OnlyAOpen`, không external-partial-close, watchdog "skip".
- [ ] R3: version tăng đúng một lần khi mở; để yên 3 phút không rebuild; nút "Đóng" per-pair **không
      bấm** ở bước này (executor null) — chỉ kiểm nút không nuốt click về mặt UI.
- [ ] Mở thêm 1 position (tổng 2) → hiện đủ, không nhân bản.
- [ ] **(từ Phase 5 Lớp 2)** Đóng tay 1 position → row biến mất khỏi tab Trade **≤ 500 ms**; trong lúc có position,
      log `[CTRADER][TRADE]` không có lần `trades map AVAILABLE` nào trước `PositionsSynced=true` sau restart.
- [ ] **(từ Phase 5)** Có position + bật Start (Bước B đã chấp nhận Start): kill socket TRADE → log VM thấy
      `MapUnavailableOrParseError`, watchdog/external-partial-close **skip** (bằng chứng log mà Phase 5 không lấy được khi chưa Start).
- [ ] Đóng tay theo thứ tự ngược → tab History app ≤ 500 ms mỗi record, `Ticket` trùng, `OpenPrice`/
      `ClosePrice` khớp web, `closeExecutionMs` hợp lý; `RegisterCloseExecutionForNewHistoryTickets` một
      lần mỗi ticket; history version độc lập với trades.
- [ ] **(từ Phase 1)** Trigger một auto open với `NullCTraderTradeExecutor` còn tại chỗ → chân B fail
      `"cTrader chưa được kích hoạt"`, chân A (MT5) rollback qua `CloseOpenedLegByTimeoutAsync` sau
      `open_pending_time_ms`; không exception chưa bắt. `max_total_opens = 1`.
- [ ] **(từ Phase 2, câu 4 — luật đã có unit test `PlatformBSwitchGuardTests`; ở đây chỉ kiểm hiển thị UI)** Có 1 slot mở: đổi `platform_b` `mt5 → ctrader` trong Config → Save **bị từ chối** với
      "Đang có N slot mở — đóng hết trước khi đổi nền tảng sàn B." (N đúng); đổi `mt4 ↔ mt5` → Save **OK như trước**.
- [ ] Web tab Positions = 0 trước khi sang Bước C.

## Bước C — Executor thật

Thay `NullCTraderTradeExecutor` bằng `CTraderTradeExecutor` **chỉ sau khi A GO và B pass**. Chi tiết
"Việc làm" và "Nghiệm thu" bên dưới.

---

## Chốt trước khi code

| # | Câu hỏi | Bối cảnh |
|---|---|---|
| 1 | **Bước A đã GO** trong cùng phiên (hoặc ≤ 2 tuần)? | R1. Không có đường lùi sau khi executor thật lên. |
| 2 | Giá trị `volumeBUnits` thật sẽ dùng? | Tag 38 tính bằng **đơn vị cơ sở**. Với XAUUSD contract 100 (Phase 0 xác minh): `1 unit = 0.01 lot`. Phải khớp với lot đang đặt ở panel one-click của terminal MT chân A (R5). |
| 3 | `CurrentOpenPendingTimeMs` hiện là bao nhiêu? | **Phải ≥ 2000 ms** (≥ 4 × chu kỳ poll 500 ms). Thấp hơn thì chân B confirm *sau* khi rollback đã bắn → bão rollback. |
| 4 | `max_total_opens` trong suốt nghiệm thu? | **1**. Chỉ nâng sau khi toàn bộ checklist pass. |

---

## Việc làm

### `CTraderTradeExecutor` — shim mỏng, mục tiêu < 150 dòng, **không chứa logic**

`TradeDesktop.App/Services/CTraderTradeExecutor.cs`, thay chỗ `NullCTraderTradeExecutor` trong
`App.xaml.cs`.

> **Comment bắt buộc ở đầu file (§6 README):**
>
> ```
> // Rule E — lớp này THUẦN BỊ ĐỘNG.
> // Báo cáo trạng thái, thực thi lệnh được ra lệnh, hết.
> // KHÔNG BAO GIỜ: tự flatten position cho là mồ côi, tự retry close,
> // tự reconcile bằng cách gửi lệnh.
> // Bất kỳ hành vi nào như vậy là một đường open/close mới bỏ qua signal engine.
> ```

#### Open

```
NewOrderSingle(D)
  11 ClOrdID   = {pairId}-{A|B}-{unixMs}-{counter}   ← duy nhất xuyên restart
  55 Symbol    = symbolId (SỐ)
  54 Side      = Buy/Sell theo request.Action
  40 OrdType   = 1 (Market)
  38 OrderQty  = volumeBUnits            ← đơn vị cơ sở, KHÔNG phải lot
  60 TransactTime = UTC
  59 TimeInForce = 3 (IOC)   ← spec nói bị bỏ qua, nhưng SDK vẫn set cho market; set theo SDK
  (KHÔNG có 721 → tạo position mới)
```

Dựng đúng như `ConsoleSample/Program.cs:230-270`:

```csharp
var msg = new QuickFix.FIX44.NewOrderSingle(
    new ClOrdID(clOrdId), new Symbol(symbolId.ToString()),
    new Side(isBuy ? '1' : '2'), new TransactTime(DateTime.UtcNow), new OrdType(OrdType.MARKET));
msg.Set(new OrderQty(volumeBUnits));
msg.Set(new TimeInForce('3'));
// Close: msg.SetField(new StringField(721, positionId.ToString()));
```

Chờ terminal report với timeout **nhỏ hơn hẳn** `OpenPendingTimeoutMs`:
- **khớp** khi `150=F` **và** `39=2` → `Success=true`
- **reject** khi `150=8` **hoặc** `39=8` → `Success=false`, `Detail` = free-text tag 58 (vì `103` luôn = 0 — R11)

> Đọc **cả** `150 ExecType` **lẫn** `39 OrdStatus`. Hai tag bổ sung nhau: `150` nói "chuyện gì vừa
> xảy ra", `39` nói "lệnh đang ở trạng thái nào". Chỉ tin một tag là tự mở cửa cho sai lệch giữa hai
> thông tin mà spec không cam kết luôn đồng bộ.
- timeout → `Success=false`

> **ExecutionReport dùng cho reject, KHÔNG dùng cho confirm** (§4.2). Ticket vẫn được phát hiện qua
> polling trades map như mọi platform khác. Market order sinh **hai** report — `150=0/39=0` rồi
> `150=F/39=2`; **không** coi cái đầu là khớp (R11).

#### Close

```
NewOrderSingle(D)
  54 Side  = NGƯỢC với position
  40       = 1 (Market)
  38       = toàn bộ volume của position
  721 PosMaintRptID = CTraderTicketCodec.TryDecode(request.Ticket)
```

`request.RowIndex` **bị bỏ qua** — nó chỉ có nghĩa với UI automation của MT.

> **Căn cứ cho tag 721** (R1): spec mô tả 721 là *"A position ID where this order should be placed"*;
> SDK chính thức `quickfixnsamples.net/ConsoleSample/Program.cs` set 721 ở nhánh market order; và
> `FIX-API-Sample/MessageConstructor.cs:346` ghi *"If not set, new position will be created"*. Điều
> **duy nhất** còn phụ thuộc Phase 0 câu 1: order ngược chiều mang 721 net về 0 trên tài khoản hedged.
> Phase này **không được bắt đầu** khi câu đó chưa có log raw chứng minh.

> **`TryRefreshCloseRows` ở router vẫn chạy nguyên, không bypass** (§4.1). Nó re-resolve ticket **sau
> khi lấy mutex** và fail closed nếu position không còn — đó là bảo đảm Rule F, và nó hoạt động qua
> decorator mà không cần sửa gì. `RowIndex` nó sinh ra thì executor bỏ qua.

### B2 — `OpenPairAsync` / `ClosePairAsync` trả lỗi tường minh

Router **không dùng** hai method này — nó luôn dispatch per-leg (`ExecuteOpenPairPerLegAsync` ~:809,
`ExecuteClosePairPerLegAsync` ~:840). Nhưng `ITradePlatformExecutor` bắt buộc implement.

**Đừng để chúng "có vẻ chạy được".** Trả `Success=false` với
`Detail = "cTrader không hỗ trợ pair-level dispatch"`, để nếu tương lai ai đó gọi nhầm thì thấy ngay
thay vì âm thầm sai.

### Validation + assert

- `volumeBUnits`: `> 0`, hữu hạn, bội của `0.01`. `contractSizeB`: `> 0`, đã được Phase 0 xác minh.
- Khởi động: `CurrentOpenPendingTimeMs >= 4 × 500 ms`; không đạt → `[CTRADER][ERROR]` + **từ chối bật
  cTrader**.
- `ClOrdID` duy nhất xuyên restart — nó là khoá của `OrderStatusRequest`, trùng là mất khả năng tra cứu.

---

## Rủi ro liên quan

**R1** (sống còn) · R4 (decode ticket) · R5 (volume) · R11 (hai report, tag 58) ·
**§4.1** (giữ `TryRefreshCloseRows`) · **§4.2** (không short-circuit confirm)

---

## Nghiệm thu

**Nghiệm thu Bước C — live 8220816, `max_total_opens = 1`, tối thiểu một phiên đầy đủ.** Tiền đề: Bước A
GO và Bước B pass trong cùng phiên. Chạy trong giờ giao dịch XAUUSD (05:00 → 03:59 UTC+7); máy dev hiện
có **2 terminal MT5** nên đủ 6 ô ma trận có thể nghiệm thu tại chỗ. Chân A trên MT cũng là tiền thật
nếu terminal A là live — chủ dự án xác nhận terminal A dùng cho nghiệm thu là demo hay live trước khi bắt đầu.

### Vòng đời cơ bản

- [ ] Một pair A(MT)/B(cTrader) mở bởi **auto signal** — không phải bấm tay — cả hai chân khớp.
- [ ] Ticket B được `RegisterOpenExpectedForNewTickets` nhận trong **≤ 1 chu kỳ poll** (500 ms).
- [ ] Slippage và `openExecutionMs` chân B là số hợp lý, không phải rác.
- [ ] Đợi close signal **tự nhiên** (không ép) → cả hai chân đóng sạch.
- [ ] Verify trên **cả hai nền tảng**: terminal MT (chân A) và **tab Positions trên cTrader Web** (chân B,
      đọc qua Playwright MCP) đều trống — **không còn position mồ côi**. Lưu snapshot web làm bằng chứng
      (ngoài repo).
- [ ] Record đóng xuất hiện ở tab History, `closeExecutionMs` hợp lý.

### Đường recovery — quan trọng không kém đường thường

- [ ] **Partial open rollback**: cố tình làm chân A fail (đóng terminal MT trước khi signal bắn) →
      `CloseOpenedLegByTimeoutAsync` **đóng được chân B cTrader**. Đây là ca dễ hỏng nhất vì nó đi qua
      đường close không có signal.
- [ ] **Chiều ngược lại**: làm chân B fail (kill TRADE session) → chân A được rollback đúng.
- [ ] **Nút "Đóng" per-pair thủ công** trên pair có chân cTrader → đóng sạch cả hai chân.
- [ ] **Restart app khi đang có pair mở** → recovery khôi phục slot đúng, ticket cTrader decode được,
      close sau đó vẫn hoạt động.
- [ ] Reject từ broker (đặt `volumeBUnits` sai cỡ để ép reject) → `Success=false` với lý do rõ từ tag 58,
      chân A rollback đúng.

### Tuân thủ quy tắc

- [ ] **Rule F**: `TryRefreshCloseRows` vẫn chạy cho leg cTrader — grep log xác nhận nó re-resolve
      ticket trước mỗi close.
- [ ] **Rule E**: grep toàn bộ log của phiên, **không có** close nào không bắt nguồn từ signal hoặc từ
      một trong các exception đã liệt kê ở CLAUDE.md §2.
- [ ] Cooldown / quota / opposite-lock hành xử y hệt như với cặp MT-MT.

### Ma trận platform

Xem [README — Ma trận platform được hỗ trợ](README.md). Phase này là lần cuối ma trận được kiểm với
khả năng đặt lệnh thật:

| A | B | Kỳ vọng | ☐ |
|---|---|---|---|
| mt4 | mt4 | Mở/đóng pair bình thường — **không hồi quy** | ☐ |
| mt4 | mt5 | Mở/đóng pair bình thường — **không hồi quy** | ☐ |
| mt5 | mt4 | Mở/đóng pair bình thường — **không hồi quy** | ☐ |
| mt5 | mt5 | Mở/đóng pair bình thường — **không hồi quy** | ☐ |
| mt4 | ctrader | Mở/đóng pair bình thường | ☐ |
| mt5 | ctrader | Mở/đóng pair bình thường | ☐ |

> Bốn ô MT-MT là **kiểm tra hồi quy**, không phải thủ tục. Đây là lần duy nhất trong cả kế hoạch mà
> một lỗi ở executor mới có thể làm hỏng cặp MT-MT đang chạy production.

### Chất lượng log

- [ ] Không có ERROR/WARN bất thường.
- [ ] `[CTRADER]`, `[ROUTER]`, `[SLOT]`, `[CLOSE_SELECT]`, `[CYCLE]` mạch lạc, truy được vết từ signal
      đến fill.

### Nâng dần

- [ ] **Chỉ sau khi toàn bộ trên pass** mới nâng `max_total_opens` lên 2, soak lại, rồi mới lên tiếp.

### Gate chung

- [ ] Test suite không tăng so với baseline Phase 0. Build sạch.

---

## Rollback

**Đổi đăng ký trong `App.xaml.cs` về `NullCTraderTradeExecutor`** — app quay lại chế độ read-only của
Phase 6, **không mất dữ liệu**, position đang mở vẫn được theo dõi và có thể đóng tay trong cTrader.

Đây là rollback rẻ nhất có thể có cho một phase giao dịch thật, và là lý do
`NullCTraderTradeExecutor` được giữ suốt từ Phase 1.

> Nếu phải rollback khi **đang có position mở**: đóng tay trong cTrader trước, để app không treo slot
> ở trạng thái PendingClose.

---

## Cổng sang Phase 8

- [ ] Toàn bộ checklist pass ở `max_total_opens = 1`.
- [ ] Đã nâng dần và soak ổn định ở mức quota thật định dùng.
- [ ] Số liệu hedge ratio thực tế đã được ghi nhận (làm đầu vào cho cảnh báo ở Phase 8 — R5).
