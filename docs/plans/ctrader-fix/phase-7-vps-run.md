# Phase 7 Bước C — chạy nghiệm thu trên VPS và gửi log về đối chiếu

Chủ dự án chạy trên VPS (máy không ngủ, mạng ổn định hơn laptop), Claude đối chiếu bằng log.
Tài liệu này là thứ duy nhất cần đọc trước khi bấm Start.

## 0. Dựng môi trường trên VPS

Phần này là chỗ dễ hỏng nhất vì đổi máy. Bốn khẳng định dưới đây đều đã kiểm trong code, không phải phỏng đoán.

### A. Lấy bản build

Dùng **portable zip** từ GitHub Actions (`TradeMulti-<ver>-portable-win-x64.zip`), đừng dùng single-file exe:
mọi thứ nằm rời nên kiểm được bằng mắt, không phụ thuộc cơ chế tự giải nén.

Giải nén xong **phải thấy đủ ba file**:

| File | Thiếu thì sao |
|---|---|
| `TradeMulti.exe` | — |
| `FIX44-CSERVER.xml` | Sàn B chết câm: dictionary được resolve bằng `Path.Combine(AppContext.BaseDirectory, …)` (`CTraderQuoteSession.cs:130`) |
| `mt5engine_capi.dll` | Chân A không click được lệnh nào |

Build phải từ commit **`6551fb3`** trở lên — bản đầu tiên có executor thật và dòng `[BUILD]` trong log.

### B. Cấu hình Windows

- **Đặt timezone = UTC+7.** Mọi mốc giờ trong hệ thống là **giờ local**: `start_time_hold`/`end_time_hold`,
  giờ nghỉ sàn 03:59:45–05:00, timestamp log. VPS mặc định thường là UTC ⇒ lệch 7 tiếng, cấu hình giờ sai
  hoàn toàn và log đọc không khớp với những gì đã ghi nhận từ trước.
- Tắt ngủ: `powercfg /change standby-timeout-ac 0` và `powercfg /change monitor-timeout-ac 0`.
- **Ngắt RDP thì được, ĐĂNG XUẤT thì không.** Engine click của MT dùng `PostMessage`/`SendMessage` với
  `WM_LBUTTON` gửi thẳng tới handle cửa sổ (`native/mt5engine-capi`), **không** dùng `SetCursorPos` hay
  `SendInput`, nên không cần desktop ở foreground. Nhưng sign out là huỷ session ⇒ mất hết cửa sổ.
  Thoát bằng nút **X** của cửa sổ Remote Desktop.
- Mở đường ra: `live.cfixapi.com:5211` và `:5212` (FIX), HTTPS tới Supabase, Telegram nếu dùng.

### C. MT5 chân A

- Cài MT5, đăng nhập tài khoản **demo**, bật **AutoTrading**.
- Gắn EA `DataExporter` — bản VPS nằm ở `DataExporter/VPS/MQ5/` (thư mục này cố ý không theo git).
- Map name khớp config: `Local\MT_A_Tick`; app tự suy ra `_Trades` và `_History` từ đó.
- Panel one-click đặt lot **0,01** để cân với `volumeBUnits = 1` (1 oz). Lệch là hai chân không đối xứng,
  và `[HEDGE_VOLUME]` sẽ cảnh báo ngay khi bấm Start.

### D. Config trong DB

App nạp config **theo hostname máy** (`Environment.MachineName`, viết thường —
`MachineIdentityService.cs`). VPS có hostname khác laptop nên **phải tạo row riêng**, nếu không app báo
`"Không có config cho host name: …"` và không chạy được.

Cách làm: copy row của `laptop-eoj2n95d`, đổi `hostname` thành hostname VPS, rồi sửa ngay trong row mới:

- `max_total_opens = 1` (đang là 3 — với 3 thì lần đầu executor chạy có thể mở 3 pair tiền thật cùng lúc)
- giữ `platform_b = ctrader`, `senderCompId = live.deriv.1551176`, `volumeBUnits = 1`, `contractSizeB = 100`

### E. Chụp lại HWND trên VPS

`manualHwndColumns` trong config đang chứa handle cửa sổ **của laptop** (`0x00010CCE`…). Handle là số định
danh cửa sổ của một tiến trình cụ thể — vô nghĩa trên máy khác, và đổi cả khi mở lại terminal trên cùng máy.
Phải lấy lại toàn bộ trên VPS, giữ đúng cặp `cN → tN` (Rule G cấm ghép chéo chart của cột này với trade panel
của cột kia).

### F. Chỉ MỘT máy được chạy tại một thời điểm

Tắt app ở laptop trước khi bật trên VPS. Hai tiến trình cùng `SenderCompID = live.deriv.1551176` tạo hai phiên
FIX trùng danh tính; sàn có thể đá phiên này để nhận phiên kia, gây logout lặp — đúng triệu chứng đã mất nhiều
thời gian truy ở P5-O1 nhưng lần này do mình tự gây ra.

### G. Kiểm trước khi bấm Start

Mở `Desktop\trade-log\{yyyyMMdd}-ctrader.log`, phải thấy đúng thứ tự này:

```
connecting ... sender=live.deriv.1551176
QUOTE logged on  +  TRADE logged on
symbolName=XAUUSD symbolId=41 digits=2        (CẢ HAI session)
PositionsSynced=true 727=0 728=2
trades map AVAILABLE  /  history map AVAILABLE
Cross-check độ lớn giá: ... ratio=1.0000
```

Sai bất kỳ bước nào thì **dừng, đừng bấm Start** — sai ở đây nghĩa là sàn B chưa sẵn sàng.

Sau khi bấm Start, log phiên phải có hai dòng:

- `[BUILD] version=…` — xác nhận đang chạy đúng bản build nào (để sau này đối chiếu log với commit)
- `[HEDGE_VOLUME][INFO] …` — xác nhận khối lượng hai chân cân. Nếu là `WARN` hoặc `ERROR` thì **dừng lại**:
  profit tính ra sẽ không bám tiền thật và Rule D sẽ quyết định sai.

---

## 1. Kiểm tra TRƯỚC khi bấm Start

| # | Việc | Vì sao bắt buộc |
|---|---|---|
| 1 | `max_total_opens = 1` trong DB (hiện **3**) | Với 3, lần đầu executor chạy có thể mở **3 pair tiền thật** cùng lúc trước khi ai kịp xác nhận một vòng đời đúng |
| 2 | `platform_b = ctrader`, `senderCompId = live.deriv.1551176` | Sàn B phải là Deriv — FxPro chặn đặt lệnh qua FIX (P7-A1) |
| 3 | `volumeBUnits = 1`, `contractSizeB = 100` | 1 oz = 0,01 lot. Sai đơn vị = sai khối lượng gấp 100 lần (R5) |
| 4 | Lot one-click của MT5 chân A = **0,01** | Hai chân phải cân; lệch là pair không đối xứng |
| 5 | Terminal MT5 + EA chạy, ghi được `Local\MT_A_Trades` | Không có chân A thì không có pair nào |
| 6 | HWND profile trong Config đúng cặp `cN → tN` | Rule G: cấm ghép chéo chart/trade |
| 7 | Tài khoản Deriv còn tiền cho ~8 vòng lệnh | Ước tính $2–4 tổng (chân A demo nên không tốn) |

**Nếu muốn có tín hiệu nhanh:** đặt tạm `open_pts = -25` (gap đang quanh −21 nên sẽ kích hoạt gần như liên
tục; CLAUDE.md cho phép ngưỡng âm). **Chạy xong PHẢI trả về `1`** — quên là hệ thống vào lệnh loạn.

## 2. Trong lúc chạy

- Bấm **Start** để bật logic giao dịch. Không bấm các nút Open/Close thủ công trừ khi đang làm đúng ca đó.
- Để chạy cho tới khi có **ít nhất một pair mở và đóng trọn vẹn bằng auto signal**.
- Các ca recovery làm sau, mỗi ca một lần, ghi lại giờ bắt đầu để dễ tra log:

| Ca | Thao tác |
|---|---|
| Rollback chân A hỏng | Đóng terminal MT5 trước khi signal bắn → chân B phải được đóng lại bằng `CloseOpenedLegByTimeoutAsync` |
| Rollback chân B hỏng | Ngắt mạng/kill TRADE session lúc đang mở → chân A phải rollback |
| Nút "Đóng" per-pair | Bấm nút Đóng của pair có chân cTrader |
| Restart khi đang có pair | Đóng app bằng nút X rồi mở lại, pair phải được khôi phục và vẫn đóng được |
| Ép reject | Đặt `volumeBUnits` sai cỡ (vd. `0.003`) → lệnh phải bị từ chối với lý do rõ, chân A rollback |

## 3. Gửi về những file này

Trong `Desktop\trade-log\` (hoặc thư mục log tương ứng trên VPS):

1. `{yyyyMMdd}-ctrader.log` — phiên FIX sàn B, chứa `[ORDER][SENT]` / `[ORDER][RESULT]`
2. `{yyyyMMdd_HHmmss}-trade-log.log` — log phiên, chứa `[VM]` `[ROUTER]` `[CYCLE]` `[SLOT]`
3. `{yyyyMMdd_HHmmss}-signal-outcome.log` — để đối chiếu tín hiệu với lệnh thực tế

Kèm theo, ghi vắn tắt: **giờ bắt đầu mỗi ca recovery**, và **ảnh tab Positions + History trên cTrader Web
lúc kết thúc** (phải trống — không còn vị thế mồ côi).

## 4. Claude đối chiếu bằng

```
python docs/tools/phase7-acceptance-report.py <thư-mục-chứa-log>
```

Script in ra từng mục của checklist kèm bằng chứng. Mục nào ghi `THIEU` nghĩa là **không tìm thấy bằng
chứng trong log** — coi như chưa nghiệm thu, không suy diễn thành đạt.

## 5. Dừng khẩn cấp

Đổi `App.xaml.cs` về `NullCTraderTradeExecutor` rồi build lại — app mất hoàn toàn khả năng đặt lệnh sàn B
trong khi mọi chức năng đọc vẫn chạy. Hoặc nhanh hơn: đổi `platform_b` sang `mt5` trong Config.
