# Phase 7 — Đặt/đóng lệnh thật (Demo → Live)

> Phase duy nhất app tự đặt lệnh trên PrimeXBT. Chia **3 bước A/B/C**, mỗi bước có cổng đồng ý riêng của chủ dự án.

[← Phase 6](phase-6-history.md) · [Index](README.md) · Phase sau: [Phase 8](phase-8-hardening.md)

---

## Cổng vào (recheck)

| # | Kiểm tra |
|---|---|
| 7.R1 | Bước chung README §8 (1–6) |
| 7.R2 | Nghiệm thu Phase 1–6 chạy lại PASS, gồm Layer 2 Phase 5 (hedge 2 record) và 6-A1 |
| 7.R3 | Đối chiếu config: `open_pending_time` ≥ giá trị đề xuất trong memo Phase 0 (P6); `primexbt_confirm_latency_b` đã đặt |
| 7.R4 | Tài khoản demo flat, HEDGE |

## Chốt trước khi code

| # | Câu hỏi | Đề xuất |
|---|---|---|
| 1 | `Success` của executor nghĩa là gì? | `orders/market/place` RESPONSE `error == null`. **Không** coi là đã có vị thế — vẫn xác nhận qua Trades map (D2) |
| 2 | Timeout chờ ack | 5 s (như cTrader `OrderReportTimeoutMs`). Timeout ⇒ `Success=false, Detail="TIMEOUT_UNCERTAIN"` + kích hoạt đối soát (D4); **không gửi lại** |
| 3 | Đóng | Chỉ `positions/id/close {positionId: subId, qty: qty hiện tại}`; ticket decode bằng `PrimeXbtTicketCodec`; không có trong cache ⇒ fail-closed |
| 4 | Lệnh khi socket rớt | Không gửi; `Success=false, Detail="NOT_CONNECTED"` |

## Việc làm

| File | Việc |
|---|---|
| `PrimeXbtTradeSession.SendMarketOrderAsync / ClosePositionAsync` | **Đường DUY NHẤT** gửi lệnh PrimeXBT; chờ RESPONSE theo `rid`; map lỗi qua `PrimeXbtErrorMapper`; log `[PRIMEXBT][ORDER]` (không token) |
| `PrimeXbtReconciler` | Sau `TIMEOUT_UNCERTAIN`: đọc `report/orders2` khoảng `[sendTime−2s, now]` + snapshot `positions`; kết luận `Filled(subId)` / `NotPlaced` / `Unknown` ⇒ log + Telegram; **chỉ báo cáo**, router xử lý theo đường partial/rollback hiện có |
| `App/Services/PrimeXbtTradeExecutor.cs` | `ITradePlatformExecutor`: `OpenLegAsync` → planner → session; `CloseLegAsync` → decode ticket → `TryGetOpenPosition` → close. `OpenPairAsync/ClosePairAsync` trả lỗi rõ (router luôn đi per-leg) |
| `App.xaml.cs` | Thay `NullPrimeXbtTradeExecutor` bằng executor thật (giữ Null làm kill switch qua 1 dòng DI) |
| `DashboardViewModel.CheckFirstPairLotOnce` | Áp dụng cho B = primexbt (so `qty/100` với lot A sau 3 s như cTrader) |

## Đã làm (2026-10-09)

- `IPrimeXbtTradeSession.SendOrderAsync` (cài trong `PrimeXbtFwsSession`) — **đường duy nhất** gửi lệnh; chỉ nhận plan của
  `PrimeXbtOrderPlanner` (route khác ⇒ từ chối, không gửi); chờ RESPONSE theo `rid` 5 s; 2 dạng lỗi ⇒ `Success=false` +
  `code: mô tả`; timeout / socket rớt / phiên dừng khi còn chờ ack ⇒ `TIMEOUT_UNCERTAIN`, **không gửi lại**, đối soát sau
  3 s bằng snapshot vị thế (`FILLED (sub X)` / `NOT_PLACED` / `CLOSED` / `NOT_CLOSED` / `UNKNOWN`) — chỉ log + Telegram
  (`PRIMEXBT_ORDER_UNCERTAIN`, `PRIMEXBT_ORDER_RECONCILED`). Không làm `PrimeXbtReconciler` riêng: logic nằm trong session vì
  cần snapshot lúc gửi; kết luận dùng snapshot (đủ cho 7A-4) thay vì `report/orders2`.
- `App/Services/PrimeXbtTradeExecutor`: open = planner(`symbol`, chiều, `volumeBOz`, trade-settings) → session; close = decode
  ticket bit 61 → `TryGetOpenPosition` (không có ⇒ fail closed) → `positions/id/close` đúng qty. DI thay Null (kill switch 1 dòng).
- `CheckFirstPairLotOnce` áp dụng cho B = primexbt (`IsExchangeBWithoutHwnd`).
- 7A chạy bằng harness console tạm (scratchpad) dùng **đúng** `PrimeXbtFwsSession` + `ClientWebSocketPrimeXbtTransport` — không
  thêm nút debug vào UI.

## Bước A — Executor cô lập trên DEMO (cần đồng ý)

Harness test (switch Auto **tắt**, gọi executor trực tiếp từ một nút debug chỉ có trong build Debug):

| # | Kịch bản | Kết quả |
|---|---|---|
| 7A-1 | Open Buy 0.01 → record xuất hiện → Close theo ticket → biến mất | ✅ ack 297 ms, hiện 1.67 s; close ack 250 ms, biến mất 1.0 s |
| 7A-2 | Open Sell khi đang có Buy (hedge) → đóng từng cái, không đụng cái kia | ✅ |
| 7A-3 | Lệnh bị từ chối (qty sai) → `Success=false`, Detail map đúng | ✅ planner chặn tại chỗ (`qty 0.015 không là bội của step 0.01`); bỏ qua planner ⇒ server `WRONG_ORDER_AMOUNT`, không lọt vị thế |
| 7A-4 | Rớt socket ngay sau gửi → `TIMEOUT_UNCERTAIN` → reconciler kết luận đúng | ✅ abort 40 ms sau gửi ⇒ `TIMEOUT_UNCERTAIN (socket rớt sau khi gửi)`, không gửi lại; đối soát ⇒ `FILLED (sub 10689793)` đúng thực tế |
| 7A-5 | Code không có đường nào gọi `positions/close` / `close/all` (grep + test reflection) | ✅ recheck A6 + test `SendOrder_ForbiddenRoute_IsRejected_NothingSent` |

## Bước B — Nghiệm thu cặp đầy đủ trên DEMO (A = MT5 demo, B = PrimeXBT demo) (cần đồng ý)

| # | Kịch bản | Kết quả |
|---|---|---|
| 7B-1 | Auto open/close ≥ 10 cặp, volume nhỏ nhất; mỗi cặp: đủ 2 chân, đóng đúng pair | ✅ 26 lệnh Open qua 4 phiên (config nhanh: ngưỡng −100, hold 30 s, post-close 5–8 s), ≥ 17 cặp Auto đóng theo signal, đúng ticket; volume 0.01 lot / 1 oz |
| 7B-2 | Rule A quota / Rule B cooldown / Rule C opposite lock / Rule D priority close vẫn đúng (đối chiếu log `[CYCLE]`, `[SLOT]`, `[CLOSE_SELECT]`) | ✅ `max_total_opens` 1 rồi 2: không vượt quota; `POST_CLOSE_OPEN_LOCK` chặn mở lại; 2 slot tới hạn cùng lúc ⇒ đóng lần lượt (Close→Close lock 1–3 s), mỗi tick 1 slot; `DUAL_SIDE_TRIGGER_DROPPED` Buy thắng |
| 7B-3 | Manual per-pair close (nút "Đóng" tab Trade) đóng đúng 2 chân của pair đó | ✅ `Close(6)`: `ManualPairClose` A 77483260 + B sub 10689941, xác nhận 1.3 s, barrier END |
| 7B-4 | Partial open: ép B từ chối (qty sai tạm thời) ⇒ `CloseOpenedLegByTimeoutAsync` đóng chân A | ✅ `volumeBOz=6000` (> max 5000) ⇒ B `qty 6000 > max 5000` ⇒ rollback A sau 31.8 s; lần 2 ⇒ F4-3 `PARTIAL_OPEN_ROLLBACK_STREAK 2/2` chặn Open. (`0.015` bị chính validation config chặn ⇒ phiên không mở, map B fail-closed — đúng.) Phát hiện F7-2 |
| 7B-5 | External close: user đóng chân B trên web ⇒ `CloseRemainingLegAfterExternalCloseAsync` đóng chân A | ✅ spike đóng B sub 10689954 ⇒ 4 lần quét xác nhận ⇒ `ExternalPartialCloseRecovery` đóng A 77483347 (~1.8 s) |
| 7B-6 | Restart giữa chu kỳ: `current_slots` restore, ticket B decode đúng, không đóng nhầm | ✅ tắt app khi cặp mở ⇒ mở lại + Start ⇒ `[SLOT][RECOVERY] Restored … ticketB=2305843009224383921`, đóng theo signal sau hold, đúng ticket |
| 7B-7 | Lot check cặp đầu khớp (oz/100 == lot A) | ✅ `first_pair_lot OK lotA=0.01 lotB=0.01` |
| 7B-8 | Đo: thời gian open→confirm B p50/p95; số rollback nhầm = 0 | ✅ 26 cặp: open→confirm p50 1 504 / p95 2 162 / max 2 415 ms; ack B p50 266 / p95 375 ms; `TIMEOUT_UNCERTAIN` 0; rollback ngoài 7B-4 = 0 |

Test giữ PASS bắt buộc (Rule F): `ManualPairClosePolicyTests`, `TryClaimSlotClose*`,
`ManualClaim_DoesNotPreventAutoFromClaimingAnotherPair`, `PartialOpenRecoverySlot_CannotBeClaimedByAutoClose`.

## Bước C — Live (chỉ khi chủ dự án đồng ý trong đúng lượt đó)

- Tài khoản Real (`L…`), volume nhỏ nhất, một máy/VPS, giám sát trực tiếp; runbook riêng `phase-7-vps-run.md` (viết khi tới bước này).
- Tiêu chí dừng ngay: bất kỳ lệnh mồ côi, rollback nhầm, `Unknown` từ reconciler, storm reconnect.

## Phát hiện

**F7-1 — `close_pending_time_ms` 1015 quá ngắn cho PrimeXBT (cấu hình, không phải code).** Snapshot vị thế trễ ~1.4–1.9 s sau
ack đóng ⇒ pending-close retry gửi đóng lại cùng sub id ⇒ sàn trả `POSITION_NOT_FOUND` (8/15 lần đóng). Vô hại (đóng theo id,
không thể trúng vị thế khác; cặp vẫn xác nhận đóng) nhưng thừa request. Đặt **2500** ⇒ 0/4 lần. **Khuyến nghị: VPS chạy
B = primexbt đặt `close_pending_time_ms` ≥ 2500.** Laptop đã trả về 1015.

**F7-2 — Race có từ trước (mọi platform): đúng mốc `open_pending_time_ms`, rollback partial-open và bộ phát hiện "EA đóng 1
chân" cùng dispatch đóng chân A** (cách 10 ms, log 16:06:09.636 / .641). MT5 chỉ ghi 1 lần đóng; router tra lại hàng theo
ticket nên không trúng vị thế khác. Ghi nhận, không sửa (logic recovery chung — cần chủ dự án quyết nếu muốn gộp).

## Rollback

DI về `NullPrimeXbtTradeExecutor` (1 dòng) ⇒ mọi lệnh B fail an toàn; hoặc đổi `platform_b`. Vị thế còn mở đóng tay trên web.

## Cổng ra → Phase 8

7A ✅ → (đồng ý) → 7B ✅ → (đồng ý) → 7C chạy ổn ≥ 1 tuần giao dịch.

## Nhật ký recheck

| Ngày | Commit | Test (pass/fail) | Kết luận |
|---|---|---|---|
| 2026-10-09 | 4b209e7 | 1 302 / 11 | Recheck vào PASS; demo flat, HEDGE; `open_pending_time_ms` 30 000 ≥ p95 hiện vị thế 1.9 s |
| 2026-10-09 | (chưa commit) | 1 313 / 11 | Recheck ra PASS + test Rule F 8/8. **7A ✅ 7B ✅** trên demo. 7C chờ chủ dự án đồng ý |
