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
- **Cổng soak dùng tiêu chí theo phiên S1–S6** ([Phase 4](phase-4-quote-feed.md)), không đòi 2 ngày liên tục.
- **Đã đóng P5-D1 và P5-O1** ([Phase 5 "Cổng sang Phase 6"](phase-5-open-positions.md)): soak ≥ 2 ngày liên tục qua cuối tuần trên bản build cuối, kết luận QUOTE logout lặp.
- **Đã đóng các mục Phase 4 hoãn P4-D1…D7** ([bảng](phase-4-quote-feed.md)): soak + giờ nghỉ, kill mạng với kiểm tra sống,
  cServer trả lời TestRequest, tổng hợp R8, logout khi đóng app. P4-D7 (price-freeze + so log, cần Start) làm ở Bước B.
- **Task R8-B (`confirm_latency_ms_b`) đã merge** và giá trị B đã đặt theo số liệu soak Phase 4 ([README R8](README.md)). Thiếu task này thì chân B bị skip/chặn `LATENCY` khi đặt lệnh thật.
- Phase 0 **GO-READ** (câu 2/3/7/8/9/10) đã có log raw; `C:\tmp\ctrader-spike` còn build được.
- **Tiền:** ~~live 8220816 đã nạp $50~~ — **LỖI THỜI**. Từ 2026-09-23 sàn B là **Deriv 1551176**;
  $50,01 ở FxPro không dùng tới. Chân A là MT5 **demo** nên không tốn tiền; chi phí Bước C ước tính
  **$2–4** (chỉ spread chân B).
  ký quỹ đỉnh $17,42 + chi phí ~$3,4 + đệm $20). Số dư ngày 2026-09-21 là **$0,01** → cần nạp.
  Đòn bẩy xác nhận trên web: **1:500**, tài khoản **Hedging**.
- **Giờ:** trong giờ XAUUSD mở (05:00 → 03:59:45 UTC+7). Toàn bộ A+B+C nên nằm trong **một ngày**.
- **Người thao tác:** chủ dự án gõ mọi lệnh ConsoleSample và bấm mở/đóng tay trên cTrader Web (policy
  chặn Claude gửi lệnh trên tài khoản thật). Claude phân tích output đã che 554, đọc tab Positions/History
  qua Playwright (chỉ đọc), ghi memo.

---

## Vốn tối thiểu và bảng chi phí (đo thật 2026-09-21)

### Số liệu gốc (không phải ước lượng)

| Thông số | Giá trị | Nguồn |
|---|---|---|
| Tài khoản | Live **8220816**, loại **Hedging**, số dư **USD 0,01** | cTrader Web (chỉ đọc) |
| Đòn bẩy | **1:500** cho khối lượng ≤ $1 000 000 | cTrader Web → Leverage |
| Giá XAUUSD | ≈ **4 354** | web + `[STATS]` soak |
| Khối lượng nhỏ nhất dùng cho test | `volumeBUnits = 1` oz = **0,01 lot** (contract size 100) | `sans_json.ctraderFix` |
| Ký quỹ 1 oz | 4 354 / 500 = **$8,71** | tính từ 2 dòng trên |
| Spread chân B | 12–20 pt = **$0,12–0,20** mỗi oz mỗi vòng | 141 dòng `[STATS]` ngày 2026-09-21 |
| Commission | ≈ **$35 / $1 000 000** khối lượng (vòng) → 1 oz ≈ **$0,15** | thống kê tài khoản demo cùng nhóm: $90,58 / $2,59m |
| **Chi phí mỗi vòng 1 oz** | **≈ $0,31** | spread ~$0,16 + commission ~$0,15 |
| Swap | **$0** | mọi vị thế đóng trong phiên, không giữ qua đêm |

### Bảng ca test và chi phí

Mỗi "vòng" = mở rồi đóng 1 oz. `Ký quỹ đỉnh` là lượng ký quỹ bị chiếm tại thời điểm nhiều vị thế nhất của ca đó.

| # | Bước | Ca kiểm | Phủ được gì | Khối lượng | Vòng (oz) | Ký quỹ đỉnh | Chi phí |
|---|---|---|---|---|---|---|---|
| A1 | A | Mở 1 oz bằng ConsoleSample, đọc `721` trong ER | Phase 0 câu 4 (tag 721 = positionId), R11 (150=0 rồi 150=F) | 1 oz | — | $8,71 | — |
| A2 | A | Gửi lệnh ngược **kèm 721** đủ volume | **Phase 0 câu 1 — đóng bằng 721** (điều kiện sống còn của executor) | 1 oz | 1 | $8,71 | $0,31 |
| A3 | A | Mở 2 oz, đóng 1 oz (kèm 721), rồi đóng nốt 1 oz | Phase 0 câu 5 — **partial close**; xác nhận Phase 6 chỉ ghi history khi đóng hẳn | 2 oz | 2 | **$17,42** | $0,62 |
| A4 | A | Gửi lệnh ngược **KHÔNG kèm 721** → tài khoản hedging tạo vị thế đối ứng; rồi đóng cả hai bằng 721 | Phase 0 câu 4b — chứng minh **bắt buộc** phải gắn 721, nếu không sẽ mở thêm vị thế thay vì đóng | 2 oz | 2 | **$17,42** | $0,62 |
| A5 | A | Gửi order sai cỡ (0,005 lot) | Phase 0 câu 6 — reject có `58`, không tạo vị thế | 0 | 0 | $0 | **$0** |
| B1 | B | Mở tay 1 oz trên web → app hiện ở tab Trade | Phase 5 Lớp 2: ticket mã hoá, symbol, giá mở, R4 decode = Position ID trên web | 1 oz | — | $8,71 | — |
| B2 | B | **Restart app khi đang có vị thế** | **R2 — invariant cứng nhất**: cửa sổ chưa sync phải là `MapUnavailableOrParseError`, không `OnlyAOpen`, không external-partial-close | (giữ B1) | — | $8,71 | — |
| B3 | B | Mở thêm 1 oz (tổng 2) | Không nhân bản; R3 version tăng đúng một lần | 1 oz | — | **$17,42** | — |
| B4 | B | Đóng tay lần lượt theo thứ tự ngược | Phase 6 Lớp 2: record history ≤ 500 ms, ticket trùng, giá khớp web, `closeExecutionMs` | — | 2 | — | $0,62 |
| C1 | C | Auto open 1 pair (A trên MT5 + B 1 oz) rồi chờ **close signal tự nhiên** | Executor thật: khớp 2 chân, slippage, `openExecutionMs`, Rule E (mọi close đều từ signal) | 1 oz | 1 | $8,71 | $0,31 |
| C2 | C | Partial open rollback: chân A fail (đóng terminal MT trước khi signal bắn) | `CloseOpenedLegByTimeoutAsync` đóng chân B đã mở | 1 oz | 1 | $8,71 | $0,31 |
| C3 | C | Chiều ngược: kill TRADE session lúc mở | Chân A được rollback; chân B **không** mở → không tốn phí bên cTrader | 0 | 0 | $0 | $0 |
| C4 | C | Nút "Đóng" per-pair thủ công | Rule F — đường manual per-pair đóng sạch cả hai chân | 1 oz | 1 | $8,71 | $0,31 |
| C5 | C | **Restart app khi đang có pair mở** | Recovery: khôi phục slot, decode ticket cTrader, không đóng nhầm | (dùng C4) | — | $8,71 | — |
| C6 | C | Ép reject từ broker (đặt `volumeBUnits` sai cỡ) | `Success=false` kèm lý do từ tag 58, không mở vị thế | 0 | 0 | $0 | $0 |
| | | **Tổng** | | | **11 oz** | **đỉnh $17,42** | **≈ $3,4** |

### Vốn tối thiểu

| Khoản | Số tiền | Giải thích |
|---|---|---|
| Ký quỹ đỉnh | $17,42 | 2 oz cùng lúc (ca A3, A4, B3) |
| Chi phí giao dịch | $3,40 | 11 vòng × $0,31 |
| Đệm biến động | $15 | mỗi vị thế giữ vài phút; XAUUSD dao động ~$1–3/5 phút, cực đoan ~$10 khi có tin. 1 oz lỗ $1 cho mỗi $1 giá chạy |
| Đệm chạy lại ca lỗi | $5 | làm lại 1–2 ca nếu sai thao tác |
| **Cộng** | **≈ $41** | |
| **Khuyến nghị nạp** | **$50** | làm tròn lên, dư ~$9 phòng stop-out và tin bất ngờ |

**Phương án rút gọn $25** (chỉ khi không muốn nạp $50): bỏ ca **A3** (partial close) và **B3** (2 vị thế) để không bao giờ giữ quá 1 oz → ký quỹ đỉnh $8,71 + phí $2,8 + đệm $12. Đổi lại **mất hai bằng chứng**: hành vi partial close (Phase 0 câu 5) và "2 vị thế không nhân bản". Không khuyến nghị: câu 5 là thứ quyết định Phase 6 ghi history đúng hay sai.

**Lưu ý khi nạp:** FxPro thường có mức nạp tối thiểu cho lần đầu (~$100 tuỳ phương thức) — nếu vậy thì phần dư cứ để trong tài khoản, không ảnh hưởng test. Số dư hiện tại là **$0,01**.

### Quy tắc kiểm soát chi phí trong phiên Phase 7

- `max_total_opens = 1` suốt Bước C → không bao giờ có 2 pair auto cùng lúc.
- Luôn dùng **1 oz** (0,01 lot); chỉ ca A3/A4/B3 mới lên 2 oz và đóng ngay sau khi xác nhận.
- Chạy **trong giờ XAUUSD mở**, tránh 30 phút quanh tin mạnh (NFP/CPI/FOMC) — biến động lúc đó ăn đệm rất nhanh.
- Mỗi ca xác nhận xong thì **đóng ngay**, không để vị thế chạy tiếp "cho tiện".
- Không giữ qua 03:59:45 UTC+7 (giờ nghỉ) để tránh swap và tránh vị thế treo qua lúc session reset.
- Thứ tự A → B → C là bắt buộc: nếu ca A2 cho thấy `721` **không** đóng được vị thế thì **dừng luôn**, không sang Bước C (executor sẽ không có đường đóng lệnh) — lúc đó chi phí đã tiêu chỉ ~$1.


## P7-A1 — FxPro CHẶN đặt lệnh qua FIX (2026-09-23, Bước A DỪNG)

Chạy Bước A câu 4 lúc 15:16 trên live 8220816. Lệnh gửi đúng chuẩn, sàn từ chối ở TẦNG KÊNH:

```
GỬI:  8=FIX.4.4|35=D|49=live.fxpro.8220816|50=TRADE|56=cServer|57=TRADE|11=spike-open-1|38=1|40=1|54=1|55=41|59=3
NHẬN: 8=FIX.4.4|35=j|49=cServer|50=TRADE|58=CHANNEL_IS_BLOCKED:Operations through current channel is blocked by your Broker|379=spike-open-1|380=0
```

Đã loại trừ từng nguyên nhân:

- **Không phải sai kênh**: `50=TRADE`, `57=TRADE`; chính kênh này vẫn đọc vị thế/lịch sử nhiều ngày.
- **Không phải sai định dạng**: `35=j` (BusinessMessageReject) nghĩa là message chưa tới engine khớp.
  Sai nội dung sẽ cho `35=8`/`39=8` kèm lý do cụ thể.
- **Không phải khoá tài khoản**: giao dịch tay trên cTrader Web vẫn chạy (thống kê có 3 lệnh).
- Dấu thời gian sàn (`52=…08:16:56.571`) SỚM HƠN `60=…15:16:57.141` của lệnh ⇒ từ chối tức thì.
- Phiên FIX vẫn sống sau đó (47 heartbeat trong message store), sàn không ngắt kết nối.

Tài liệu cTrader: FIX API bật mặc định trừ khi broker tắt. **Chi phí phát hiện: $0** — không vị thế nào mở.
**Hệ quả: Bước C (viết `CTraderTradeExecutor`) BỊ CHẶN trên FxPro.** Phần đọc (Phase 4/5/6) không ảnh hưởng.

## P7-A2 — Deriv 1551176: kênh đặt lệnh CÓ VẺ MỞ (chưa kết luận)

Thử cùng phép thử trên tài khoản `live.deriv.1551176` (cùng host `live.cfixapi.com`, TRADE 5212 SSL).
Logon thành công. Lệnh thăm dò gõ nhầm chuỗi giữ chỗ, và **chính lỗi đó lại là thông tin quý**:

```
NHẬN: 35=j | 45=2 | 58=Symbol(55) must be numeric. But it is <symbolId> | 379=deriv-probe-1 | 380=0
```

Deriv **đọc và kiểm tra nội dung** lệnh rồi mới báo lỗi định dạng; FxPro chặn trước khi nhìn nội dung.
Dấu hiệu mạnh là kênh Deriv không bị khoá, nhưng **chưa kết luận** — validate có thể chạy trước khi kiểm quyền.
Cần thử lại với symbolId hợp lệ.

**SecurityList Deriv (`8|sec-deriv|0`, 348 symbol, `9=10938`):** XAUUSD = **`55=41`, `1008=2`** — trùng
khít FxPro. Ghi chú kỹ thuật: spike parse trọn message 10,9 KB này KHÔNG lỗi, trong khi app chính từng
ném `UnsupportedVersion` với message 9,4 KB ⇒ củng cố kết luận P5-A1 (lỗi phụ thuộc thứ tự nạp assembly,
không phụ thuộc kích thước message).

## P7-A3 — BƯỚC A **GO** trên Deriv 1551176 (2026-09-23 16:27–16:30)

Gửi lệnh thật 1 oz XAUUSD (`38=1`, `55=41`) rồi đóng bằng `721`. Nguyên văn:

```
MỞ  35=8|39=0|150=0|37=26913373|38=1|54=1|55=41|151=1|721=623507120          ← nhan lenh
MỞ  35=8|39=2|150=F|6=4319.85|14=1|32=1|151=0|721=623507120                  ← KHOP sau 284 ms
AP  35=AP|727=1|728=0|721=623507120|730=4319.85|702=1|704=1|705=0            ← 1 vi the LONG 1 oz
ĐÓNG 35=8|39=0|150=0|37=26913435|38=1|54=2|55=41|151=1|721=623507120         ← nhan lenh dong
ĐÓNG 35=8|39=2|150=F|6=4316.30|14=1|32=1|151=0|721=623507120                 ← KHOP sau 101 ms
AP  35=AP|710=pos-final|727=0|728=2                                          ← so sach
```

**Kết luận GO/NO-GO R1: GO.** Đóng vị thế bằng tag `721` hoạt động — lệnh sell mang đúng
`721=623507120` khớp và `728=2` xác nhận không còn vị thế. **Không cần** chuyển chiều đóng sang Open API
(`ProtoOAClosePositionReq`). Thiết kế `CTraderTradeExecutor` giữ nguyên.

Xác nhận thêm từ chính phép thử: `721` CÓ trong ExecutionReport; `38=1` cho ra đúng 1 oz; `35=AP` trả đủ
`727/728/730/702/704`; tốc độ khớp 101–284 ms.

**Chi phí thật: −$3,55** (mua 4 319,85 → bán 4 316,30). Trong đó spread chỉ ~$0,3; phần lớn là do giá vàng
rơi ~3,2 USD trong **2 phút 30 giây** giữa hai lệnh — Claude bị chặn gửi lệnh đóng và phải chờ chủ dự án
cho phép. **Bài học bắt buộc áp dụng cho Bước C:** đường đóng lệnh phải chạy được ngay lập tức, không xen
bước xin phép giữa chừng; mỗi giây giữ vị thế thừa là rủi ro thật, không phải lý thuyết.

## P7-B1 — Bước B (rút gọn) trên Deriv: luồng ĐỌC vị thế + history ĐẠT (2026-09-23 17:08–17:33)

Hai vị thế thật mở tay trên cTrader Web (Buy `623650688` @4316.90; Sell `623725107` @4316.40), app chỉ
quan sát (`NullCTraderTradeExecutor` nguyên tại chỗ, KHÔNG bấm Start).

| Mục | Bằng chứng |
|---|---|
| Nhận vị thế qua `35=8` | 17:08:51 `721=623650688 54=1 32=1` → `count=0→1 version=0→1`; 17:29:06 `721=623725107 54=2` → `count=2 version=2` |
| **Khởi động lại khi đang có vị thế** | 17:25:28–30: `MapNotFound(sync=false, cache=none)` → `PositionsSynced=true 727=1 728=0 count=1` → `AVAILABLE count=1`. Tổng **1,76 s**, `count=1` ngay từ lần sync đầu, không có giai đoạn 0 |
| Thứ tự R2 | Không có khoảnh khắc nào map AVAILABLE với cache rỗng |
| Nhóm `702/704/705/730` | `727=1` lần đầu xuất hiện (trước đó 5 272 lần đều `727=0`); có cả Buy lẫn Sell nên chạy cả `704` và `705` |
| Không nhân bản | Mỗi vị thế làm `count`/`version` tăng ĐÚNG một lần |
| **History sinh bản ghi thật** | 17:31:28.675 `positionId=623650688 side=Buy open=4316.9 close=4316.84 move=-0.06`; 17:33:19.966 `positionId=623725107 side=Sell open=4316.4 close=4317.61 move=-1.21`. Sinh **cùng mili-giây** với ExecutionReport đóng, không đợi đối soát 60 s |
| Giá mở khớp broker | Bản ghi `open=4316.9` = Entry price 4316.90 trên cTrader Web |
| Dấu lãi/lỗ | Sell giá tăng → `move` âm; Buy giá giảm → `move` âm. Đúng chiều cho cả hai |
| `history_version` độc lập | `history_version=1` khi `version` của trades đã là 3 |

### KHÔNG kiểm được bằng cách mở tay — sửa lại checklist gốc

- **R4 (ticket mã hoá bit 62):** `DashboardViewModel.cs:6035` lọc `IsAppGeneratedTicket` — tab Trade **cố ý
  chỉ hiện vị thế do chính app mở**, tra bảng `_pairIdByTicket`. Vị thế mở tay không bao giờ lên UI, nên
  checklist gốc *"mở tay → app tab Trade hiện ticket mã hoá"* là **SAI về nguyên tắc**, không phải lỗi code.
  R4 chỉ kiểm được ở Bước C khi executor tự mở lệnh.
- **external-partial-close, watchdog, rollback chân A:** chỉ chạy sau khi bấm Start (logic giao dịch).
  Không bấm Start nên chúng vô hại, nhưng **chưa được chứng minh** là hành xử đúng. Phải kiểm ở Bước C.

**Ghi chú quan trọng về số liệu:** log tự nói *"Profit/Commission là số TÍNH LẠI, không phải số broker"* —
tab History của app tính lãi/lỗ từ chênh lệch giá, KHÔNG gồm commission/swap thật. Đối chiếu tiền thật phải
xem History trên cTrader Web.

## P7-B2 — (ĐÃ BỊ THAY THẾ bởi P7-B3) Nghi ngờ gap Deriv thấp hơn FxPro

> **Kết luận "CHẶN Bước C" của mục này đã bị bác bằng dữ liệu dài hơn — xem P7-B3 ngay dưới.**
> Số liệu 51 phút dưới đây vẫn đúng với cửa sổ đo của nó; cái sai là suy rộng thành kết luận chặn.

Gom toàn bộ dòng `[STATS]` ngày 2026-09-23 (mỗi phút một mẫu, là tick cuối cửa sổ):

| | FxPro (918 phút) | Deriv (51 phút) |
|---|---|---|
| Gap Buy trung vị | −23 | **−17** |
| Phân vị 25–75 | −32 … −17 | −21 … −15 |
| Biên độ | −401 … +9 | −32 … −3 |
| **\|gap\| trung bình** | **37,1 pts** | **17,7 pts** |
| Spread B trung bình | 25,2 | **16,0** |

Tỉ lệ phút đạt ngưỡng trên Deriv: `|gap|>=8` 98 %, `>=10` 96 %, `>=12` 92 %, `>=15` 82 %, `>=20` 29 %,
`>=25` 8 %, `>=30` 2 %.

**Hệ quả:** ngưỡng `open_pts` / `confirm_gap_pts` / `close_pts` / `close_confirm_gap_pts` đang đặt theo gap
FxPro. Giữ nguyên trên Deriv thì hoặc gần như không bao giờ có tín hiệu (nếu ngưỡng quanh 25–30), hoặc tín
hiệu tràn lan (nếu quanh 8–10). **Phải đo và chỉnh lại TRƯỚC khi bật `CTraderTradeExecutor`** — đây là rủi
ro tiền thật, không phải việc dọn dẹp.

Giới hạn của số liệu: mỗi phút một mẫu, Deriv mới có 51 phút trong khung giờ chiều. Cần thêm phiên Mỹ và
phiên Á để chắc. Nhưng chênh lệch ~2 lần thì đã vượt xa sai số đo.

## P7-B3 — Đo lại gap: P7-B2 KHÔNG tái lập, Bước C không bị chặn (2026-09-24)

Gom `gap_buy_pts=` từ toàn bộ `[STATS]` hiện có, thay vì 51 phút của một buổi chiều:

| | FxPro 21–22/09 (2 070 phút) | Deriv 23/09 16:27 → 24/09 10:19 (859 phút) |
|---|---|---|
| Gap Buy trung vị | −18 | **−21** |
| Phân vị 25–75 | −23 … −14 | −27 … −15 |
| **\|gap\| trung bình** | 19,6 | **24,2** |
| Spread B trung bình | 20,3 | 21,5 |
| \|gap\| ≥ 15 | 71 % | 77 % |
| \|gap\| ≥ 20 | 41 % | 54 % |

Gap Deriv **lớn hơn** FxPro một chút, không phải bằng một nửa. **Ngưỡng `open_pts` / `confirm_gap_pts` /
`close_pts` / `close_confirm_gap_pts` đang đặt theo FxPro vẫn dùng được — không cần chỉnh lại trước khi bật
executor.** Rủi ro mà P7-B2 nêu không có thật.

Hai giới hạn phải giữ trong đầu khi đọc bảng này:

- Hai cửa sổ đo **không cùng phân bố giờ trong ngày** (Deriv ~14 h liên tục qua đêm và sáng; FxPro 34,5 h
  trải hai ngày). Với vàng thì giờ phiên có ảnh hưởng, nên đây là *"không thấy chênh lệch đáng kể"*, không
  phải *"đã chứng minh hai sàn như nhau"*.
- `would_skip_latency_b` (65,5 % FxPro vs 14,6 % Deriv) **không so sánh được**: giai đoạn FxPro chạy
  `confirm_latency_ms=100`, Deriv chạy `1000`. Đừng đọc nó như cải thiện của sàn.

Bài học chung: 51 phút trong một khung giờ duy nhất không đủ để kết luận về một sàn, kể cả khi chênh lệch
quan sát được lên tới hai lần.

## Hệ quả: đổi sàn B sang Deriv

FxPro không dùng được cho Phase 7 (P7-A1). Deriv dùng được. Việc phải làm trước khi chạy Bước B/C:

- Cập nhật `sans_json.ctraderFix`: `senderCompId=live.deriv.1551176`, `username=1551176`, mật khẩu mới,
  `symbolId=41` (trùng), kiểm lại `contractSizeB` và `volumeBUnits` trên Deriv.
- **Nghiệm thu lại Phase 4/5/6 trên Deriv**: digits, contract size, tần suất tick, spread, hành vi `728`
  đều có thể khác FxPro. Code KHÔNG phải sửa (cùng giao thức FIX), nhưng số liệu soak của FxPro không
  chuyển sang được.
- Quyết định số dư: tiền $50,01 đang nằm ở FxPro, tài khoản Deriv có sẵn tiền (đủ ký quỹ 1 oz).


**Nếu Deriv cho đặt lệnh** thì việc chuyển sàn B sang Deriv KHÔNG phải việc nhỏ: phải cập nhật
`sans_json.ctraderFix`, nạp tiền vào 1551176, và nghiệm thu lại Phase 4/5/6 trên broker mới
(digits, contract size, hành vi `728`, tần suất tick đều có thể khác).

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

## P7-C0 — Môi trường VPS `win-hfa1234` ĐẠT (2026-09-24)

Kiểm bằng `docs/tools/vps-setup.ps1`. Quy trình dựng môi trường nằm ở
[phase-7-vps-run.md](phase-7-vps-run.md) mục 0.

| Mục | Kết quả |
|---|---|
| hostname | `win-hfa1234` — **khoá tạo row config trong DB**, khác laptop nên phải tạo row riêng |
| Timezone / sleep | UTC+7 sẵn có; `standby-timeout-ac` và `monitor-timeout-ac` đặt về 0 |
| FIX ra sàn | QUOTE ~333 ms, TRADE ~232 ms — **ngang laptop** (222–311 ms) |
| EA chân A | Cài lại từ `DataExporter/MQ5/`; cả ba map `_Tick` / `_Trades` / `_History` đọc được |
| App tại `C:\TradeMultiCtrader` | **chưa cài** — còn phải tải portable zip |

**VPS mua uptime, không mua latency.** Số đo FIX ngang laptop, nên đừng kỳ vọng `would_skip_latency_b`
giảm khi đổi máy. Cái được là máy không ngủ và không mất session — laptop đã bị Modern Standby cắt log
hai lần trong lúc soak.

**Sự cố EA và cách nhận ra** (ghi lại để lần sau khỏi truy lại): lần kiểm đầu thấy `MT_A_Tick` đọc được
nhưng `MT_A_Trades` và `MT_A_History` không tồn tại. Tổ hợp đó **bất khả thi với bản EA trong repo**:
`OnInit` tạo map theo thứ tự **Trades → History → Tick** và mỗi bước hỏng đều `return INIT_FAILED` trước
khi tới bước sau (`DataExporter.mq5:22-50`), nên Trades hỏng thì Tick không bao giờ được tạo. Suy ra chắc
chắn có một EA **khác** đang giữ `MT_A_Tick` — VPS chạy 3 tiến trình `terminal64`. Cài lại EA theo mục C
của runbook là hết. Hai chỗ hay hỏng: chưa bật **Allow DLL imports** (EA gọi `kernel32.dll` ở
`SharedMemoryBase.mqh:5-11`), và sót EA cũ chưa gỡ ở terminal khác.

### Còn lại trước khi bấm Start

1. Tải portable zip từ GitHub Actions (commit `50a444e` trở lên) → `C:\TradeMultiCtrader`, chạy lại script.
2. Tạo row config DB cho `win-hfa1234` (copy từ `laptop-eoj2n95d`), đặt `max_total_opens = 1`.
3. Chụp lại `manualHwndColumns` trên VPS, giữ đúng cặp `cN → tN` (Rule G).
4. Tắt app ở laptop (hai phiên cùng `SenderCompID` sẽ đá nhau), rồi kiểm mục G của runbook.

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

**Nghiệm thu Bước C — live **Deriv 1551176** (KHÔNG phải FxPro 8220816 — sàn đó chặn đặt lệnh qua FIX, xem P7-A1), `max_total_opens = 1`, tối thiểu một phiên đầy đủ.** Tiền đề: Bước A
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
