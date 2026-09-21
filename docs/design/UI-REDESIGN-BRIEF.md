# Brief thiết kế lại giao diện TradeDesktop (màn hình chính, Config, Trading Logs)

## 1. Bối cảnh
TradeDesktop là ứng dụng desktop Windows (WPF, .NET 8) theo dõi và tự động giao dịch cặp lệnh trên 2 sàn
(Sàn A, Sàn B). Người dùng là trader/vận hành, nhìn màn hình liên tục nhiều giờ, cần đọc nhanh:
giá hai sàn, chênh lệch (gap), trạng thái hệ thống, các khóa thời gian đang chặn lệnh, và lãi/lỗ từng cặp lệnh.

Yêu cầu: **làm lại màu sắc và bố cục** cho rõ ràng, hiện đại, dễ quét mắt. Kết quả sẽ được dựng lại bằng WPF.

Phạm vi gồm 3 cửa sổ dùng chung một bộ màu, font và thành phần: **màn hình chính** (mục 3, 5.1–5.5),
**cửa sổ Config** để nhập cấu hình theo máy (mục 5.6), và **cửa sổ Trading Logs** xem log realtime (mục 5.7).

## 2. Ràng buộc bắt buộc (QUAN TRỌNG NHẤT)
Thiết kế chỉ được thay đổi **cách trình bày**. Không được thay đổi hành vi của ứng dụng.
1. **Không thêm, bớt hay đổi ý nghĩa dữ liệu.** Chỉ dùng đúng các trường liệt kê ở mục 5. Không thêm số liệu
   mới, không thêm nút có chức năng mới, không gộp hai giá trị thành một phép tính mới.
2. **Không thêm, bớt nút hay thay đổi hành vi nút.** Giữ đủ các nút và công tắc ở mục 5. Được đổi màu, kích
   thước, vị trí, icon; không được đổi chức năng. Không thêm hộp xác nhận (ví dụ nút Close theo cặp phải bấm
   là đóng ngay như hiện tại).
3. **Giữ điều kiện ẩn/hiện.** Phần tử nào đang có điều kiện hiện (mục 5, cột "Hiện khi") thì vẫn theo đúng điều
   kiện đó.
4. **Giữ ý nghĩa màu trạng thái:**
   - Đỏ = đang chặn / lỗi / cảnh báo.
   - Xanh lá = đang chạy (Trading Logic RUNNING).
   - Xám = dừng / rảnh / phụ / đang tải.
   - Cam = trống / cần chú ý (không phải lỗi).
   Được chọn sắc độ mới, nhưng không đảo nghĩa (ví dụ không dùng xanh cho trạng thái CHẶN).
5. **Nút Close từng cặp phải là bảng riêng**, tách khỏi bảng Profit (kỹ thuật: tránh lỗi mất click khi bảng
   profit cập nhật liên tục). Có thể đặt cạnh nhau cho liền mạch về thị giác.
6. **Bảng dữ liệu cập nhật liên tục (5 lần/giây).** Tránh hiệu ứng động/animation trên từng ô, tránh nền
   nhấp nháy. Số dùng font đơn cách (monospace) để không nhảy cột.
7. **Chỉ giao diện sáng (light).** Không cần dark mode ở đợt này.
8. Chỉ dùng thành phần phổ biến dựng được bằng WPF thuần: khung bo góc, bảng (DataGrid), nút, công tắc bật/tắt,
   tab, chữ. Không dùng biểu đồ, không dùng thư viện UI bên ngoài.
9. **Config:** giữ nguyên thứ tự nhập, các ô nhập tự do (không đổi thành dropdown), nút Save chỉ bật khi
   hợp lệ. **Ô Password không bao giờ hiện mật khẩu đã lưu**; để trống khi Save = giữ mật khẩu cũ,
   chỉ có dòng chữ trạng thái bên cạnh cho biết đã có mật khẩu hay chưa.
10. **Trading Logs:** hai danh sách log cập nhật realtime rất nhanh, **không** animation từng dòng. Giữ bộ lọc
    và nút "Về log mới". Màu dòng log mang nghĩa (mục 6), không đổi nghĩa.

## 3. Bố cục mới mong muốn
Kích thước mục tiêu: 1920×1080 (chính) và 1440×900 (tối thiểu).

```
┌─ Header ──────────────────────────────────────────────────────────────────────────────────┐
│ Dashboard  host · version        [Start] [Stop] [Trade ops ▢] [Copy Host] [Reconnect] [Open Log] [Config] │
│ (dòng DB inline — chỉ một số máy) (dòng lỗi config — khi có lỗi)                         │
└───────────────────────────────────────────────────────────────────────────────────────────┘
┌─ Thông số (bảng ngang: mỗi sàn 1 hàng, mỗi thông số 1 cột) ───────────────────────────────┐
│ Sàn    Symbol  Bid      Ask      Spread  Latency(ms) TPS  Time      Max Lat  Avg Lat     │
│ Sàn A  XAUUSD  2650.12  2650.36  24      12          18   12:01:05  45       11          │
│ Sàn B  XAUUSD  2650.40  2650.64  24      30          9    12:01:05  80       28          │
│ GAP BUY 12   GAP SELL -8                                  [BUY] [SELL] [CLOSE] (ẩn mặc định) │
│ (cảnh báo manual — khi có)                                                                │
└───────────────────────────────────────────────────────────────────────────────────────────┘
┌─ Trading Signal ─────────────────────────────── Open Gap Buy (công tắc)  Open Gap Sell (công tắc) ┐
│ ┌ Trạng thái (cột trái) ───────────────┐ ┌ Random Locks (cột phải) ────────────────────────┐ │
│ │ Trading Logic Status: RUNNING        │ │ Lock | Khoảng | Đã chọn | Còn | Trạng thái        │ │
│ │ Last Detected Signal                 │ │ 5 dòng + "Last Auto dispatch"                   │ │
│ │ Physical A / Physical B              │ ├ Signal Cycles ──────────────────────────────────┤ │
│ │ Managed Pairs / Close Monitoring     │ │ danh sách động: tên | tiến độ | trạng thái      │ │
│ │ System State / Position Sync         │ │ + dòng chi tiết nhỏ                             │ │
│ │ Random Quota ........ [Random lại]   │ │                                                 │ │
│ └──────────────────────────────────────┘ └─────────────────────────────────────────────────┘ │
└───────────────────────────────────────────────────────────────────────────────────────────┘
┌─ Tab: [Trade] [History] ──────────────────────────────────────────────────────────────────┐
│ Trade:  [Bảng lệnh Sàn A] [Bảng lệnh Sàn B] [Profit Realtime (A+B)] [Close]               │
│ History:[Bảng lịch sử Sàn A] [Bảng lịch sử Sàn B]                                          │
└───────────────────────────────────────────────────────────────────────────────────────────┘
```
Được phép đề xuất tỉ lệ, khoảng cách, nhóm lại khác đi, miễn giữ đủ phần tử và ràng buộc ở mục 2.

## 4. Nguyên tắc thị giác mong muốn
- Mắt nhìn nhanh thấy ngay: **hệ thống có đang chạy không**, **có gì đang chặn lệnh không**, **lãi/lỗ từng cặp**.
- Số liệu: font monospace, căn phải hoặc căn giữa cố định.
- Trạng thái dạng "nhãn" (badge/pill) cho: RUNNING/STOPPED, CHẶN/RẢNH/TẮT, STABLE/COLLECTING...
- Profit dương/âm có thể phân biệt bằng màu chữ (xanh lá / đỏ); không đổi nền cả hàng.
- Mật độ thông tin cao nhưng thoáng: ưu tiên đường kẻ mảnh, nền phân vùng nhạt.

## 5. Danh sách đầy đủ phần tử (giữ nguyên, chỉ đổi trình bày)

### 5.1 Header
| Phần tử | Loại | Nội dung / hành vi | Hiện khi |
|---|---|---|---|
| Tiêu đề "Dashboard" | chữ | tĩnh | luôn |
| Host máy | chữ | tên máy | luôn |
| Version | chữ | số build | luôn |
| DB inline | chữ đỏ, dài 1 dòng | dump cấu hình DB | chỉ vài máy chỉ định |
| Lỗi config | chữ đỏ | thông báo lỗi | khi có lỗi |
| Start | nút | bắt đầu giao dịch tự động | luôn |
| Stop | nút | dừng giao dịch tự động | luôn |
| Trade operations | công tắc có chữ trạng thái ("Cho phép lệnh" / "Đã chặn lệnh") | tắt = chặn mọi mở/đóng lệnh (kill-switch); trạng thái tắt cần nổi bật như cảnh báo | chỉ khi đang chạy (sau Start) |
| Copy Host | nút | sao chép tên máy | luôn |
| Reconnect | nút | nạp lại cấu hình | luôn |
| Open Log | nút | mở cửa sổ log | luôn |
| Config | nút | mở cửa sổ cấu hình | theo quyền hiển thị config |

### 5.2 Thông số
- 9 thông số × 2 sàn: Symbol, Bid, Ask, Spread, Latency(ms), TPS, Time, Max Lat(ms), Avg Lat(ms).
  Tên sàn ở đầu hàng là động (ví dụ "Sàn A (MT5)").
- GAP BUY, GAP SELL: số nguyên, có thể âm, cập nhật mỗi tick.
- Nút BUY (đỏ), SELL (xanh lá), CLOSE (xám): **mặc định ẩn**, chỉ hiện khi bật chế độ nút tay.
- Dòng cảnh báo manual: chữ, hiện khi có cảnh báo.

### 5.3 Trading Signal
- 2 công tắc: Open Gap Buy, Open Gap Sell (bật/tắt).
- Các dòng "Nhãn: giá trị" (giá trị là chuỗi do hệ thống sinh, độ dài thay đổi, có thể xuống dòng):
  Trading Logic Status (màu: xanh lá khi RUNNING, xám khi dừng), Last Detected Signal, Physical A, Physical B,
  Managed Pairs, Close Monitoring, System State, Position Sync, Random Quota (+ nút "Random lại").
- **Random Locks** (bảng cố định 5 dòng + 1 dòng chú thích):
  cột Lock | Khoảng | Đã chọn | Còn | Trạng thái.
  - Dòng: Same Open, Same Close, Post-close, Opposite, Global cd.
  - Trạng thái: `CHẶN …` (nổi bật đỏ), `RẢNH`, `TẮT` (xám). Khoảng có thể là `OFF`.
  - Dòng chú thích: "Last Auto dispatch: OPEN BUY lúc 12:01:05".
- **Signal Cycles** (danh sách động, 0–20 dòng): mỗi dòng gồm tên, tiến độ dạng `3/5`, trạng thái chữ,
  và một dòng chi tiết nhỏ bên dưới (có tooltip).

### 5.4 Tab Trade
- Bảng lệnh Sàn A và Sàn B (cùng cấu trúc, cập nhật liên tục). Cột: STT, Open EA Time Local, PairId, Ticket,
  Profit, Symbol, Type, Volume, Slippage, Open execution, Profit ($), Price, Timestamp, Count, SL, TP, Time.
  Phía trên mỗi bảng là **thanh tiêu đề panel**: bên trái tên panel (ví dụ "Sàn A · MAP_A"), bên phải một
  **dòng trạng thái** (chuỗi do hệ thống sinh) đổi màu theo 4 trạng thái: đang tải (xám), trống (cam),
  có dữ liệu (xanh lá), lỗi (đỏ). Chỉ một trạng thái tại một thời điểm.
- **Profit Realtime (A + B)**: cột STT, Profit realtime, Holding, Post-open, HWND.
  Holding/Post-open dạng `60s còn 12s` (đang chờ) / `60s ✓` (đã xong) / `-`.
- **Close**: bảng riêng, mỗi cặp một nút đỏ "Close(n)", cùng thứ tự dòng với bảng Profit.

### 5.5 Tab History
- Cùng thanh tiêu đề panel + dòng trạng thái 4 màu như tab Trade.
- Bảng lịch sử Sàn A và Sàn B. Cột: STT, Close EA Time Local, PairId, Ticket, Profit, Symbol, Type, Volume,
  Open Slippage, Close Slippage, Open execution, Close execution, Profit ($), Open Price, Close Price, Timestamp,
  Count, Commission, SL, TP, Open Time, Close Time.

### 5.6 Cửa sổ Config (cấu hình theo máy, mở từ nút "Config")
| Nhóm | Phần tử | Ghi chú |
|---|---|---|
| Sàn A | Chọn nền tảng: MT4 / MT5 (radio) | |
| | Map Name (ô nhập) + nút **Check** + chữ trạng thái kiểm tra | Trạng thái: "Chưa kiểm tra" / hợp lệ / lỗi |
| | CHART HWND A: **danh sách nhiều cột** (mỗi cột có số thứ tự + ô nhập) + nút **add** / **delete** | Cột số N của A **đi cặp** với cột số N của CHART HWND B (một bộ HWND). add/delete tác động cả A lẫn B; delete xoá cột cuối, mờ khi chỉ còn 1 cột. Thường tối đa 6 cột; nhiều hơn thì cuộn ngang, **không xuống dòng**, để cột A và B luôn thẳng hàng theo số thứ tự |
| | TRADE HWND A (ô nhập) | |
| Sàn B | Chọn nền tảng: MT4 / MT5 / **cTrader** (radio) | |
| Sàn B = MT4/MT5 | Map Name + Check + trạng thái, CHART HWND B, TRADE HWND B | CHART HWND B là danh sách cột thẳng hàng với CHART HWND A (cùng số thứ tự, không có nút add/delete riêng); TRADE HWND B một ô |
| Sàn B = cTrader | Kênh B (chữ, không sửa, tên kênh cố định) | ẩn toàn bộ phần MT ở trên |
| | FIX QUOTE: chữ trạng thái session + nút **Kiểm tra** | |
| | QUOTE: Host name, Port (SSL), Port (Plain text) | 3 ô |
| | TRADE: Host name, Port (SSL), Port (Plain text) | 3 ô |
| | CHUNG: Kết nối "Dùng SSL" (checkbox), SenderCompID + nhãn cảnh báo **⚠ Tài khoản LIVE** (chỉ hiện khi là tài khoản live), TargetCompID, Username (553) (chỉ đọc, tự suy), Password (ô mật khẩu) + chữ trạng thái mật khẩu | cảnh báo LIVE phải nổi bật |
| | FIX symbol ID (ô) + tên symbol (chữ), Volume B (units) + gợi ý lot (chữ), Contract size B, Volume A (lot) + chú thích "Chỉ khai báo để cảnh báo hedge ratio, không cưỡng chế chân A" | |
| Chân trang | Dòng trạng thái tải (có dấu ✔ khi tải xong), dòng lỗi (đỏ, khi có), nút **Save** | |

### 5.7 Cửa sổ Trading Logs (log realtime, mở từ nút "Open Log")
| Vùng | Phần tử | Ghi chú |
|---|---|---|
| Header | Tiêu đề "Trading Logs"; nút **Current Log**, **Log Folder**, **Close** | |
| Cột trái: MINIMAL SIGNAL LOGS | Bộ lọc (dropdown): All / Open / Hedge; nút **Về log mới** | nút chỉ hiện khi người dùng cuộn lên xem log cũ |
| | Danh sách dòng log (chữ đơn cách, mỗi dòng 1 chuỗi, màu theo kết quả: phát hiện xanh dương, xác nhận xanh lá, bị chặn cam, thất bại đỏ) | mới nhất ở trên, tối đa ~2000 dòng |
| | Chân: "Signal logs: realtime" | |
| Cột phải: SYSTEM / EXECUTION LOGS | 2 bộ lọc: Nhóm (All / Signal / Trading / Execution / Recovery / Market / Connection / Application) và Mức độ (All / Info / Warn / Error); nút **Về log mới** | |
| | Danh sách dòng log (màu mặc định chữ đậm; dòng có kết quả dùng cùng bảng màu bên trái) | |
| | Chân: "System: N lines · N warnings · N errors" | chuỗi do hệ thống sinh |
| Cửa sổ | Thu nhỏ / phóng to / đóng; hai cột có thanh chia | cửa sổ kéo giãn được |

## 6. Màu hiện tại (tham khảo, được phép thay)
- Nền thẻ trắng `#FFFFFF`, viền `#E4E7EC`, nền vùng phụ `#FAFBFC`, đường kẻ `#EEF1F5`.
- Chữ: nhãn `#344054`, giá trị `#101828`, phụ `#667085` / `#98A2B3`, header bảng `#475467`.
- Đỏ lỗi/chặn `#B42318`, nút đỏ `#D92D20`, nút xanh `#12B76A`, nút xám `#667085`, RUNNING xanh lá, dừng xám.
- Dòng trạng thái panel lệnh: có dữ liệu `#067647`, trống `#B54708`, đang tải `#475467`, lỗi `#B42318`.
- Màu dòng log (Trading Logs): phát hiện `#175CD3`, xác nhận `#067647`, bị chặn `#B54708`, thất bại `#B42318`,
  mặc định `#101828`.
- Font dữ liệu: Consolas. Tiêu đề 18–24 px, SemiBold.

## 7. Cần Claude Design gửi lại
1. **Mockup** màn hình chính ở 1920×1080 và 1440×900, cả tab Trade và tab History; cửa sổ Config ở 2 chế độ
   Sàn B (MT5, và cTrader có cảnh báo LIVE); cửa sổ Trading Logs có dữ liệu mẫu và nút "Về log mới".
2. **Bảng màu (design tokens)** dạng hex, có tên: nền, bề mặt, viền, chữ (chính/phụ/mờ), trạng thái
   (chạy / chặn / rảnh / tắt / lỗi / cảnh báo), profit dương/âm, màu nút (thường / hover / nhấn / vô hiệu).
3. **Typography**: font, cỡ, độ đậm cho tiêu đề, nhãn, giá trị, header bảng, ô bảng.
4. **Khoảng cách & bo góc**: padding thẻ, khoảng giữa khối, bo góc, độ dày viền.
5. **Spec thành phần**: nút, công tắc, tab, bảng (header, hàng, hàng chẵn/lẻ nếu có, hàng chọn), nhãn trạng
   thái (badge), thẻ/khung.
6. Ghi chú những chỗ **cố ý khác** với bố cục ở mục 3 và lý do.

## 8. Ngoài phạm vi đợt này
Dark mode, icon/logo mới, biểu đồ, các popup cảnh báo/hộp thoại hệ thống.
