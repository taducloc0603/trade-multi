# Phase 7 — Thực thi lệnh

> **Phase duy nhất app được đặt lệnh cTrader.** Mọi thứ trước đây là read-only.
> Cổng vào phase này là chặt nhất trong cả kế hoạch.

[← Phase 6](phase-6-history.md) · [Index](README.md) · Phase sau: [Phase 8](phase-8-hardening.md)

---

## Mục tiêu

Thay `NullCTraderTradeExecutor` bằng executor thật. Một cặp A(MT)/B(cTrader) được mở bởi **auto
signal**, giữ, rồi đóng sạch cả hai chân.

---

## Phụ thuộc phase trước

- Phase 4, 5, 6 đã pass và đã soak. Toàn bộ chiều đọc đã đúng.
- **Phase 0 câu 1** (đóng bằng tag 721) và **câu 4** (quy đổi volume) được xác nhận lại lần cuối.

---

## Chốt trước khi code

| # | Câu hỏi | Bối cảnh |
|---|---|---|
| 1 | Xác nhận lại kết quả Phase 0 câu 1 — **tag 721 có thực sự đóng position không**? | R1. Nếu spike đã cũ hơn vài tuần thì **chạy lại**. Không có đường lùi sau khi executor thật lên. |
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

**Demo account. `max_total_opens = 1`. Tối thiểu một phiên đầy đủ.**

### Vòng đời cơ bản

- [ ] Một pair A(MT)/B(cTrader) mở bởi **auto signal** — không phải bấm tay — cả hai chân khớp.
- [ ] Ticket B được `RegisterOpenExpectedForNewTickets` nhận trong **≤ 1 chu kỳ poll** (500 ms).
- [ ] Slippage và `openExecutionMs` chân B là số hợp lý, không phải rác.
- [ ] Đợi close signal **tự nhiên** (không ép) → cả hai chân đóng sạch.
- [ ] Verify trên **cả hai nền tảng** (terminal MT và app cTrader): **không còn position mồ côi**.
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
