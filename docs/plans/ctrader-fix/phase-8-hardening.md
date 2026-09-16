# Phase 8 — Hardening + tài liệu

> Phase cuối. Không thêm khả năng mới, chỉ làm cho những rủi ro **đã biết** trở nên **nhìn thấy được**,
> và ghi lại cho người đến sau.

[← Phase 7](phase-7-execution.md) · [Index](README.md)

---

## Mục tiêu

Ba việc:
1. Biến các rủi ro "đã chấp nhận" (R5, R8, R10) thành cảnh báo chủ động thay vì bất ngờ trong production.
2. Cập nhật `README.md` và `CLAUDE.md` để hành vi mới được ghi lại đúng chỗ người ta sẽ tìm.
3. Ghi lại **pitfall** quan trọng nhất để người sau không phá.

---

## Phụ thuộc phase trước

- Phase 7 đã chạy ổn định ở mức quota thật định dùng.
- Số liệu hedge ratio và tần suất skip `LATENCY` chân B đã được ghi nhận từ Phase 4 và 7.

---

## Chốt trước khi code

| # | Câu hỏi | Bối cảnh |
|---|---|---|
| 1 | Ngưỡng cảnh báo hedge asymmetry? | Đề xuất `[0.95, 1.05]` → `[WARN]`; ngoài `[0.8, 1.2]` → Telegram. Điều chỉnh theo số liệu thật thu được ở Phase 7. |
| 2 | **R8** — sau khi có số liệu thật về skip `LATENCY` chân B, có cần `confirm_latency_ms_b` riêng không? | Nếu tần suất skip cao đến mức chặn tín hiệu hợp lệ thì đây là **task tách biệt**, mở issue riêng, **không** nhét vào phase này. Nếu thấp thì đóng lại và ghi nhận. |
| 3 | Telegram `CTRADER_SESSION_DOWN` có debounce không? | Đề xuất: có, 30 s — reconnect chớp nhoáng không đáng bắn. |

---

## Việc làm

### Cảnh báo chủ động

| Việc | Rủi ro | Chi tiết |
|---|---|---|
| Wire `HedgeVolumeConsistencyChecker` | **R5** | `ratio = (volumeBUnits / contractSizeB) / volumeALots`, `contractSizeB` từ config — **không hardcode 100**. Chạy mỗi lần `ApplyRuntimeConfig`. Log `[CTRADER][INFO] hedge ratio=1.00`; `[WARN]` ngoài `[0.95, 1.05]`; Telegram `HEDGE_VOLUME_ASYMMETRY` ngoài `[0.8, 1.2]` |
| Telegram `CTRADER_SESSION_DOWN` | §4.7 | Khi QUOTE hoặc TRADE logout, có debounce |
| Telegram `CTRADER_DIGITS_MISMATCH` | **R6** | Đã fail-closed từ Phase 4; phase này chỉ thêm thông báo |
| Telegram `CTRADER_POSITIONS_NOT_SYNCED` | R2 | Sau 60 s chưa sync |

> **R5 đáng nhắc lại:** `CalculateTradeProfit` = `(Bid - openPrice) * point`, **bỏ qua lot size**.
> Nếu B lệch notional so với A thì `slot.LastProfitSnapshot` vẫn chỉ là tổng hai delta điểm giá;
> `min_profit_to_close` (Rule D gate) và priority-close ra quyết định trên con số **không còn bám tiền
> thật**. Cảnh báo này là thứ duy nhất khiến điều đó nhìn thấy được — công thức **không** được sửa
> trong phạm vi này.

### Tài liệu

#### `README.md`

| Section | Việc |
|---|---|
| §7.1 Runtime config | Thêm khối `ctraderFix` vào bảng config; ghi rõ password lưu plaintext trong `sans_json` và ba quy tắc bảo vệ |
| §7.2 Routing thực thi lệnh | Thêm `CTraderTradeExecutor`; ghi rõ close bằng tag 721, `RowIndex` bị bỏ qua |
| §8 Data source | Thêm nhánh cTrader FIX cho cả 3 cửa; ghi rõ `Profit`/`Commission` của history B là **số tính lại**, không phải số broker (R10) |
| §13 | Ghi chú multi-slot hoạt động không đổi với chân cTrader |

#### `CLAUDE.md`

§5 "Common pitfalls" — thêm mục mới:

> **cTrader FIX (sàn B)**
> - Trades/history map cTrader **phải** báo `IsMapAvailable=false` cho tới khi positions sync xong.
>   Nếu không, `GetLivePairTradeState` trả `OnlyAOpen` và `TryDetectAndHandleExternalPartialClose`
>   sẽ **đóng chân A của một hedge đang mở thật**.
> - `Timestamp` của `SharedMapReadResult` phía cTrader là **content-version counter**, không phải thời
>   gian. Trả hằng số ⇒ open không bao giờ được confirm. Trả `UtcNow` ⇒ rebuild
>   `TradeRealtimeProfitRows` mỗi 500 ms và nút "Đóng" per-pair nuốt click.
> - Ticket cTrader được mã hoá namespace (`| 0x4000_0000_0000_0000`) để không đụng dải ticket MT.
>   Đây cũng là dạng lưu trong `current_slots`.
> - Adapter FIX **thuần bị động** — không bao giờ tự flatten/retry/reconcile bằng cách gửi lệnh
>   (Rule E).
> - Mật khẩu FIX nằm **plaintext** trong `configs.sans_json`. **Không bao giờ** log `sans_json` thô
>   (dùng `SansJsonHelper.Redact`), không log tag 554, không bật `FileLogPath` của QuickFIX/n, không
>   nối `CurrentCTraderFixConfig` vào `RuntimeSummary`.
> - QuickFIX/n **không tự gắn** `553 Username`/`554 Password` vào Logon từ cfg — phải set trong
>   `IApplication.ToAdmin` (như `QuickFixNApp.cs` của Spotware). Quên ⇒ server **im lặng**, chỉ thấy
>   reconnect loop.
> - `SecurityList` tag `1007`/`1008` đọc bằng số thô; lớp `Tags` của QuickFIX/n gọi chúng là
>   `SideReasonCd`/`SideTrdSubTyp`.
> - `PositionReport` `728=2` = "không có position" **vẫn phải** set `PositionsSynced=true`.
> - `NormalizePlatform` có **3 bản sao** (`ConfigService` :25 và :488, `RuntimeConfigState` :460).
>   Sửa một chỗ mà quên chỗ khác ⇒ `platform_b` âm thầm về `mt5` và app click vào HWND MT5 sai.

§9 "Key external dependencies" — thêm cTrader FIX API (QuickFIXn.Core + QuickFIXn.FIX4.4 + dictionary
`FIX44-CSERVER.xml`).

#### Bảng tiến độ

Cập nhật [README.md §7](README.md) của thư mục này: đánh dấu toàn bộ phase hoàn thành.

---

## Rủi ro liên quan

**R5** (cảnh báo) · R6 · R8 (quyết định, không code) · R10 (ghi tài liệu)

---

## Nghiệm thu

### Cảnh báo hoạt động

- [ ] Đặt `volumeALots` lệch cố ý → `[WARN]` xuất hiện; lệch nhiều → Telegram bắn.
- [ ] Kill session → `CTRADER_SESSION_DOWN` bắn sau debounce; reconnect chớp nhoáng **không** bắn.
- [ ] Đặt `point` sai → `CTRADER_DIGITS_MISMATCH` bắn và app fail closed (hồi quy từ Phase 4).
- [ ] Không có cảnh báo nào bắn sai trong một phiên bình thường.

### Tài liệu đúng

- [ ] `README.md` §7.1, §7.2, §8, §13 phản ánh đúng hành vi **đã ship**, không phải hành vi dự định.
- [ ] `CLAUDE.md` §5 và §9 đã có mục cTrader.
- [ ] Một người chưa biết dự án đọc `README.md` §8 hiểu được chân B lấy dữ liệu từ đâu.
- [ ] Bảng tiến độ ở `docs/plans/ctrader-fix/README.md` được cập nhật.

### Gate chung

- [ ] Chạy lại **toàn bộ** test suite; không tăng so với baseline Phase 0.
- [ ] Build sạch, không warning mới.

---

## Rollback

Revert commit. Phase này không đổi hành vi giao dịch — chỉ thêm log/cảnh báo và tài liệu, nên rollback
an toàn tuyệt đối.

---

## Sau Phase 8

Những thứ **cố ý để ngoài phạm vi**, mở issue riêng nếu cần:

| Việc | Vì sao để ngoài |
|---|---|
| `confirm_latency_ms_b` riêng cho chân B | R8 — đổi hành vi một service dùng chung, cần thiết kế riêng |
| `CalculateTradeProfit` tính theo lot size | R5 — đụng thẳng vào Rule D và TP, rủi ro cao, phải có kế hoạch riêng |
| cTrader ở vị trí A | Chủ dự án đã chốt chỉ làm B. Opposite-open price guard và `min_profit_to_close` đều tính theo chân A nên rủi ro cao hơn hẳn |
| Cưỡng chế `volumeALots` cho chân MT | Phụ thuộc `docs/PLAN-MT5-OCT-VOLUME-INPUT.md` — plan đó đang bế tắc ở chính cổng kiến trúc của nó |
| Dùng Open API bổ sung cho balance/equity/symbol metadata | FIX không có những thứ này. Chỉ làm nếu thực sự cần |
