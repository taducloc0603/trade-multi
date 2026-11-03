# Phase 8 — Runbook bật sàn C "Fast Leg" trên host live (chủ dự án TỰ làm)

> CLAUDE.md §0.0: AI **không** đổi config, deploy hay restart trên host/VPS thật. Tài liệu này để chủ dự án tự thực hiện.
> Sàn C không bao giờ đặt lệnh, nhưng ở `mode=filter` nó **đổi quyết định giao dịch** của A/B (chặn Open, hoãn Close/TP),
> nên mọi bước dưới đây là thao tác trên hệ thống thật.

## 0. Điều kiện trước

- MASTER-PLAN §1: Phase 1–7 ✅ và Phase 6 (nghiệm thu demo + soak 1 h) ✅. Build đã qua `recheck.ps1 -Phase 8 -Exit`
  (A1–A8 PASS). Ghi lại commit.
- Có tài khoản cho terminal C. Được là tài khoản THẬT vì C chỉ đọc giá; ưu tiên broker có feed **dày** (nhiều tick/giây).
  Feed thưa (vd Exness ~1–2 lần đổi giá/s) làm Stale cao ⇒ chặn Open nhiều.
- Biết rõ bản chất: ngưỡng Long (`maxMovePts=5`) đo trên cặp THINK–GTC. Với feed khác, cấu hình đó có thể chặn Open
  ~1/3 thời gian (đo 2026-11-03 với Exness). **Không bật `filter` trước khi chạy `observe` và đọc báo cáo ở bước 3.**
- Số đo nghiệm thu demo (Phase 6, 2026-11-03, C = Exness, `filter` theo thông số Long, 1.5 h, 36 cặp):
  - Open bị chặn **30 %** thời gian.
  - Open bị hoãn p50 ~160 ms, tối đa ~1 s. Close thường bị hoãn 5 lần, mỗi lần 95–455 ms.
  - Router chặn lại 1/37 lần (`FAST_LEG_STALE`, tick C vừa quá 1000 ms) ⇒ mất signal đó.
  - RAM/thread/handle không tăng; 0 ERROR.
  - Chưa đo được sàn C có giảm trượt hay không: A/B demo cùng broker nên gap gần như không đổi.
- **Telegram có thể KHÔNG gửi gì.** Trong repo, `TelegramNotifier` để trống `BotToken`/`ChatId` (no-op cho MỌI cảnh báo
  của app, không riêng sàn C). Nếu bản build trên VPS chưa điền token thì đừng trông vào Telegram ở bước 5: theo dõi
  panel Signal của app / `trade-log` (các dòng `[FAST_LEG][ERROR] FAST_LEG_*` vẫn được ghi).

## 1. Cài terminal C trên VPS

1. Cài một MT5 **riêng** (thư mục riêng, vd `C:\Program Files\MetaTrader 5 C`). Đăng nhập tài khoản C.
   Đọc tiêu đề cửa sổ để chắc đang ở đúng tài khoản/server.
2. Cài EA `DataExporter` theo `docs/plans/ctrader-fix/phase-7-vps-run.md` §C (C2 chép đủ 7 file, C4 compile 0 error), với
   các khác biệt sau:
   - **`EA_CHANNEL_ID = C`**. ⚠ Đặt nhầm `A` hoặc `B` sẽ **ghi đè giá của chân đang giao dịch**, vì `CreateFileMappingW`
     mở lại map đã có. Kiểm lại hộp Inputs trước khi bấm OK.
   - Gắn vào chart vàng của broker C (tên symbol thật của broker, vd `XAUUSD`, `XAUUSD.lmx`).
   - Tools → Options → Expert Advisors: bật **Allow DLL imports**; **tắt** "Allow algorithmic trading" / AutoTrading.
     Laptop dev đã chạy đúng cấu hình này (`AllowLiveTrading=0`, `Enabled=0`, `AllowDllImport=1`) và EA vẫn ghi map đầy đủ.
     Như vậy terminal C không có cách nào gửi lệnh.
3. Terminal C phải chạy **cùng Windows session** với app (map `Local\`). Không chạy qua dịch vụ hay user khác.
4. Kiểm bằng chứng:
   - Tab Experts của terminal C: `[OK] Khởi động thành công. Channel ID: C`.
   - Map tick `Local\MT_C_Tick` đọc được: nút **Check** ở bước 2. Heartbeat `Local\MT_C_Trades` kiểm qua dòng
     `start verdict: … hb=alive` ở bước 2.3 (`hb=n/a` = không thấy map heartbeat ⇒ không tách được Down khỏi Stale).
     Máy có repo thì chạy thêm `recheck.ps1 -Phase 2 -SkipBuild` và xem dòng **A7** (kiểm cả hai map).
   - Giá A/B trên app KHÔNG đổi sau khi gắn EA C. Nếu đổi thì gỡ EA C ngay (khả năng gắn nhầm kênh).
5. Khi thao tác trên VPS, **đừng đụng cửa sổ MT5 của A/B** (bài học Phase 6):
   - Đóng chart của A/B là gỡ luôn EA DataExporter trên chart đó ⇒ giá đứng ⇒ guard price-freeze chặn mọi Open.
   - Toolbox của A/B phải để ở tab **Trade**. Nếu chuyển sang tab khác (vd Experts), lệnh đóng bằng click vẫn báo
     thành công nhưng lệnh KHÔNG đóng ⇒ pending-close thử lại liên tục.
   - Mở lại MT5 sẽ đổi HWND ⇒ phải cập nhật `manualHwndColumns` trước khi Start.

## 2. Migration + cấu hình (chế độ quan sát)

1. Supabase SQL editor: chạy phần `begin … commit` của `docs/FAST-LEG-MIGRATION.sql`. Cột thêm vào là `null`, nên host nào
   chưa cấu hình vẫn chạy y như cũ. Bản app cũ bỏ qua cột này.
2. Trên app của host: **Config** ⇒ tick **Bật sàn C (Fast Leg)** ⇒ nhập:
   - Map `Local\MT_C_Tick`; để trống **Symbol** rồi bấm **Check** ⇒ app tự điền symbol EA đang xuất. Đọc lại cho đúng mã vàng.
   - **Chế độ: Observe** (chỉ ghi dữ liệu, không chặn gì).
   - Max move 5, Window 200, Max age 1000, Max defer 3000, **Fail open** tắt, **Áp dụng cho Close thường / TP** bật
     (thông số Long). Ở observe, các ngưỡng này chỉ để tính verdict ghi log.
   ⇒ **Save**. Nếu thông báo là "Sàn A/B đã lưu, Sàn C lưu thất bại: …" thì sàn C CHƯA được lưu: đọc lỗi, sửa, Save lại.
3. Bấm **Start** như thường lệ. Trong `Desktop\trade-log\<phiên>-trade-log.log` phải có:
   - `[FAST_LEG][INFO] config=observe|quiet|move5|win200|age1000|defer3000|failOpen0|exit1|…|sym=<symbol>`
   - `[FAST_LEG][INFO] start verdict: verdict=Confirmed … hb=alive`
   - KHÔNG có `[FAST_LEG][WARN] san C trung symbol va gia voi san …`. Nếu có thì kiểm kênh EA (bước 1.2).
   - Dải **Sàn C** dưới panel A/B có giá và tuổi tick hợp lý.

## 3. Quan sát 1–2 ngày rồi chọn ngưỡng

1. Để chạy `observe` qua cả phiên Á, Âu và Mỹ. Gửi về (hoặc tự chạy) 3 file của mỗi phiên: `-gap-tick.log`,
   `-trade-log.log`, `-signal-outcome.log`.
2. Chạy báo cáo:

   ```
   python -I docs\plans\fast-leg\tools\fast-leg-report.py <thư mục log>\<prefix phiên 1> <prefix phiên 2> ... --out report-<ngày>
   ```

3. Đọc `summary.md`:
   - **Mục 1:** `stale_hb_alive_pct` cao (> 10 %) ⇒ feed C thưa. Cân nhắc nâng `maxAgeMs` lên 1500–2500 hoặc đổi broker C.
   - **Mục 2:** % thời gian bị chặn Open ở từng cặp (max_move, max_age). Chọn mức chặn chấp nhận được (vd ≤ 20 %).
   - **Mục 3–4:** đây là bằng chứng sàn C có ích. Nhóm |move| lớn phải có `decay` lớn hơn và slippage tệ hơn nhóm nhỏ.
     Cần ≥ 30 cặp mỗi nhóm mới kết luận. Nếu không thấy khác biệt thì **không bật filter**: chỉ tốn lệnh mà không giảm trượt.
4. Chốt bộ ngưỡng và ghi lý do vào MASTER-PLAN §6.

## 4. Bật filter có kiểm soát

1. Config ⇒ Sàn C ⇒ **Chế độ: Filter**, nhập ngưỡng đã chốt ⇒ **Save**. Runtime áp ngay, hoặc bấm Reconnect cho chắc.
2. Log Start phải ghi `config=filter|…` đúng ngưỡng. Nếu C Down lúc Start sẽ có `FAST_LEG_NOT_READY_AT_START`
   (ERROR, kèm Telegram nếu đã cấu hình); khi đó mọi Open bị chặn cho tới khi C hoạt động.
3. Ngày đầu theo dõi sát:
   - `[FAST_LEG][DEFER]` / `[RELEASE]` / `[ABANDON]`: nhiều `ABANDON reason=defer_timeout` nghĩa là C TooFast/Stale kéo dài
     ⇒ ngưỡng đang quá chặt.
   - Số cặp mở/ngày so với lúc chưa bật. Giảm nhiều hơn mức % chặn ở bước 3 là bất thường.
   - Không có `[GAP_STABILITY][WARN] long_cycle`.
   - Chạy lại báo cáo sau 1–2 ngày (báo cáo sẽ cảnh báo dữ liệu bị cắt tại ngưỡng — đúng thiết kế).

## 5. Theo dõi & dừng khẩn cấp

- Cảnh báo cần phản ứng. Luôn có trong panel Signal và `trade-log` dạng `[FAST_LEG][ERROR] FAST_LEG_*`; qua Telegram
  thì chỉ khi bản build đã điền token (xem §0):
  - `FAST_LEG_DOWN`: C hỏng ≥ 30 s. Với `failOpen=false`, **mọi Open bị chặn**; Close vẫn chạy.
    Mở terminal C, kiểm kết nối và EA. Không sửa được nhanh thì tắt C (kill switch).
  - `FAST_LEG_NOT_READY_AT_START`: như trên, xảy ra lúc Start.
  - `FAST_LEG_RECOVERED`: C có giá trở lại.
- **Tắt terminal C KHÔNG phải kill switch** (nó chặn Open). Dừng sàn C đúng cách: [KILL-SWITCH.md](KILL-SWITCH.md):
  Config bỏ tick Bật sàn C rồi Save, hoặc SQL `enabled=false` / `mode=observe`, rồi Reconnect.
- Chỉ một máy chạy app cho mỗi tài khoản A/B tại một thời điểm, như trước.

## 6. Rollback

- Không cần sửa DB: bản app cũ bỏ qua cột `fast_leg`.
- Muốn bỏ hẳn: `update public.configs set fast_leg = null where lower(hostname) = '<host>';`, rồi gỡ EA C và đóng terminal C.
  Cột có thể để nguyên.
