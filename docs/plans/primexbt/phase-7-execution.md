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

## Bước A — Executor cô lập trên DEMO (cần đồng ý)

Harness test (switch Auto **tắt**, gọi executor trực tiếp từ một nút debug chỉ có trong build Debug):

| # | Kịch bản | Kết quả |
|---|---|---|
| 7A-1 | Open Buy 0.01 → record xuất hiện → Close theo ticket → biến mất | |
| 7A-2 | Open Sell khi đang có Buy (hedge) → đóng từng cái, không đụng cái kia | |
| 7A-3 | Lệnh bị từ chối (qty sai) → `Success=false`, Detail map đúng | |
| 7A-4 | Rớt socket ngay sau gửi → `TIMEOUT_UNCERTAIN` → reconciler kết luận đúng | |
| 7A-5 | Code không có đường nào gọi `positions/close` / `close/all` (grep + test reflection) | |

## Bước B — Nghiệm thu cặp đầy đủ trên DEMO (A = MT5 demo, B = PrimeXBT demo) (cần đồng ý)

| # | Kịch bản | Kết quả |
|---|---|---|
| 7B-1 | Auto open/close ≥ 10 cặp, volume nhỏ nhất; mỗi cặp: đủ 2 chân, đóng đúng pair | |
| 7B-2 | Rule A quota / Rule B cooldown / Rule C opposite lock / Rule D priority close vẫn đúng (đối chiếu log `[CYCLE]`, `[SLOT]`, `[CLOSE_SELECT]`) | |
| 7B-3 | Manual per-pair close (nút "Đóng" tab Trade) đóng đúng 2 chân của pair đó | |
| 7B-4 | Partial open: ép B từ chối (qty sai tạm thời) ⇒ `CloseOpenedLegByTimeoutAsync` đóng chân A | |
| 7B-5 | External close: user đóng chân B trên web ⇒ `CloseRemainingLegAfterExternalCloseAsync` đóng chân A | |
| 7B-6 | Restart giữa chu kỳ: `current_slots` restore, ticket B decode đúng, không đóng nhầm | |
| 7B-7 | Lot check cặp đầu khớp (oz/100 == lot A) | |
| 7B-8 | Đo: thời gian open→confirm B p50/p95; số rollback nhầm = 0 | |

Test giữ PASS bắt buộc (Rule F): `ManualPairClosePolicyTests`, `TryClaimSlotClose*`,
`ManualClaim_DoesNotPreventAutoFromClaimingAnotherPair`, `PartialOpenRecoverySlot_CannotBeClaimedByAutoClose`.

## Bước C — Live (chỉ khi chủ dự án đồng ý trong đúng lượt đó)

- Tài khoản Real (`L…`), volume nhỏ nhất, một máy/VPS, giám sát trực tiếp; runbook riêng `phase-7-vps-run.md` (viết khi tới bước này).
- Tiêu chí dừng ngay: bất kỳ lệnh mồ côi, rollback nhầm, `Unknown` từ reconciler, storm reconnect.

## Rollback

DI về `NullPrimeXbtTradeExecutor` (1 dòng) ⇒ mọi lệnh B fail an toàn; hoặc đổi `platform_b`. Vị thế còn mở đóng tay trên web.

## Cổng ra → Phase 8

7A ✅ → (đồng ý) → 7B ✅ → (đồng ý) → 7C chạy ổn ≥ 1 tuần giao dịch.

## Nhật ký recheck

| Ngày | Commit | Test (pass/fail) | Kết luận |
|---|---|---|---|
| | | | |
