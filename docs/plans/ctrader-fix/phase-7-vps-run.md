# Phase 7 Bước C — chạy nghiệm thu trên VPS và gửi log về đối chiếu

Chủ dự án chạy trên VPS (máy không ngủ, mạng ổn định hơn laptop), Claude đối chiếu bằng log.
Tài liệu này là thứ duy nhất cần đọc trước khi bấm Start.

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
