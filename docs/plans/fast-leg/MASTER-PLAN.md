# MASTER PLAN — Sàn C "Fast Leg" (nguồn giá thứ ba, chỉ để lọc "tránh xung lượng")

> **File duy nhất cần mở để làm việc.** Khuôn giống `docs/plans/tradelocker/MASTER-PLAN.md`: bảng trạng thái (§1),
> vòng lặp 8 bước (§2), recheck tích luỹ (§3), thẻ phase (§4), nhật ký append-only (§6).
> Bản này đã được rà chéo với code thật; kết quả rà (F1–F16) đã đưa vào các phase, đánh dấu `[R-Fx]`.
>
> Tài liệu kèm theo: [README.md](README.md) (kiến trúc, ngữ nghĩa phép đo), [KILL-SWITCH.md](KILL-SWITCH.md),
> [tools/recheck.ps1](tools/recheck.ps1). Nguồn ý tưởng: slide `fast-leg-slides.html` của Long (không nằm trong repo).
>
> **Cách dùng mỗi phiên:** *"Đọc `docs/plans/fast-leg/MASTER-PLAN.md`. Chạy phase đầu tiên chưa ✅ theo §2. Sau Bước 1
> báo bảng recheck trước khi code. Dừng ở mọi 🛑. Không commit."*

---

## Context

### Vì sao cần sàn C
Tín hiệu A–B chỉ nhìn hai sàn, nên không phân biệt được hai trường hợp:
- "A chậm thật" (lệch sạch, nên vào),
- "giá thật đang chạy" (lệch sẽ khép trước khi lệnh khớp ⇒ trượt 4–7 điểm).

Theo slide `fast-leg-slides.html` và trao đổi với Long:
- Bot khớp lệnh mất hàng trăm ms, không thắng được cuộc đua độ trễ ⇒ dùng quy tắc **quiet**.
- Phép đo: biên độ giá giữa của sàn C trong 200 ms. Nếu |move| ≥ ngưỡng thì giá đang chạy quá nhanh ⇒ không vào lệnh.
- "Động lượng = khối lượng × vận tốc" chỉ là cách nói; thứ được đo thật là **quãng đường giá F đi trong cửa sổ**.
- F **không bao giờ đặt lệnh**.

Thông số Long đưa (áp dụng ngay):

| Thông số | Giá trị |
|---|---|
| rule | quiet |
| mode | filter |
| max_move | 5 điểm |
| window | 200 ms |
| max_age | 1000 ms |
| fail_open | tắt |
| apply_to_exit | bật |

- Số liệu của Long trên 265 lệnh: |move| trung vị 2, p90 4; tuổi tick F trung vị 59 ms.
- 1 điểm của Long = 0.01 USD (XAU), trùng đơn vị với `point=100` của dự án.

### Quyết định đã chốt với chủ dự án

| # | Quyết định |
|---|---|
| D1 | Sàn C = **terminal MT5 thứ 3**, chạy EA `DataExporter` với `EA_CHANNEL_ID="C"`, đọc MMF `Local\MT_C_Tick` |
| D2 | Chỉ làm quy tắc **quiet** |
| D3 | Bật **filter ngay** theo thông số Long, vẫn ghi đủ dữ liệu verdict. Vẫn hỗ trợ chế độ `observe` |
| D4 | Gate **TRONG engine, trước khi xoá cycle**: F chặn ⇒ engine trả `null`, không reset, tick sau hỏi lại F |
| D5 | **SOS Close bỏ qua F.** Normal Close và TP bị hoãn khi `apply_to_exit` bật |
| D6 | **Close không bao giờ chờ C hỏng**: lúc đóng chỉ TooFast mới hoãn, Down/Stale luôn cho đóng. Open theo `fail_open`. Telegram khi C hỏng quá 30 s |
| D7 | Làm trên nhánh `feature/exchange-c-fast-leg`, tách từ `feature/tradelocker` (`30f38db`) |
| D8 | **Trần hoãn `maxDeferMs`** nằm trong JSON `fast_leg` (mặc định 3000, khoảng 500–10000). Quá trần ⇒ reset cycle, ghi `ABANDON`, chống phình `_gaps` (pitfall treo UI) `[R-F1]` |
| D9 | **Stale vẫn chặn Open** như cấu hình Long. Heartbeat của map `MT_C_Trades` (offset 4 = GetTickCount64, offset 12 = TERMINAL_CONNECTED, ghi mỗi 50 ms) dùng để tách **Down** (terminal chết hoặc mất kết nối) khỏi **Stale** (terminal sống nhưng không có tick). STATS đếm riêng hai loại; quyết định lại sau khi có dữ liệu `[R-F12]` |

### Đối chiếu business rules (CLAUDE.md §2)
- **Rule E:** F chỉ chặn hoặc hoãn. Engine vẫn là nguồn duy nhất của signal.
- **Rule A, B, C:** không đổi. Gate không tạo và không gia hạn timer nào.
- **Rule D:**
  - Verdict là chung cho cả tick, nên mọi Normal Close/TP cùng bị hoãn hoặc cùng được thả ⇒ thứ tự ưu tiên khi thả vẫn đúng.
  - **Lệch chấp nhận (ghi vào CLAUDE.md):** lúc TooFast, slot SOS lợi nhuận thấp vẫn đóng, trong khi slot Normal lợi nhuận cao đang bị hoãn `[R-F5]`.
- **Rule F:** Manual per-pair và Recovery không đi qua engine ⇒ không bị F chặn.
- **F tắt** (cột `null` hoặc `enabled=false`) ⇒ `snapshot.FastLeg = null` ⇒ hành vi giống hệt cũ. Có test khoá.

---

## Kiến trúc

```
Terminal MT5 #3 (EA channel C) ─ MT_C_Tick (64 B đầu) + MT_C_Trades (heartbeat 16 B)
        │
        ▼
FastLegMmfSampler (thread riêng, PeriodicTimer 10 ms ≈ 15.6 ms thực, đọc chống torn-read)
        │ Push(sample) / SetHeartbeat(alive, connected)
        ▼
FastLegTickBuffer (lock, trần mẫu)
        │
DashboardViewModel.OnSnapshotReceived (UI thread, 50 ms)
  └─ ngay sau Map (L7749): verdict = _fastLegGate.EvaluateNow()  ← MỘT lần mỗi tick [R-F7]
        ├─ LogGapTick (L7754): c_* trước point=
        ├─ strip "Sàn C" + FastLegStats + FastLegHealthTracker (chạy cả khi chưa Start)
        └─ GapSignalSnapshot(..., FastLeg: verdict) → Coordinator → Open engine / Close engine (gate D4 + trần D8)
TradeExecutionRouter.ValidateOpenPolicy → FastLegOpenPolicy.Check(gate.EvaluateNow()) (chỉ Open, chỉ log ở lượt sau mutex)
```

### Ngữ nghĩa phép đo (chốt để test)

**Đọc MMF `[R-F10][R-F11]`**
- Đọc **64 B**: header 16 B (`version@0`, `ts@4`) + body 48 B (`bid@16`, `ask@24`, `spread@32`, `time_msc@40`, `symbol@48..63`).
- Thứ tự đọc: `ts` → body → `ts` lần nữa. Hai lần khác nhau ⇒ thử lại tối đa 3 lần, rồi bỏ mẫu.
- Kiểm toàn vẹn:
  - `spread == ask − bid` (EA ghi đúng phép tính này dạng double) — đây là checksum có sẵn.
  - `time_msc` không đi lùi.
  - `version == 1`.
  - Symbol khớp cấu hình (nếu có).
- Tick mới khi khoá `(ts, time_msc, bid, ask)` đổi.

**Thời gian**
- Mốc thời gian của mẫu là `ts` do EA ghi (`GetTickCount64`), cùng đồng hồ với `Environment.TickCount64`. Độ phân giải khoảng 15.6 ms.
- `age = max(0, now − ts_last)`.

**Phép đo**
- `mid = (bid + ask) / 2`.
- Mẫu tham chiếu: mẫu mới nhất có `ts ≤ now − windowMs`. Không có mẫu nào như vậy ⇒ dùng mẫu cũ nhất.
- `move = (mid_last − mid_ref) × CurrentPoint`.
- `span = ts_last − ts_ref`.
- `velocity = span > 0 ? move / span × 1000 : 0` (chỉ để log).

**Thứ tự phán quyết**
1. **Down**: F tắt, không có map tick, heartbeat Trades cũ quá 2 s hoặc `connected = 0`, version/symbol sai, hoặc point ≤ 0. Không có map Trades ⇒ bỏ qua heartbeat, ghi chú `hb=n/a`.
2. **Stale**: chưa có tick, hoặc `age > maxAgeMs`.
3. **TooFast**: `|move| ≥ maxMovePts`.
4. **Confirmed**: còn lại.

**Chính sách**
- `BlocksOpen = filter ∧ Kind ≠ Confirmed ∧ ¬(failOpen ∧ Kind ∈ {Down, Stale})`.
- `BlocksClose = filter ∧ applyToExit ∧ Kind == TooFast` (D6).

### Điểm chạm đã xác minh trong code

| Việc | Vị trí |
|---|---|
| Gate Open | `GapSignalConfirmationEngine.TryCreateStableOpenResult` (`private static`, đã có `ref observation` ở :209) ⇒ thêm `ref FastLegDeferState`. Chèn giữa :245 và :247 |
| Gate Normal/SOS Close | `CloseSignalEngine` giữa :333 và :335 (`usesSos` có sẵn) |
| Gate TP | `CloseSignalEngine.ProcessTp` giữa :572 và :574 |
| Các điểm reset phải xoá trạng thái defer `[R-F2]` | Open: `Reset()` :96, được gọi từ Coordinator :432 / :522 / :1428; reset nội bộ :235 / :243; cycle vỡ trong `Process()`. Close: `Reset()` :157, `ResetGapState()` :163 (lật SOS), `ResetTpCycle` :476 / :546 / :613 |
| Dựng `GapSignalSnapshot` | Chỉ một chỗ production: VM :7799. Test dựng theo vị trí 8 tham số ⇒ thêm tham số optional ở cuối vẫn biên dịch |
| Gap-tick | `GapTickLineFormatter` :20-45. Test khoá `EndsWith(" point=100")` |
| Lifecycle | `LogSignalOutcome` VM :9467, chèn sau :9576. `_signalContextByTrigger` so sánh theo tham chiếu nên thêm field không làm hỏng lookup |
| Router | `ValidateOpenPolicy` trước :478. Chạy 2 lần: :53 (trước mutex) và :76 (sau mutex) |
| Parse config | `SupabaseConfigRepository` gọi tay `TryGetProperty(...)` + `.Clone()` ở **hai** hàm (:370/:463 và :562/:653). `ConfigService` theo mẫu `Success(...) with { ... }` (:157). Chỉ có 3 PATCH theo cột, nên không ai xoá `fast_leg` |
| Start | `StartTradingLogicAsync`, sau `LogHedgeVolumeConsistency()` (:1425) |
| Vòng đời reader | ctor VM :529-530; không có Stop |

---

## §0 Quy tắc cứng
- **CLAUDE.md §0.0 đứng trên hết.** Chỉ đặt lệnh trên laptop dev sau khi xác minh **cả 3 terminal (A, B, C)** đều là "Demo Account".
  Không đụng VPS/host live; đổi config live do chủ dự án làm.
- **Cảnh báo va chạm kênh `[R-F13]`:** EA với `EA_CHANNEL_ID` trùng kênh "A" hoặc "B" sẽ **ghi đè** giá của chân đang giao dịch, vì `CreateFileMappingW` mở lại map đã có.
  Terminal C bắt buộc dùng `"C"` và phải chạy cùng Windows session với app (`Local\`).
- 🛑 = dừng, chờ chủ dự án đồng ý trong chính lượt đó. Áp dụng cho: đặt lệnh demo, migration DB, đổi hành vi ngoài phạm vi đã chốt, mọi việc live.
- Không commit nếu chưa được yêu cầu trong lượt đó. Chỉ đề xuất message theo dạng `fast-leg P<N>: <tóm tắt>`.
- Không refactor code A/B (§0.2): không sửa `SharedMemoryMarketDataReader`, `SignalEntryGuard`, `PositionSlot`, `SlotPersistence`, `SansJsonHelper`, EA.
- Báo chủ dự án sau Bước 1 (bảng recheck) và sau Bước 7 (bảng nghiệm thu) của mỗi phase.

## §1 Bảng trạng thái

| Phase | Tên | Phụ thuộc | Đổi hành vi giao dịch? | Đặt lệnh? | Trạng thái | Commit | Ngày |
|---|---|---|---|---|---|---|---|
| 0 | Nhánh + baseline + khung tài liệu + terminal C | — | Không | Không | ✅ (C = Exness #4, chỉ đọc giá) | `9b830c7` | 2026-10-30 |
| 1 | Config `fast_leg` (DB → runtime) | 0 | Không | Không | ✅ code + test; migration (chủ dự án chạy); T1 runtime ✅ 2026-11-02. Chưa kiểm: Config→Save giữ cột, JSON hỏng | `9b830c7` | 2026-11-02 |
| 2 | Đọc giá C: sampler, buffer, evaluator, heartbeat, STATS, strip UI | 1 | Không (chỉ đọc) | Không | ✅ code + test; T2 runtime ✅ 2026-11-03, kể cả nhánh `connected=0` (`terminal_disconnected`) | `9b830c7` | 2026-11-03 |
| 3 | Ghi dữ liệu verdict (observe end-to-end, engine chưa chặn) | 2 | Không | Demo 🛑 (tuỳ chọn) | ✅ code + test; T3 gap-tick ✅; lifecycle `fast*` ✅ runtime (Phase 6, 36 cặp) | `9b830c7` | 2026-11-03 |
| 4 | Gate trong engine + trần hoãn | 3 | **Có** (chỉ khi bật F) | Không | ✅ code + 21 test gate; T4 ✅ runtime (Phase 6: DEFER/RELEASE Open + Close, ABANDON `cycle_reset`, 0 `long_cycle`). SOS lúc TooFast chỉ có unit test | `9b830c7` | 2026-11-03 |
| 5 | Router re-check Open + kiểm lúc Start + Telegram | 4 | Có (chỉ chặn Open) | Không | ✅ code + test; log Start ✅ runtime; router chặn `FAST_LEG_STALE` ✅ runtime (1 lần, Phase 6). T5 chỉ lý thuyết (theo chủ dự án) | `9b830c7` | 2026-11-03 |
| 6 | Nghiệm thu demo end-to-end + soak 1 h | 5 | — | Demo 🛑 | ✅ 6-1, 6-2, 6-5, 6-6 (Close per-pair + restart giữa chu kỳ), 6-7 trên Connext-Demo (phiên `20261103_141615`, `_161908`, `_163241`). Chỉ có unit test: 6-3 (SOS lúc TooFast). Chỉ lý thuyết theo chủ dự án: 6-4/T5 | `42d1a93`, `9871886` | 2026-11-03 |
| 7 | Công cụ phân tích ngưỡng (Python offline) | 3 | Không | Không | ✅ tool + 24 test; nghiệm thu trên log Phase 6: 36 `SIGNAL_OPEN_CONFIRMED` = 36 cặp, markout 36/36 | `1e690a5` | 2026-11-03 |
| 8 | Tài liệu + runbook live cho chủ dự án | 6, 7 | — | Live, **chỉ user** 🛑 | ✅ tài liệu + runbook; đã rà lại sau Phase 6 (số đo, Telegram no-op, bẫy MT5). Bật live: chủ dự án tự làm theo runbook | `42d1a93`, `8011fda` | 2026-11-03 |

Ký hiệu: ✅ xong · ⏳ làm được · 🔄 đang làm · ❌ fail, đang sửa · ⛔ chưa đủ phụ thuộc.

### Baseline (điền ở Phase 0; chỉ đổi khi chủ dự án duyệt)

| Mục | Giá trị |
|---|---|
| Commit baseline | `30f38db` (2026-10-30, nhánh `feature/exchange-c-fast-leg` tách từ `feature/tradelocker`) |
| Test pass / fail | 1583 / 11 (tổng 1594) |
| Danh sách test fail | [tools/baseline-failed-tests.txt](tools/baseline-failed-tests.txt): 8 `CloseSignalEngineTests`, 1 `PortfolioBlockedSignalTests`, 2 `SosCloseConfigResolverTests` (trùng baseline TradeLocker) |
| Số warning build Release | 3: [tools/baseline-warnings.txt](tools/baseline-warnings.txt) |
| Secret-like có sẵn được phép | 1: [tools/baseline-secret-hits.txt](tools/baseline-secret-hits.txt) |
| Nhóm test khoá A4 | 17 nhóm, **xanh toàn bộ** trên baseline (không nhóm nào phải thu hẹp) |
| Terminal C | `C:\Program Files\MetaTrader 5 4` — **Exness-MT5Real28 213656047 (tài khoản THẬT, chỉ dùng đọc giá)**, chart XAUUSD M1, 3 chữ số. Data folder `C8EA20FBAA76B52E8CECA1F23EADCE50`. Khởi động bằng `fastleg-c-startup.ini` (`[Experts] AllowLiveTrading=0, Enabled=0, AllowDllImport=1`) + preset `MQL5\Presets\fastleg-c.set` (`EA_CHANNEL_ID=C`). EA không có lệnh giao dịch nào (đã grep) và vẫn chạy khi Algo Trading tắt |
| Tài khoản MT5 trên laptop lúc baseline | ⚠️ `MetaTrader 5 1` và `MetaTrader 5 2` đăng nhập **CMCMarkets-MT5-LIVE 39244439** (title cửa sổ) ⇒ coi là THẬT. Không đặt lệnh nào trên laptop cho tới khi cả 3 terminal là demo |

## §2 Vòng lặp 8 bước (MỌI phase)

| Bước | Việc | Dừng khi |
|---|---|---|
| 1. Recheck vào | `recheck.ps1 -Phase N` + checklist tay của mọi phase < N | Có FAIL ⇒ sửa phase cũ trước |
| 2. Chốt | Đọc lại thẻ phase; hỏi nếu còn câu hỏi | 🛑 còn câu hỏi |
| 3. Rủi ro | 3 dòng CLAUDE.md §0.1 (logic giao dịch / luồng dữ liệu / side effect) ghi vào §6 | Có rủi ro chưa được phép |
| 4. Làm | Đúng mục "Làm" | Gặp việc ngoài phạm vi |
| 5. Unit test | Test mới: happy path + ≥ 2 edge cho mỗi method public. Chạy `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj` | Có tên fail mới |
| 6. Runtime | Kịch bản "Nghiệm thu" | Còn ❌ |
| 7. Recheck ra | `recheck.ps1 -Phase N -Exit` + checklist tay của chính phase N | FAIL |
| 8. Bàn giao | Cập nhật §1/§6, đề xuất commit, ghi pitfall mới | — |

## §3 Recheck tích luỹ

### Tự động: `docs/plans/fast-leg/tools/recheck.ps1`
Chép từ bản TradeLocker. Sửa `$planRel`, banner, thư mục build tạm, A1, A6, A7.

| ID | Kiểm tra | Từ phase | FAIL khi |
|---|---|---|---|
| A1 | `git status` + **kiểm nhánh `feature/exchange-c-fast-leg`** (code mới — bản TradeLocker không kiểm nhánh) | 0 | Sai nhánh. Có thay đổi ngoài phạm vi chưa khai báo ⇒ WARN |
| A2 | Build Release ra thư mục tạm | 0 | Lỗi build hoặc số warning vượt baseline |
| A3 | `dotnet test` | 0 | Có tên test fail mới |
| A4 | Nhóm test khoá (danh sách dưới) | 0 | Có test khoá fail |
| A5 | Secret scan | 0 | Có hit mới |
| A6 | Rule E grep: các file `*FastLeg*` (trừ Tests) **không** chứa `OpenPairAsync`, `ClosePairAsync`, `SendOrderAsync`, `SendMarketOrderAsync`, `TryMarkCloseTriggered`, `AllocatePendingOpenSlot`, `ManualClosePairBySlotAsync`. Thay đổi hành vi trong engine thì dựa vào A4 | 2 | Có xuất hiện |
| A7 | Probe `Local\MT_C_Tick` + `MT_C_Trades` qua `Add-Type` P/Invoke `GetTickCount64` (PS 5.1 không có `TickCount64`) `[R-F14]` | 2 | **FAIL:** version sai, symbol sai, heartbeat cũ quá 5 s hoặc `connected=0`. **WARN:** tick cũ (cuối tuần / thị trường yên). **SKIP:** không có map |
| A8 | `python -I tools/test-fast-leg-report.py` (thêm ở Phase 7) | 7 | Có test fail, hoặc không có `python` |

Nhóm test khoá (A4). Test ở `TradeDesktop.Tests/FastLeg/` phải dùng namespace `TradeDesktop.Tests.FastLeg`, vì A4 so khớp theo tên đầy đủ.
- **Giữ nguyên nhóm của TradeLocker:** `PlatformNormalizationTests`, `PlatformBSwitchGuardTests`, `ManualPairClosePolicyTests`, `TryClaimSlotClose`,
  `ManualClaim_DoesNotPreventAutoFromClaimingAnotherPair`, `PartialOpenRecoverySlot_CannotBeClaimedByAutoClose`,
  `TradeDesktop.Tests.PrimeXbt.`, `BPriceOffset`, `TradeDesktop.Tests.TradeLocker.`.
- **Thêm từ Phase 0:** `GapTickLineFormatterTests`, `GapStableOpenIntegrationTests`, `GapStableCloseIntegrationTests`, `DualSideOpenTriggerTests`,
  `PriorityCloseRuleTests`, `OpenMaxLastGap`, `SignalGapOutcomeTracker`, `GapCycleLoggingTests`.
  Ở Phase 0 phải xác nhận mọi nhóm này **xanh toàn bộ** trên baseline. Nhóm nào có test fail sẵn thì chỉ khoá các test đang pass.
- **Thêm từ cổng ra Phase 1:** `TradeDesktop.Tests.FastLeg.`.

### Checklist tay

| ID | Từ phase | Kiểm tra |
|---|---|---|
| T0 | 0 | Cả 3 terminal đều "Demo Account". Terminal C chạy EA kênh `"C"`. `DataExporter/test.py` (hoặc A7) đọc được `MT_C_Tick` và `MT_C_Trades`. Symbol và `time_msc` của C khác A và B |
| T1 | 1 | Start 2' với `fast_leg=null` ⇒ log ghi `fast_leg=disabled`, không có ERROR mới. Mở cửa sổ Config rồi Save ⇒ cột `fast_leg` trên DB không đổi. JSON hỏng ⇒ `[FAST_LEG][ERROR]` và F tắt. `mapName` trùng A/B ⇒ bị từ chối |
| T2 | 2 | Bật F, **chưa Start**: strip Sàn C khớp terminal C (≤ 1 tick). Tắt EA ⇒ Down ≤ 2 s. Ngắt mạng terminal C ⇒ Down (do `connected=0`). Có `[FAST_LEG][STATS]` mỗi phút. Khi F tắt, gap-tick giữ y format cũ |
| T3 | 3 | Start, hai switch Open TẮT ⇒ gap-tick có `c_*`. Nếu được phép chạy 1 cặp demo ⇒ dòng lifecycle có `fastVerdict` / `fastMovePts` / `fastAgeMs` |
| T4 | 4 | Có cặp `[FAST_LEG][DEFER]` / `[FAST_LEG][RELEASE]`. Có `ABANDON` khi vượt `maxDeferMs`. Lệnh chỉ mở khi \|move\| < 5. SOS vẫn đóng lúc TooFast. Không có `[GAP_STABILITY][WARN] long_cycle` |
| T5 | 5 | Tắt terminal C khi đang chạy ⇒ Open bị chặn, Close thường vẫn đóng, Telegram `FAST_LEG_DOWN` sau 30 s, bật lại ⇒ `FAST_LEG_RECOVERED` |

---

## §4 Thẻ phase

### Phase 0 — Nhánh, baseline, khung tài liệu, terminal C
- **Làm:**
  - Tạo nhánh: `git switch -c feature/exchange-c-fast-leg` (từ `30f38db`). Để nguyên `debug-logs/`.
  - Tạo `docs/plans/fast-leg/`:
    - `MASTER-PLAN.md` (plan này).
    - `README.md`: kiến trúc, ngữ nghĩa phép đo, D1–D9, rủi ro.
    - `KILL-SWITCH.md`: `fast_leg.enabled=false` hoặc `null` rồi Reconnect.
    - `tools/recheck.ps1`.
  - Chạy `-Phase 0 -WriteBaseline`. Kiểm từng nhóm khoá A4 có xanh trên baseline không.
  - 🛑 Chủ dự án cài terminal MT5 thứ 3 bằng tài khoản **DEMO**. AI hướng dẫn cài EA theo `docs/plans/ctrader-fix/phase-7-vps-run.md` §C, với `EA_CHANNEL_ID="C"` (cảnh báo `[R-F13]`).
  - Ghi lại digits/point XAU của C và độ lệch nền so với A/B.
- **Nghiệm thu:** baseline xong; T0 ✅; `TradeDesktop.*` không đổi.

### Phase 1 — Config `fast_leg` (không đổi hành vi)
- **Làm:**
  - 🛑 `docs/FAST-LEG-MIGRATION.sql`: thêm cột `fast_leg jsonb null` + comment + mẫu UPDATE cho laptop. Giá trị theo Long:
    `{"enabled":true,"mapName":"Local\\MT_C_Tick","heartbeatMapName":"Local\\MT_C_Trades","symbol":"XAUUSD","mode":"filter","rule":"quiet","maxMovePts":5,"windowMs":200,"maxAgeMs":1000,"maxDeferMs":3000,"failOpen":false,"applyToExit":true}`
  - `Application/Models/FastLegConfig.cs` (sealed record): `Disabled`, `IsActive`, `TryParse(json, mapName1, mapName2, out cfg, out error)`.
    - Ràng buộc:
      - `mapName` không rỗng và **khác MapName1/MapName2**.
      - `heartbeatMapName` mặc định = `mapName` thay `_Tick` bằng `_Trades`.
      - `rule` chỉ nhận `quiet`.
      - `mode` thuộc `{observe, filter}`.
      - `maxMovePts > 0`.
      - `windowMs` trong 1..5000.
      - `maxAgeMs > 0`.
      - `maxDeferMs` trong 500..10000 (mặc định 3000).
    - Vi phạm ⇒ `Disabled` kèm `error`.
  - `SupabaseConfigRepository`: `TryGetProperty("fast_leg")` + `.Clone()` (khi là Object) ở **cả hai** hàm parse, rồi đưa vào `ConfigRecord.FastLegJson` (thêm cuối record).
  - `ConfigService`: parse ⇒ `Success(...) with { FastLeg = ..., FastLegError = ... }`. `fast_leg` hỏng **không** làm hỏng cả config.
  - `IRuntimeConfigProvider.CurrentFastLegConfig => FastLegConfig.Disabled`.
  - `RuntimeConfigState.UpdateFastLeg(cfg)`: method riêng, field `volatile`. Không qua `Update(...)`.
  - Gọi `UpdateFastLeg` ở **cả hai** đường load: VM `InitializeRuntimeConfigAsync` (~7653) và `ConfigViewModel` (~912).
  - Log: thêm `fast_leg=...` vào `RuntimeSummary` (~4203) và `[DB]` (~7673). Khi có lỗi ⇒ `[FAST_LEG][ERROR] config invalid: ...`.
- **Test mới:**
  - `FastLegConfigTests`: thông số Long; default; từng ràng buộc sai; `momentum` bị từ chối; map trùng A/B bị từ chối.
  - `FastLegConfigMappingTests` (`StaticJsonHandler` + repository + service thật):
    - Thiếu cột ⇒ Disabled.
    - `null` ⇒ Disabled.
    - Hợp lệ ⇒ đúng giá trị.
    - Gọi `Update(...)` thiếu tham số không làm mất F.
- **Nghiệm thu:** T1.
- **Rollback:** code cũ dùng `select=*`, bỏ qua cột lạ ⇒ thứ tự deploy và migration không quan trọng.

### Phase 2 — Đọc giá C (chỉ đọc)
- **Làm:**
  - `Application/Models/FastLegVerdict.cs`:
    - Trường: `Kind`, `MovePts`, `VelocityPtsPerSec`, `SpanMs`, `AgeMs`, `CBid`, `CAsk`, `Mode`, `BlocksOpen`, `BlocksClose`, `DownReason`, `HeartbeatState`.
    - Có `ToLogFields()`.
  - `Application/Services/FastLeg/FastLegTickBuffer.cs`:
    - Dùng `lock`.
    - API: `Push`, `SetHeartbeat`, `MarkDown`, `Reset`, `Snapshot(windowMs, nowMs)`.
    - Chỉ giữ 1 mẫu ≤ đầu cửa sổ. Trần 1024 mẫu.
  - `Application/Services/FastLeg/FastLegEvaluator.cs`: hàm thuần, theo đúng ngữ nghĩa ở phần trên.
  - `Infrastructure/MarketData/FastLegMmfSampler.cs`:
    - Thread riêng.
    - Đọc 64 B chống torn-read và kiểm toàn vẹn (`[R-F10][R-F11]`). Đọc heartbeat 16 B đầu của map Trades.
    - Tắt F ⇒ dispose + `MarkDown("disabled")`.
    - Thiếu map ⇒ thử lại mỗi 1 s, log có throttle.
    - Đổi map ⇒ mở lại map + `Reset()`.
    - Bắt mọi exception, không để chết thread.
    - Code đọc viết **riêng**, không trích từ reader A/B.
  - `Application/Services/FastLeg/FastLegStats.cs`: mỗi 60 s ghi `[FAST_LEG][STATS] ticks= p50_age= max_age= p50_abs_move= p90_abs_move= toofast_pct= stale_pct= stale_hb_alive_pct= down_pct=`.
    `stale_hb_alive_pct` là phần dữ liệu để quyết lại D9.
  - `App/Services/IFastLegGate.cs` + `FastLegGate`: `EvaluateNow()` (cache theo `Environment.TickCount64` để hai lần gọi trong cùng 1 ms không tính lại).
  - DI singleton cho sampler, buffer, gate. Khởi động sampler trong ctor VM, cạnh L530.
  - VM: gọi `EvaluateNow()` **một lần** ngay sau `Map` (L7749) khi F bật `[R-F7]`, rồi dùng chung cho gap-tick, strip, stats, health và snapshot.
  - `MainWindow.xaml`: thêm **dải "Sàn C"** riêng dưới panel A/B (không động vào 10 grid hiện có). Nội dung: `symbol · bid/ask · ±move pt/200ms · tuổi · verdict` (tô màu). Ẩn khi F tắt. Cập nhật trong nhánh `canRenderUi`.
- **Test mới:**
  - `FastLegTickBufferTests`: prune, trần, mẫu tham chiếu khi tick thưa, ghi/đọc đồng thời.
  - `FastLegEvaluatorTests`:
    - Biên `|move| == max` ở cả hai chiều.
    - `age == maxAge` không Stale; `age > maxAge` thì Stale.
    - `span = 0`.
    - Heartbeat cũ ⇒ Down; heartbeat sống + tick cũ ⇒ Stale.
    - Ma trận observe / failOpen / applyToExit cho `BlocksOpen` / `BlocksClose`, gồm D6.
  - `FastLegMmfSamplerTests` (tạo map thật bằng `MemoryMappedFile.CreateNew`):
    - Đọc 64 B, symbol đúng.
    - Khử trùng lặp.
    - Từ chối torn read (spread ≠ ask − bid, `ts` đổi giữa hai lần đọc).
    - Version sai.
    - Map biến mất rồi có lại.
    - Heartbeat `connected=0`.
  - `FastLegStatsTests`.
- **Nghiệm thu:** T2. A6/A7 bật. Chạy 10' với F bật, chưa Start: CPU/RAM ổn định, `[LOGGER][HEALTH]` không có drop.

### Phase 3 — Ghi dữ liệu verdict (engine CHƯA chặn)
- **Làm:**
  - Thêm tham số optional ở cuối:
    - `GapSignalSnapshot(..., FastLegVerdict? FastLeg = null)`.
    - `GapSignalTriggerResult(..., FastLegVerdict? FastLeg = null, int FastLegDeferredTicks = 0, long FastLegDeferredMs = 0)`.
  - VM truyền verdict vào snapshot (L7799). Hai engine gán `FastLeg: snapshot.FastLeg` ở mọi chỗ dựng result production.
    Legacy `ProcessSide` **bỏ qua có chủ đích**, vì không có tham số snapshot và không có đường production `[R-F16]`.
  - Gap-tick: `GapTickLineFormatter.Format(metrics, point, FastLegVerdict? c = null)` chèn `c_bid= c_ask= c_age= c_move= c_verdict=` **trước** ` point=`. F tắt ⇒ chuỗi giống hệt từng byte. Sửa LEGEND trong `TradeSessionFileLogger`.
  - Lifecycle: `SignalLifecycleContext.FastLeg` (gán trong `GetOrCreateSignalContext`). `LogSignalOutcome` thêm `fastVerdict`, `fastMovePts`, `fastAgeMs`, `fastDeferredMs` sau L9576.
  - Signal-outcome: thêm `SignalOutcomeSignal.FastLeg` (cuối). `FormatSignalLine` thêm `fast_*` sau `future_ticks_planned`, chỉ khi có giá trị.
  - Mở → đóng: `_fastLegOpenVerdictByPairId`.
    - Ghi `fastOpenVerdict` / `fastOpenMovePts` vào `SIGNAL_CLOSE_CONFIRMED`.
    - **Prune** khi đóng xác nhận, khi Stop và trong `ResetTradingLogicState` `[R-F16]`.
- **Test mới:**
  - Gap-tick byte-identical khi tắt; khi bật vẫn kết thúc bằng ` point=100`.
  - `SignalGapOutcomeTracker` có và không có `fast_*`.
  - Trigger mang đúng verdict của snapshot.
  - Map mở→đóng được prune.
  - Toàn bộ test cũ PASS.
- **Nghiệm thu:** T3. Phase này chính là chế độ observe. Nếu chủ dự án đồng ý 🛑, chạy demo để thu dữ liệu đầu tiên cho Phase 7.

### Phase 4 — Gate trong engine + trần hoãn (đổi hành vi khi bật F)
- **Làm:**
  - `Application/Services/FastLeg/FastLegDeferState.cs` (struct):
    - Trường: `CycleId`, `FirstDeferUtc`, `EvaluatedTicks`, `LastVerdict`.
    - Các hàm: `Begin`, `Tick`, `Clear`, `IsExpired(maxDeferMs, now)`.
  - **Open** (`GapSignalConfirmationEngine`):
    - Thêm field `_buyFastDefer` / `_sellFastDefer`. Truyền `ref` vào `TryCreateStableOpenResult`.
    - Chèn giữa :245 và :247:
      - `BlocksOpen` ⇒ `Begin`/`Tick`.
      - Nếu `IsExpired` ⇒ `state.Reset("FAST_LEG_DEFER_TIMEOUT")` + `ABANDON` + `Clear`.
      - Sau đó `return null`. Không reset cycle, không tạo GUID, không `LogTrigger`, không ghi observation.
    - Khi phát trigger: gán `FastLegDeferredTicks/Ms`, rồi `Clear`.
      `TriggeredAtUtc` giữ bằng thời điểm của **tick thả** (để không bị `SIGNAL_EXPIRED`) `[R-F3]`.
    - Xoá defer trong các trường hợp `[R-F2]`:
      - Trong `Reset()`.
      - Ở các reset :235 / :243.
      - Ngay sau `Process()` nếu `defer.CycleId ≠ update.CurrentCycle.CycleId`, hoặc cycle không còn Stable ⇒ ghi `ABANDON reason=cycle_reset`.
  - **Normal Close** (`CloseSignalEngine` giữa :333 và :335): `!usesSos && BlocksClose` ⇒ áp cùng cơ chế defer và trần, theo từng slot và từng chiều.
    - SOS đi qua (D5).
    - Slot đang ở `NORMAL_FALLBACK` (SOS active nhưng dùng ngưỡng Normal) **vẫn bị lọc** `[R-F4]`.
  - **TP** (`ProcessTp` giữa :572 và :574): `BlocksClose` ⇒ defer và trần, không `ResetTpCycle`.
    - Khi TP rớt dưới target lúc đang hoãn (:543) ⇒ `ABANDON reason=target_not_reached`.
    - **Ghi nhận `[R-F4]`:** TP bị hoãn trong khi SOS gap phát ⇒ SOS thắng, `CloseReason` chuyển từ Tp sang Gap (router chạy validator SOS). Hành vi này chấp nhận được.
  - Xoá defer close trong `Reset()`, `ResetGapState()` (lật SOS) và `ResetTpCycle`.
  - Log qua `ISlotLogger.LogVerbose`, **mỗi cycle một dòng**:
    - `[FAST_LEG][DEFER] action=OPEN|CLOSE|TP side= slot= cycle= verdict= move_pts= age_ms=`.
    - `[FAST_LEG][RELEASE] ... deferred_ticks= deferred_ms=`.
    - `[FAST_LEG][ABANDON] ... reason=cycle_reset|engine_reset|max_times_tick|target_not_reached|defer_timeout`.
    - Phải thoát trước khi nội suy chuỗi nếu logger null.
- **Rủi ro ghi §6:**
  1. Cycle bị giữ tối đa `maxDeferMs` (≤ 200 mẫu ở mức 10 s). Có trần ⇒ không lặp lại lỗi treo.
  2. Hai chiều Open cùng bị giữ; khi thả có thể cùng phát ⇒ rơi vào `DUAL_SIDE_TRIGGER_DROPPED` (hành vi sẵn có).
  3. Close bị hoãn tới khi hết TooFast hoặc hết `maxDeferMs`. Hết trần ⇒ bỏ signal, chờ cycle close mới. SOS không bị ảnh hưởng.
  4. Open engine không chạy khi: quota đầy, có close eligible, `HasNonAutoCloseInFlight` (:186), hoặc global cooldown (:444) ⇒ trạng thái defer đứng yên.
     `deferred_ms` chỉ đếm tick có đánh giá gate. Kiểm CycleId sẽ dọn trạng thái cũ `[R-F6]`.
  5. Ở tick thả, close thắng sẽ `_openSignalEngine.Reset()` (:432) ⇒ open đang hoãn bị bỏ (`ABANDON engine_reset`) `[R-F5]`.
- **Test mới** (`TradeDesktop.Tests/FastLeg/FastLegEngineGateTests.cs`, dùng helper `Config` / `Process` của `GapStableOpenIntegrationTests` và `GapStableCloseIntegrationTests`):
  - Open TooFast ⇒ null. Tick sau Confirmed ⇒ trigger ngay, không chờ lại hold. CycleId giữ nguyên. `DeferredTicks = 1`. `TriggeredAtUtc` = thời điểm tick thả.
  - Hoãn vượt `maxDeferMs` ⇒ reset + ABANDON. Signal kế tiếp cần cycle mới.
  - Hoãn vượt `open_max_times_tick` ⇒ reset như cũ.
  - Cycle vỡ (delta > tolerance) lúc đang hoãn ⇒ defer bị xoá + ABANDON.
  - `Reset()` từ bên ngoài ⇒ defer bị xoá.
  - Observe ⇒ không chặn, trigger mang TooFast.
  - Open Stale/Down: `failOpen=false` ⇒ hoãn; `failOpen=true` ⇒ phát.
  - Normal Close TooFast + `applyToExit` ⇒ hoãn rồi phát. `applyToExit=false` ⇒ phát ngay. Stale/Down ⇒ **luôn phát** (D6).
  - SOS TooFast ⇒ phát. NORMAL_FALLBACK TooFast ⇒ hoãn.
  - TP TooFast ⇒ hoãn, cửa sổ TP còn nguyên; Confirmed ⇒ phát. TP rớt target ⇒ ABANDON. TP hoãn + SOS gap ⇒ SOS thắng.
  - Lật SOS lúc đang hoãn ⇒ defer close bị xoá.
  - Coordinator:
    - Hai slot Normal cùng hoãn ⇒ không đóng. Thả ⇒ đóng đúng thứ tự Rule D.
    - Slot SOS lợi nhuận thấp + Normal lợi nhuận cao lúc TooFast ⇒ SOS đóng.
    - Tick thả có cả open và close ⇒ close thắng, open ABANDON.
    - Slot bị hoãn vẫn `Live`, không bị claim.
  - Log: đúng 1 DEFER và 1 RELEASE mỗi cycle (`CaptureLogger` của `GapCycleLoggingTests`).
  - **Khoá:** khi `FastLeg = null`, toàn bộ test cũ phải PASS.
- **Nghiệm thu:** unit test xanh. Runtime chạy ở Phase 6 (T4).

### Phase 5 — Router re-check Open, kiểm lúc Start, Telegram
- **Làm:**
  - `Application/Services/FastLeg/FastLegOpenPolicy.Check(verdict) → (bool Allowed, string Code)` (hàm thuần).
    Mã trả về: `FAST_LEG_TOO_FAST`, `FAST_LEG_STALE`, `FAST_LEG_DOWN`. Cố ý tránh dạng `LATEST_*_INVALID`.
  - `TradeExecutionRouter`: thêm tham số ctor optional cuối cùng `IFastLegGate? fastLegGate = null` (DI ở `App.xaml.cs:79`).
    - Kiểm trước :478.
    - **Chỉ log ở lượt sau mutex** (lượt trước mutex chặn im lặng) để không thành 2 dòng WARN `[R-F8]`.
    - Không áp cho Close.
  - Ghi vào docs `[R-F8]`: router chặn ⇒ signal mất (engine đã reset ở :290 và :522), giống mọi khối `LATEST_*`. Debounce 1.5 s cùng chiều vẫn còn hiệu lực.
    Trường hợp này hiếm, vì engine vừa xét cùng verdict vài ms trước. Phase 7 sẽ đếm số lần xảy ra.
  - `SignalLifecycleLogFormatter.DescriptionForReason`: thêm mô tả tiếng Việt cho 3 mã (không đi qua `ResolveGuardReasonCode`).
  - `Application/Services/FastLeg/FastLegHealthTracker.cs` (thuần):
    - Down/Stale liên tục > 30 s ⇒ phát `Down` một lần.
    - Sau đó có tick Confirmed/TooFast ⇒ phát `Recovered`.
    - VM gửi Telegram `FAST_LEG_DOWN` (ERROR) / `FAST_LEG_RECOVERED` (INFO), chỉ khi `IsTradingLogicEnabled`.
  - Lúc Start (sau :1425): `LogFastLegConfiguration()` ghi cấu hình.
    - F filter bật mà map thiếu hoặc Down ⇒ `[FAST_LEG][ERROR]` + Telegram `FAST_LEG_NOT_READY_AT_START`.
    - Kiểm chống va chạm: symbol + `time_msc` của C trùng hệt A hoặc B ⇒ ERROR `[R-F13]`.
    - **Không** chặn Start.
- **Test mới:** `FastLegOpenPolicyTests`, `FastLegHealthTrackerTests` (ngưỡng 30 s, không spam, có Recovered), kiểm có mô tả cho từng reason.
- **Nghiệm thu:** T5 (chạy ở Phase 6).

### Phase 6 — Nghiệm thu demo end-to-end 🛑
- **Điều kiện:**
  - A, B, C đều đã xác minh "Demo Account".
  - Sao lưu config laptop.
  - Đặt `fast_leg` theo thông số Long.
  - Agent có thể tự chạy qua UIA + Supabase REST (ghi nhớ "tự chạy smoke").
- **Kịch bản:**

| Mã | Nội dung | Kỳ vọng |
|---|---|---|
| 6-1 | ≥ 10 cặp Auto | `SIGNAL_OPEN_CONFIRMED` có `fastVerdict=Confirmed` và \|move\| < 5. Có DEFER / RELEASE / ABANDON |
| 6-2 | Close lúc TooFast | Close bị hoãn rồi đóng đủ hai chân, slot được remove |
| 6-3 | SOS lúc TooFast | Vẫn đóng |
| 6-4 | T5 (tắt / ngắt mạng terminal C) | Như mô tả ở T5 |
| 6-5 | Reconnect với `fast_leg=null` | Hành vi cũ; log `fast_leg=disabled` |
| 6-6 | Close per-pair; restart giữa chu kỳ | Không bị F ảnh hưởng |
| 6-7 | Soak 1 h | 0 ERROR mới, không có `long_cycle`, RAM/thread/handle ổn định, `[LOGGER][HEALTH]` không drop, dòng STATS hợp lý |

- **Kết thúc:** cả 3 tài khoản demo flat; config laptop trả lại như bản sao lưu (so khớp JSON).

### Phase 7 — Công cụ phân tích ngưỡng (offline)
- **Làm:** `tools/fast-leg-report.py` (stdlib, chạy với `python -I`). Đầu vào là một phiên: `-gap-tick`, `-trade-log`, `-signal-outcome`. Báo cáo:
  1. Phân bố \|move\| và tuổi tick của C theo giờ. Tỉ lệ Stale khi heartbeat còn sống (dữ liệu cho D9).
  2. % thời gian TooFast / Stale ở các ngưỡng 3–10.
  3. Theo từng lệnh: verdict, \|move\|, `deferred_ms`, slippage A/B, markout `future_gaps` ⇒ edge theo nhóm \|move\| (0–2, 2–4, 4–5, ≥ 5), như slide 10b của Long.
  4. Mô phỏng `max_move = k`: bao nhiêu signal bị chặn và edge còn lại.
  5. Đếm DEFER / RELEASE / ABANDON theo lý do và `FAST_LEG_*` ở router.

  Xuất CSV + tóm tắt markdown.
- **Test:** `tools/test-fast-leg-report.py` trên log mẫu ở `tools/fixtures/fast-leg/`.
- **Nghiệm thu:** chạy trên log Phase 6 ⇒ số lệnh khớp số `SIGNAL_OPEN_CONFIRMED`.

### Phase 8 — Tài liệu + runbook live
- **Làm:**
  - `CLAUDE.md`:
    - Rule E: F chỉ chặn/hoãn.
    - Rule D: lệch chấp nhận với SOS.
    - Pitfall "Sàn C Fast Leg": gate không reset + trần `maxDeferMs`; xoá defer ở mọi reset; SOS bỏ qua; Close fail-open với Down/Stale; record 64 B + chống torn-read; va chạm kênh EA; JSONB riêng; mã `FAST_LEG_*`; gap-tick chèn trước `point=`; router block làm mất signal.
    - §9: thêm dependency.
  - `README.md` §7.1 (cột `fast_leg` + các khoá JSON) và §11.2 (trường `c_*`). `LOGS.md` (`[FAST_LEG]`).
  - `docs/plans/fast-leg/phase-8-live-runbook.md` cho **chủ dự án tự làm**:
    1. Cài terminal C trên VPS (kênh `"C"`, cùng session).
    2. Chạy migration.
    3. UPDATE `fast_leg`. Khuyến nghị chạy `observe` 1–2 ngày, chạy báo cáo Phase 7, rồi mới chuyển `filter`, vì ngưỡng 5 được đo trên cặp sàn THINK–GTC.
    4. Theo dõi STATS và Telegram.
    5. Kill switch.
  - AI không đụng VPS/live.

---

## §5 File chính

| Loại | File |
|---|---|
| Mới — Application | `Models/FastLegConfig.cs`, `Models/FastLegVerdict.cs`, `Services/FastLeg/{FastLegTickBuffer,FastLegEvaluator,FastLegStats,FastLegHealthTracker,FastLegOpenPolicy,FastLegDeferState}.cs` |
| Mới — Infrastructure | `MarketData/FastLegMmfSampler.cs` |
| Mới — App | `Services/IFastLegGate.cs`, `Services/FastLegGate.cs` |
| Sửa | `GapSignalModels.cs`, `GapSignalConfirmationEngine.cs`, `CloseSignalEngine.cs`, `TradeExecutionRouter.cs`, `DashboardViewModel.cs`, `ConfigViewModel.cs`, `MainWindow.xaml`, `SupabaseConfigRepository.cs`, `ConfigRecord.cs`, `ConfigService.cs`, `RuntimeConfigState.cs`, `IRuntimeConfigProvider.cs`, `GapTickLineFormatter.cs`, `TradeSessionFileLogger.cs` (LEGEND), `SignalGapOutcomeTracker.cs`, `SignalLifecycleLogFormatter.cs`, `Infrastructure/DependencyInjection.cs`, `App.xaml.cs` |
| Tài liệu / công cụ | `docs/FAST-LEG-MIGRATION.sql`, `docs/plans/fast-leg/*`, `tools/fast-leg-report.py`, `CLAUDE.md`, `README.md`, `LOGS.md` |
| **Không đụng** | `SharedMemoryMarketDataReader.cs`, `SignalEntryGuard.cs`, `PortfolioCoordinator.cs` (chỉ thêm test), `PositionSlot.cs`, `SlotPersistence.cs`, `SansJsonHelper.cs`, EA MQ5 |

## §6 Nhật ký thực thi (append-only)
| Ngày | Phase | Bước | Commit | Test pass / fail (fail mới?) | Recheck (auto / tay) | Kết luận / quyết định |
|---|---|---|---|---|---|---|
| 2026-10-30 | — | 2. Chốt | 30f38db | — | — | Chủ dự án chốt D1–D9 (MT5 thứ 3, quiet, filter ngay theo thông số Long, gate trong engine, SOS bỏ qua F, Close fail-open với Down/Stale, nhánh riêng, `maxDeferMs` 3000 trong DB, Stale vẫn chặn Open + heartbeat). Plan rà chéo với code (F1–F16) và đã đưa vào thẻ phase |
| 2026-10-30 | 0 | 4. Làm | 30f38db | — | — | Tạo nhánh `feature/exchange-c-fast-leg`; tạo `docs/plans/fast-leg/` (MASTER-PLAN, README, KILL-SWITCH, `tools/recheck.ps1` sinh từ bản TradeLocker: A1 thêm kiểm nhánh + bỏ qua `debug-logs/`, A4 thêm nhóm khoá signal/log, A6 = grep Rule E trên file `*FastLeg*`, A7 = probe MMF C qua P/Invoke `GetTickCount64`) |
| 2026-10-30 | 0 | 0.R baseline + 1. Recheck vào | 30f38db | 1583 / 11 (không fail mới) | auto PASS: A1–A5 PASS, A6/A7 SKIP | Ghi baseline. 17 nhóm khoá A4 xanh toàn bộ. Kiểm chỉ đọc: `MT_A_Tick/_Trades`, `MT_B_Tick/_Trades` có; `MT_C_*` chưa có. **Phát hiện an toàn:** hai terminal MT5 đang chạy đăng nhập `CMCMarkets-MT5-LIVE 39244439` (title có LIVE) ⇒ theo §0.0 coi laptop là THẬT; mọi kịch bản đặt lệnh (Phase 3 tuỳ chọn, Phase 6) bị chặn cho tới khi chủ dự án chuyển cả 3 terminal về demo. **Dừng 🛑 chờ chủ dự án cài terminal C (T0)** |
| 2026-10-30 | 0 | 4. Terminal C (T0) | 30f38db | — | A7 PASS (`sym=XAUUSD tick_age=172ms hb_age=0ms connected=1`) | Chủ dự án: "máy có 4 MT5, tự chọn và setup". Cả 4 đều đăng nhập tài khoản THẬT (CMC LIVE ×2 = A/B, FxPro Live02, Exness Real28). Sàn C chỉ đọc giá ⇒ nằm trong phạm vi "chỉ ĐỌC" của §0.0. Chọn **#4 Exness** (khác broker với A/B). Profile không có chart nào gắn EA; chép 7 file EA, compile 0 error / 0 warning; khởi động với Algo Trading TẮT ở cả terminal lẫn EA. Đo 30 s lúc 11:44 (GMT+7, phiên Á sớm): A `GOLD` 2.2 tick/s, B `GOLDG7` 1.7 tick/s, **C `XAUUSD` 1.4 tick/s** (spread 17 pt); lệch nền giữa C−A = −10 pt. **Nhận xét:** lúc này C KHÔNG dày tick hơn A ⇒ với `maxAgeMs=1000` sẽ có nhiều Stale (D9). Cần đo lại vào phiên Âu/Mỹ trước khi kết luận, cân nhắc so với #3 FxPro. **T0 ✅** |
| 2026-10-30 | 1 | 1. Recheck vào | 30f38db | (A3 không hợp lệ) | A1/A2 PASS · A3 FAIL do agent sửa code TRONG lúc recheck đang build test (race, không phải lỗi baseline) | Cây lúc vào = baseline Phase 0 (A3/A4 xanh 1583/11). Bài học: không sửa code khi recheck đang chạy. Recheck ra chạy đầy đủ trên cây ổn định |
| 2026-10-30 | 1 | 3. Rủi ro | — | — | — | Logic giao dịch: không đổi (chỉ đọc + lưu cấu hình, không ai tiêu thụ ngoài log). Luồng dữ liệu: thêm `fast_leg` vào parse config; cột hỏng ⇒ F tắt, phần còn lại vẫn load (không fail-safe cả config). Side effect: dòng `RuntimeSummary` thêm `FastLeg: ...`, `[DB]` thêm `fast_leg=`, log `[CONFIG] fast_leg=` mỗi lần load |
| 2026-10-30 | 1 | 4–5 Làm + test | (chưa commit) | 1627 / 11 (không fail mới; +44 test) | — | `FastLegConfig` (record bất biến, `TryParse` không ném lỗi, nhận camelCase + snake_case, mặc định theo slide: observe/quiet/10/200/1000; `maxDeferMs` 3000; từ chối map trùng A/B, heartbeat trùng map tick/Trades A/B, `rule≠quiet`, sai kiểu/khoảng; `"enabled":"yes"` cũng báo lỗi — bug tìm được nhờ test). Repository: `TryGetProperty("fast_leg")` + `Clone` ở **cả hai** hàm parse + `ConfigRow.FastLegJson`. `ConfigRecord.FastLegJson`, `ConfigLoadResult.FastLeg/FastLegError` (cuối record, gán qua `with`). `IRuntimeConfigProvider.CurrentFastLegConfig` (default Disabled). `RuntimeConfigState.UpdateFastLeg` (field `volatile`, KHÔNG qua `Update(...)`), gọi ở **cả hai** đường load (Dashboard + ConfigViewModel). Log `[CONFIG] fast_leg=...`, `[FAST_LEG][ERROR] config invalid...`, `RuntimeSummary`, `[DB]`. `docs/FAST-LEG-MIGRATION.sql` (cột `jsonb null`). Test: `FastLegConfigTests`, `FastLegConfigMappingTests` (repository + service thật). Test "Update(...) thiếu tham số không mất F" **không viết được**: project test không tham chiếu tầng App; bẫy sentinel bị loại bằng thiết kế (method riêng) |
| 2026-10-30 | 1 | 6. Runtime T1 | — | — | — | **CHƯA KIỂM — cần:** (1) chủ dự án chạy/cho phép chạy `docs/FAST-LEG-MIGRATION.sql` 🛑; (2) laptop về tài khoản demo (A/B đang CMC LIVE ⇒ §0.0 cấm Start app và sửa config DB của host này). Phần logic T1 (JSON hỏng ⇒ ERROR + tắt, map trùng ⇒ từ chối, Save Config không xoá cột vì PATCH chỉ đụng `sans/hostname/platform_*`) đã phủ bằng test/khảo sát code |
| 2026-10-30 | 1 | 7. Recheck ra | (chưa commit) | 1627 / 11 | auto PASS: A1 WARN (thay đổi của phase), A2–A5 PASS (A4: 18 nhóm gồm `TradeDesktop.Tests.FastLeg.`) | Code Phase 1 xanh. Đề xuất commit: `fast-leg P1: config fast_leg (JSONB) -> runtime, chua dung o dau` |
| 2026-10-31 | 1 | Migration | (chưa commit) | — | REST chỉ đọc: cột `fast_leg` có trên 21/21 host, tất cả NULL | Chủ dự án tự chạy `docs/FAST-LEG-MIGRATION.sql`. Agent chưa ghi gì lên DB |
| 2026-10-31 | 2 | 1. Recheck vào | (chưa commit) | 1627 / 11 | auto PASS: A1 WARN (code P1), A2–A6 PASS, A7 WARN (thứ Bảy: tick C cũ 17 h, `hb_age=0`, `connected=1`) | Terminal 1, 2 vẫn CMC LIVE ⇒ §0.0 cấm Start app và sửa config DB host laptop ⇒ T1/T2 tiếp tục CHƯA KIỂM. Phase 2 chỉ làm phần code + kiểm chứng đọc map |
| 2026-10-31 | 2 | 3. Rủi ro | — | — | — | Logic giao dịch: không đổi (verdict chưa truyền vào engine/router). Luồng dữ liệu: thêm thread sampler riêng + một lần `EvaluateNow` mỗi tick trên UI thread (lock ngắn, không cấp phát buffer); reader A/B không sửa. F tắt: `EvaluateNow` trả null ngay, sampler nghỉ 500 ms/nhịp. Side effect: thêm hàng `Grid.Row=10` (ẩn khi F tắt) |
| 2026-10-31 | 2 | 4–5 Làm + test | (chưa commit) | 1702 / 11 (không fail mới; +75 test) | — | `FastLegVerdict`, `FastLegTickBuffer`, `FastLegEvaluator`, `FastLegStats`, `IFastLegGate`/`IFastLegSampler`, `FastLegGate`, `FastLegMmfSampler` (64 B, header→body→header, checksum spread, heartbeat), DI, VM (`ObserveFastLegVerdict`, `BindFastLegStrip`), dải "Sàn C" trong `MainWindow.xaml`. **Lệch plan có chủ đích:** (1) `FastLegGate` nằm ở Application thay vì App, vì project test không tham chiếu App; (2) `Capture` chỉ trả mẫu tham chiếu + mẫu cuối vì quiet không cần các mẫu giữa, nhờ vậy không cấp phát trên UI thread; (3) `[FAST_LEG][STATS]` chỉ vào file khi có phiên Start (logger không ghi khi chưa có phiên); dải UI vẫn chạy trước Start |
| 2026-10-31 | 2 | 6. Kiểm chứng đọc thật | (chưa commit) | — | Harness scratchpad (không DB, không đường lệnh) dùng đúng sampler + gate, thông số Long | Map thật `Local\MT_C_Tick` đọc đúng (`sym=XAUUSD`, bid 3981.202 / ask 3981.37). Thị trường đóng ⇒ `Stale`, `hb=alive`, `blocksOpen=True`, `blocksClose=False`, `stale_hb_alive_pct=99.6`. Map sai tên ⇒ `Down: map_not_found`. **Chưa thấy trên dữ liệu thật:** Confirmed/TooFast (cần phiên mở, đã phủ bằng unit test). **CHƯA KIỂM:** dải UI hiển thị (chưa chạy app được, chỉ build), tắt EA ⇒ Down trên terminal thật |
| 2026-10-31 | 2 | 7. Recheck ra | (chưa commit) | 1702 / 11 (không fail mới) | Lần 1 **A2 FAIL**: `OpenExisting` trong sampler thêm 1 warning CA1416 (4 > 3). Sửa bằng guard `OperatingSystem.IsWindows()`, không nới baseline | Lần 2 **auto PASS**: A1 WARN (thay đổi của phase), A2–A6 PASS (warning 3 = baseline, 18 nhóm khoá, 8 file FastLeg không tham chiếu đường lệnh), A7 WARN (tick cũ do thị trường đóng). Đề xuất commit: `fast-leg P2: doc gia san C (sampler, verdict, STATS, dai UI), chua chan lenh` |
| 2026-10-31 | 2 | 6b. Kiểm chứng BTC cuối tuần | (chưa commit) | — | Terminal #4 tạm chuyển BTCUSD, harness chỉ đọc (cùng sampler + gate production, thông số Long), bộ ghi tick độc lập + `oracle.py` tính lại verdict từ đặc tả. Đã trả về XAUUSD | **Đạt:** thấy đủ Confirmed/TooFast/Stale trên tick thật; đối chiếu 1529/1529 dòng Confirmed+TooFast (610 + 919): move, kind, blocksOpen/Close đều khớp. Kill terminal ⇒ `Down heartbeat_stale` sau 2.02 s, `blocksOpen=True`, `blocksClose=False` (D6); bật lại ⇒ hết Down trong ~3.5 s. Giá C lệch Coinbase (MCP Playwright) ~13 USD (0.017%), chỉ là kiểm tra thô. **Vấn đề mở (Rủi ro R-TICK, xem dòng dưới).** Ngưỡng 5 pt không có ý nghĩa trên BTC, chỉ dùng để thử code. Vẫn CHƯA KIỂM: dải UI, `connected=0` khi ngắt mạng, T1/T2 |
| 2026-10-31 | 2 | R-TICK | — | — | BTC Exness Real28, ~1 tick/s (`time_msc` cùng nhịp nên tick không rơi ở đoạn map → sampler; chưa tách được do sàn hay do terminal). `p50_age` 547–578 ms so với 59 ms của Long | Với feed thưa: (1) 94–99 % Confirmed có `span=0`, tức `move=0` vì không có tick mới trong cửa sổ, không phải vì giá yên; (2) TooFast đo trên ~1 s chứ không phải 200 ms (`span_p50` ~1040 ms), và chỉ giữ ~200 ms sau mỗi tick; (3) `maxAgeMs=1000` sát nhịp tick nên Stale nhấp nháy 9–13 % thời gian, mỗi lần chặn Open. Code đúng đặc tả; đây là câu hỏi feed có đủ dày không. **Cổng mới trước khi bật filter:** đo XAU phiên Âu/Mỹ trên terminal C, so #3 FxPro. Chưa đổi code |
| 2026-10-31 | 2 | Ghi chú | — | — | Tick XAU cũ 17 h ở dòng kiểm chứng trước | Do laptop vào Modern Standby 18:41 thứ Sáu (hết ngân sách pin), không phải lỗi terminal C. MT5 build 6230 có MCP server ở `127.0.0.1:22346`; cổng do terminal #1 (CMC LIVE) giữ, agent KHÔNG kết nối |
| 2026-10-31 | 2 | Đổi tài khoản | — | — | Title `MainWindowTitle` + journal #1 (`demo account - hedging mode`, 0 vị thế); #2 mới có bằng chứng title, journal chưa flush lúc kiểm. Map A = GOLD, B = GOLDG7, `trades_count=0`, heartbeat sống | Theo yêu cầu chủ dự án: terminal #1, #2 từ CMC LIVE 39244439 sang **CMCMarkets-MT5-DEMO 39509143**. Terminal #4 (C) **vẫn Exness Real 213656047**, chỉ đọc giá, chưa có demo ⇒ T0 chưa đạt theo plan (§0 đòi cả 3 demo). Restart làm HWND của #1, #2 đổi ⇒ phải cập nhật `manualHwndColumns` của laptop trước khi đặt lệnh demo (Phase 6) |
| 2026-11-02 | 2 | Thử LMAX cho C | — | — | Title, journal, netstat, danh sách server của hộp thoại Login, ô "Find your company" | Chủ dự án đưa LMAX-Live 2006138 (`xauusd.lmx`, tài khoản THẬT, chỉ đọc giá). **Chưa đăng nhập được:** MT5 #4 không biết server `LMAX-Live`, và thư mục công ty của MetaQuotes không có LMAX (`lmax.com` chỉ ra địa chỉ thô). Bài học: `/config` trỏ tới server lạ làm title đổi, nhưng terminal không mở socket nào và EA bị gỡ sau 5' (`symbol synchronization timeout`). Đã trả #4 về Exness bằng `fastleg-c-startup.ini`; harness: Confirmed/TooFast/Stale bình thường. Cần tên server hoặc địa chỉ đúng, và xác nhận tài khoản là MT5 |
| 2026-11-02 | 2 | Bước A: A/B về Connext-Demo | — | — | Title, journal, `mmf-probe` (.NET 8; PS 5.1 không mở được map `Local\`), backup config DB | Lúc kiểm, #2 lại đăng nhập **CMC LIVE 39244439** (mở lúc 17:25, không phải do agent). App không chạy, không có lệnh nào. #1 = **538216**, #2 = **538217**, server `Connext-Demo`, title "Demo Account – Hedge". Connext không có GOLD/GOLDG7 ⇒ chart + EA kênh A/B chuyển sang **`XAUUSD.s`**. Preset `fastleg-a/b.set`, startup ini `fastleg-a/b-connext.ini` (không mật khẩu). Map A/B: version 1, `connected=1`, `trades=0`. A và B cùng server nên giá gần trùng ⇒ gap ≈ 0, signal hiếm. EA kênh C dừng lúc 17:24 ⇒ đã bật lại bằng `fastleg-c-startup.ini` (Exness). DB laptop: `fast_leg = NULL`, `max_total_opens=1`, `manualHwndColumns` là HWND cũ (chưa sửa). Backup JSON trong scratchpad |
| 2026-11-02 | 1 | T1 runtime | — | — | App Release build từ nhánh (0 warning), A/B Connext-Demo, switch Open Off, Start qua `start-demo-app.ps1` (kiểm demo + switch trước khi bấm Yes) | **T1 đạt:** sau Reconnect log ghi `[CONFIG] fast_leg=disabled`; phiên chạy ~2 h, 0 ERROR. Signal Open thật bị chặn `SIDE_DISABLED`; HWND cũ ⇒ `WindowMissing` và app tự TẠM DỪNG mở/đóng (dialog "HWND không hợp lệ"). Chưa kiểm: Config→Save giữ `fast_leg`, JSON hỏng |
| 2026-11-03 | 2 | T2 runtime | — | — | Chủ dự án UPDATE `fast_leg` (Exness `XAUUSD`, thông số Long). UIA đọc dải Sàn C; `mmf-probe` đọc map C | **T2 đạt:** dải "Sàn C" hiện cả khi chưa Start và khớp map C (≤ 1 tick). Reconnect ⇒ `[CONFIG] fast_leg=filter|quiet|move5|win200|age1000|defer3000|failOpen0|exit1|…|sym=XAUUSD`. Đóng terminal C ⇒ `[FAST_LEG][WARN] san C Down reason=heartbeat_stale` (≤ 2 s); mở lại ⇒ `[FAST_LEG][INFO] san C hoat dong lai`. STATS mỗi phút (Exness: p50_age 328–375 ms, toofast 22–26 %, stale 5–10 %, `stale_hb_alive_pct = stale_pct` ⇒ Stale đều do feed thưa, không phải terminal chết). Soak 6': CPU 0.35 %, RAM 223→221 MB, handle/thread không tăng, `dropped_* = 0`, 0 ERROR. Gap-tick chưa có `c_*` (đúng, đó là Phase 3). Chưa kiểm: ngắt mạng ⇒ `connected=0`. Ghi chú vận hành: `BM_CLICK` không ăn khi dialog không active ⇒ dùng `WM_COMMAND IDYES`; file `.ps1` có tiếng Việt phải lưu UTF-8 **có BOM** cho PS 5.1 |
| 2026-11-03 | 2 | Thử LMAX lần 2 | — | — | Chủ dự án đưa `36.255.77.152:443`, symbol `XAUUSD.lmx`. Thêm server qua Open an Account (nhập IP), Connect existing account, Save password | **Chưa được.** Title `2006138 - 36.255.77.152:443 - Hedge`, nhưng journal ghi `connection to 36.255.77.152:443 lost` ~2 s sau khi Finish: không có `authorized`, cũng không có `invalid account`. TCP 443 tới IP này mở được (`Test-NetConnection`). Ghi chú: hôm qua #4 (Exness) có socket tới `36.255.76.150:443`, cùng dải `36.255.76–77.x`, nên địa chỉ được đưa có thể là access point của broker khác. #4 đã về Exness bằng `fastleg-c-exness.ini` (login + EA kênh C). **Phụ:** lúc đổi tài khoản, app báo `[FAST_LEG][WARN] san C Down reason=terminal_disconnected` rồi `hoat dong lai` ⇒ nhánh `connected=0` của T2 đã có bằng chứng runtime |
| 2026-11-03 | 3 | Làm + test + T3 | (chưa commit) | 1712 pass / 11 fail = **đúng 11 tên baseline**, 0 fail mới; +10 test `FastLegVerdictPropagationTests` | Build Release 3 warning = baseline 3 | **Làm:**<br>• Thêm tham số optional cuối: `GapSignalSnapshot.FastLeg`, `GapSignalTriggerResult.FastLeg/FastLegDeferredTicks/FastLegDeferredMs`. Gán `FastLeg: snapshot.FastLeg` ở 3 chỗ dựng result production (Open stable, Normal/SOS close, TP); legacy `ProcessSide` cố ý bỏ qua.<br>• VM truyền verdict vào snapshot và gap-tick.<br>• `GapTickLineFormatter` chèn `c_bid/c_ask/c_age/c_move/c_verdict` trước ` point=`; sàn C tắt thì byte-identical (test khoá chuỗi literal). Sửa LEGEND.<br>• `SignalOutcomeSignal.FastLeg/FastLegDeferredMs`; dòng `[SIGNAL]` thêm `fast_*` sau `future_ticks_planned`, chỉ khi có verdict.<br>• Lifecycle: `SignalLifecycleContext.FastLeg`; `LogSignalOutcome` thêm `fastVerdict/fastMovePts/fastAgeMs/fastDeferredMs`. `_fastLegOpenVerdictByPairId` cho `SIGNAL_CLOSE_CONFIRMED` (`fastOpenVerdict/fastOpenMovePts`), prune khi đóng xác nhận, trong `ResetTradingLogicState` (Stop/Reconnect) và khi ≥ 64 entry (bỏ pair không còn slot).<br>**T3 runtime:** phiên 10:35, gap-tick có `c_* … point=100`, LEGEND mới, 0 ERROR, logger `dropped_* = 0`. 11 Open Cycle STARTED nhưng 0 signal trong ~11', vì A/B cùng server Connext nên gap ≈ 0 ⇒ chưa thấy dòng lifecycle `fast*` ở runtime. Map mở→đóng là code VM private, chưa có unit test |
| 2026-11-03 | 3b | Mục "Sàn C" trong Config (mở rộng phạm vi, chủ dự án duyệt) | (chưa commit) | 1725 pass / 11 fail = đúng 11 tên baseline; +13 test `FastLegConfigSaveTests` | Build Release 3 warning = baseline | **Yêu cầu:** sàn C là tuỳ chọn. Tick thì mới chạy, không tick thì app y như cũ.<br>**Làm:**<br>• `ConfigWindow.xaml` thêm hàng 16–18: checkbox "Bật sàn C", các ô Map Name + Check, Symbol, Chế độ Observe/Filter, Max move, Window, Max age, Max defer, Fail open, Áp dụng cho Close thường/TP. Các ô ẩn khi bỏ tick.<br>• `ConfigViewModel` nạp cả cấu hình đang TẮT qua `FastLegConfig.ReadDraft(loadResult.FastLegJson)`; cột bật nhưng hỏng vẫn tick và hiện lỗi.<br>• Save kiểm sàn C TRƯỚC khi ghi gì, bằng chính `FastLegConfig.TryParse`. Ghi RIÊNG cột qua `IConfigRepository.UpdateFastLegAsync` (PATCH chỉ có khoá `fast_leg`), sau đó `UpdateFastLeg` runtime. Bỏ tick: máy đã có cấu hình ⇒ ghi `enabled:false` giữ thông số; máy chưa từng có ⇒ không ghi (cột vẫn NULL).<br>• `FastLegConfig.ToJson/ReadDraft/FormDefaults`: tick lần đầu điền thông số Long ở **observe**.<br>• `FastLegMapProbe` cho nút Check.<br>**Runtime:** mở Config ⇒ nạp đúng DB (tick, XAUUSD, Filter, exit=1). Check ⇒ `✔ XAUUSD 3926.977/3927.145, tuổi 188 ms`. Bỏ tick ⇒ ô ẩn. Đóng không lưu ⇒ DB không đổi. **Chưa thử Lưu trên UI:** Save kiểm HWND trước, HWND laptop trên DB đã cũ ⇒ dừng ở "HWND không hợp lệ". Logic lưu phủ bằng unit test ở tầng repository/service; `TryBuildFastLegSave` trong VM chưa có test riêng |
| 2026-11-03 | 3b | Check tự điền Symbol | (chưa commit) | — (chỉ App) | Build Release 0 warning, 0 lỗi | Ô Symbol trống + bấm Check ⇒ điền symbol EA kênh C đang xuất, kèm nhắc "kiểm tra đúng mã vàng rồi Lưu". Ô đã có giá trị thì KHÔNG ghi đè, chỉ cảnh báo lệch (có thể EA gắn nhầm chart). Runtime: xoá ô ⇒ Check ⇒ `XAUUSD` (✔ … đã tự điền Symbol); gõ `XAUUSD.lmx` ⇒ Check giữ nguyên và báo ⚠ lệch. Đóng không lưu ⇒ DB không đổi |
| 2026-11-03 | 3 | Đánh giá thực tế + rà code độc lập | — | — | 2 phiên (10:35 dài 26', 11:17 dài 13.6'), C = Exness `XAUUSD`, A/B = Connext `XAUUSD.s`, `analyze_gaptick.py` (scratchpad) | **C đi trước A:** tương quan chéo bước giá đạt đỉnh ở độ trễ +150 ms đến +200 ms (r = +0.18, phía âm ≈ 0). C nhảy ≥ 5 pt ⇒ A đi cùng chiều +6.5 đến +7.8 pt trong 500 ms (65–69 %).<br>**Feed C thưa:** 1.3–2.0 lần đổi giá/s (A: 1.9–2.8); tuổi tick p50 390–500 ms, p99 1.7–2.6 s; Stale 10–17 % thời gian (`stale_hb_alive_pct = stale_pct` ⇒ do feed, không phải terminal chết).<br>**Ngưỡng:** \|move\| p90 11–16 pt (Long: 4) ⇒ TooFast 15–23 % ở ngưỡng 5. Bật filter với cấu hình Long sẽ chặn Open khoảng 30–40 % thời gian.<br>Không đo được giá trị lọc trên gap, vì A/B cùng broker.<br>**Lifecycle runtime ✅:** `SIGNAL_OPEN_BLOCKED … fastVerdict=Confirmed fastMovePts=0 fastAgeMs=312 fastDeferredMs=0`; dòng `[SIGNAL]` có `fast_*`.<br>**Ổn định:** CPU 0.5 %, RAM 235 → 235 MB, 0 ERROR, logger `dropped_* = 0`.<br>**Rà code độc lập** (agent, chỉ đọc): Phase 3 sạch. Lỗi ở mục Config Sàn C:<br>(1) `5,5` đọc thành 55 (đã tự xác nhận);<br>(2) Save khi đang tick luôn ghi đè `fast_leg`; `heartbeatMapName` bị suy lại; cấu hình hỏng bị thay bằng mặc định;<br>(3) A/B lưu xong mà Sàn C lỗi ⇒ return trước khi cập nhật runtime A/B; thiếu cột ⇒ exception báo "Save thất bại" dù A/B đã ghi;<br>(4) Save trước khi load xong ⇒ bỏ tick không có tác dụng;<br>(5) `fast_leg` không phải object ⇒ tắt im lặng;<br>(6) `EvaluateNow` chưa có try/catch trước `ProcessSnapshot`.<br>**CHƯA SỬA**, chờ chủ dự án duyệt |
| 2026-11-03 | 3b | Sửa 6 lỗi rà code (chủ dự án duyệt) | (chưa commit) | 1751 pass / 11 fail = đúng 11 tên baseline; +26 test (`FastLegFormLogicTests`, `NonObjectColumn_IsDisabledWithError` ×3) | Build Release 3 warning = baseline | (1) `FastLegFormLogic.TryParseDecimal` chỉ nhận dấu chấm (`AllowDecimalPoint`), `5,5` / `1,000` / số âm bị từ chối kèm thông báo; số nguyên dùng `NumberStyles.None`.<br>(2) Cờ `_fastLegDirty` qua `SetFastLegField`, `FastLegFormLogic.Decide`: không sửa mục Sàn C ⇒ Save KHÔNG ghi cột (giữ heartbeat tuỳ chỉnh, cấu hình hỏng không bị thay bằng mặc định). `heartbeatMapName` lấy lại từ bản đã lưu khi map không đổi.<br>(3) Runtime A/B áp NGAY sau khi A/B lưu xong; Sàn C lưu qua `TrySaveFastLegAsync` có try/catch riêng ⇒ báo "Sàn A/B đã lưu, Sàn C lưu thất bại: …" (kể cả thiếu cột), cửa sổ giữ mở.<br>(4) Chặn Save cho tới khi load DB xong (`_isDbLoadCompleted`); bỏ tick khi runtime đang chạy sàn C ⇒ ghi `enabled=false` dù DB chưa có JSON.<br>(5) Repository giữ mọi kiểu khác null của `fast_leg` ⇒ chuỗi/mảng/bool báo "phải là JSON object" (test cũ `NonObjectColumn_IsTreatedAsMissing` đổi thành kỳ vọng có lỗi).<br>(6) `EvaluateFastLegSafely` bọc `EvaluateNow` + `ObserveFastLegVerdict`; lỗi ⇒ verdict null cho tick đó, log `[FAST_LEG][ERROR]` throttle 60 s.<br>Runtime: dải Sàn C chạy, Config nạp đúng (tick, ✔ đã tải). Lưu trên UI vẫn chưa thử (HWND cũ chặn trước). Chưa làm (nice-to-have): `ProcessSide` legacy, "Down: disabled" thoáng qua khi vừa bật |
| 2026-11-03 | 4 | Gate trong engine + trần hoãn | (chưa commit) | 1773 pass / 11 fail = đúng 11 tên baseline; +21 `FastLegEngineGateTests`; 3 test Phase 3 chuyển sang verdict observe | Build Release 3 warning = baseline | **Rủi ro §0.1:** đổi logic giao dịch CHỈ khi `fast_leg.mode = filter`; observe/null ⇒ y hệt cũ (test `NullFastLeg_EnginesBehaveAsBefore`, `Open_ObserveTooFast_TriggersImmediately` + toàn bộ test engine cũ). Không tạo/gia hạn timer, không đụng quota/cooldown/ownership.<br>**Làm:**<br>• `FastLegVerdict.MaxDeferMs` (optional cuối, evaluator lấy từ config). `FastLegDeferState` (class): `Defer` / `IsExpired` / `Abandon` / `LogDefer` / `LogRelease`, log qua `LogVerbose`, mỗi cycle 1 DEFER + 1 RELEASE hoặc ABANDON.<br>• **Open:** `_buyFastDefer` / `_sellFastDefer`. Gate đặt sau `open_max_times_tick`: `BlocksOpen` ⇒ return null, không reset cycle; quá trần ⇒ `state.Reset` + ABANDON `defer_timeout`. Thả ⇒ `FastLegDeferredTicks/Ms`, `TriggeredAtUtc` = tick thả. `SyncFastDefer` bỏ hoãn khi cycle đổi/vỡ (`cycle_reset`). `max_last_gap` / `max_times_tick` / `Reset()` (`engine_reset`) đều bỏ hoãn.<br>• **Normal Close:** chỉ khi `!usesSos` (NORMAL_FALLBACK vẫn bị lọc) và `BlocksClose` (chỉ TooFast, D6). `ResetGapState` (gồm lật SOS) bỏ hoãn.<br>• **TP:** gate sau `close_max_times_tick`, không reset cửa sổ; `ResetTpCycle` bỏ hoãn với lý do tương ứng (vd `target_not_reached`, `fast_leg_defer_timeout`).<br>**Runtime 10' (filter bật, switch Open tắt):** 0 ERROR, logger không drop. Chỉ 2 Open Cycle, 0 signal ⇒ chưa thấy DEFER/RELEASE ngoài đời: cần signal thật (B khác broker hoặc ngưỡng âm).<br>**Chưa có test coordinator** (Rule D khi nhiều slot cùng hoãn, SOS lợi nhuận thấp đóng trước, close thắng ở tick thả ⇒ open ABANDON qua `Reset`) — mới phủ ở tầng engine |
| 2026-11-03 | 5 | Router + Start + Telegram | (chưa commit) | 1787 pass / 11 fail = đúng 11 tên baseline; +14 (`FastLegPhase5Tests`, 3 mô tả reason) | Build Release 3 warning = baseline | **Rủi ro §0.1:** chỉ chặn thêm Open khi F filter (router), không áp cho Close; không đổi luồng dữ liệu; Telegram là cảnh báo.<br>**Làm:**<br>• `FastLegOpenPolicy.Check` ⇒ `FAST_LEG_TOO_FAST` / `FAST_LEG_STALE` / `FAST_LEG_DOWN`.<br>• `TradeExecutionRouter` thêm `IFastLegGate? fastLegGate = null` (DI theo kiểu ⇒ tự inject), kiểm SAU `LEG_*_NOT_READY`, trước ALLOWED. Chỉ lượt sau mutex ghi `[ROUTER][WARN] Open blocked by fast leg`. Mã đi thẳng vào `PolicyBlockCode` ⇒ `SIGNAL_OPEN_BLOCKED reasonCode=FAST_LEG_*`, mô tả tiếng Việt trong `SignalLifecycleLogFormatter`. `FastLegGate.EvaluateNow` không có state ⇒ gọi từ router thread an toàn.<br>• `FastLegHealthTracker`: Down/Stale liên tục ≥ 30 s ⇒ phát Down một lần; Confirmed/TooFast ⇒ Recovered; F tắt ⇒ reset, không phát. VM gửi Telegram `FAST_LEG_DOWN` (ERROR) / `FAST_LEG_RECOVERED` (INFO), chỉ khi `IsTradingLogicEnabled`.<br>• `LogFastLegConfiguration` lúc Start: `config=…`, `start verdict: …`. Filter + Down ⇒ `FAST_LEG_NOT_READY_AT_START` ERROR + Telegram. Trùng symbol + giá với A/B ⇒ WARN (plan ghi ERROR; hạ xuống WARN vì một tick trùng là bằng chứng yếu). Không chặn Start.<br>**Runtime:** Start 12:27 ⇒ `[FAST_LEG][INFO] config=filter|…|sym=XAUUSD` và `start verdict: verdict=Confirmed … hb=alive`, 0 ERROR.<br>**Chưa chạy:** T5 tắt terminal C > 30 s (sẽ gửi Telegram thật); router chặn Open ngoài đời (cần signal). Router nằm ở project App nên không có unit test (test không tham chiếu App) |
| 2026-11-03 | 7 | Công cụ phân tích ngưỡng | (chưa commit) | 24/24 `test-fast-leg-report.py`; .NET không đổi: 1787 pass / 11 fail = baseline | `recheck.ps1 -Phase 7 -Exit`: A2–A8 PASS (A1 WARN vì chưa commit) | **Rủi ro §0.1:** không có. Tool chỉ đọc log, không đụng code app.<br>**Làm:** `tools/fast-leg-report.py` (stdlib, `python -I`), nhận một hoặc nhiều phiên (tiền tố, hoặc một file bất kỳ của phiên). Xuất `summary.md` + 5 CSV:<br>(1) phân bố \|move\| / tuổi tick C theo giờ, % verdict, `stale_hb_alive_pct` lấy trung bình có trọng số từ STATS;<br>(2) % chặn Open theo ma trận max_move 3–10 × max_age 1000–3000, đúng thứ tự evaluator Down → Stale → TooFast;<br>(3) edge theo nhóm \|move\| 0–2 / 2–4 / 4–5 / ≥ 5, cho cặp đã mở (slip A+B) và cho mọi signal OPEN. Markout = `retained@N` / `decay@N` từ `future_gaps`, tự đổi dấu nhánh SELL;<br>(4) mô phỏng max_move = k;<br>(5) đếm DEFER / RELEASE / ABANDON theo lý do, router `FAST_LEG_*`, lifecycle `reasonCode=FAST_LEG_*`, cảnh báo Down/Recovered.<br>Đối chiếu số dòng `SIGNAL_OPEN_CONFIRMED` với số cặp (khử trùng theo signalId). Cảnh báo khi phiên chạy `filter` (dữ liệu bị cắt tại ngưỡng). Trade-log cũ không có `fast*` thì lấy từ dòng `[SIGNAL]`.<br>Fixture `tools/fixtures/fast-leg/20261103_095900-*` số liệu chọn tay. `recheck.ps1` thêm **A8**.<br>**Chạy trên log thật:**<br>• 4 phiên 09/10 có lệnh demo: 26 `SIGNAL_OPEN_CONFIRMED` = 26 cặp, ghép markout 26/26 (chưa có sàn C ⇒ nhóm n/a).<br>• 6 phiên 02–03/11 có sàn C (186 530 tick): TooFast 23.8 %, Stale 9.4 %, \|move\| p90 16.5 pt. Chặn Open 33.2 % ở cấu hình Long; 25.7 % nếu max_age 1500; 21.5 % nếu max_move 6 + max_age 1500. Down 0 % trong gap-tick nhưng 3.1 % trong STATS, vì STATS đếm cả lúc chưa Start (lúc thử tắt EA cho T2).<br>**Nghiệm thu chính thức** (log Phase 6) còn chờ Phase 6 |
| 2026-11-03 | 8 | Tài liệu + runbook live (làm trước Phase 6, chủ dự án chưa duyệt Phase 6) | (chưa commit) | .NET 1787 pass / 11 fail = baseline; Python 24/24 | `recheck.ps1 -Phase 8 -Exit`: A2–A8 PASS (A1 WARN vì chưa commit) | **Rủi ro §0.1:** không có. Chỉ sửa tài liệu, không đụng code hay DB.<br>**Làm:**<br>• `CLAUDE.md`: Rule D thêm lệch chấp nhận SOS/TooFast (D5). Rule E: sàn C chỉ chặn/hoãn, kèm A6 grep. Pitfall mới "Sàn C Fast Leg": va chạm kênh EA, JSONB riêng, `UpdateFastLeg` thay vì `Update`, Config chỉ ghi khi dirty, verdict một lần mỗi tick, gate không reset + trần `maxDeferMs`, mọi điểm reset phải Abandon, SOS/NORMAL_FALLBACK, router `FAST_LEG_*` làm mất signal, `c_*` trước `point=`, Telegram 30 s, tắt terminal C không phải kill switch, số đo Exness. §9 thêm dependency.<br>• `README.md` §7.1: đoạn Sàn C + bảng 12 khoá `fast_leg`. §11.2: trường `c_*`, `fast_*`, lệnh chạy báo cáo.<br>• `LOGS.md`: mục `[FAST_LEG]` (ví dụ, bảng khi nào ghi, lý do ABANDON lấy từ code, case đặc biệt) + mục lục + dòng Debug by scenario.<br>• `docs/plans/fast-leg/README.md` §7 Vận hành (Config UI, theo dõi, chọn ngưỡng, số đo 03/11); sửa rủi ro va chạm kênh thành WARN cho đúng code. `KILL-SWITCH.md`: cách tắt trong Config, dòng log `[DB]` / `[CONFIG] fast_leg=disabled`.<br>• `phase-8-live-runbook.md` (chủ dự án tự làm): cài terminal C (kênh "C", cùng session, DLL bật, Algo Trading tắt — đúng cấu hình laptop `AllowLiveTrading=0` / `Enabled=0` / `AllowDllImport=1` đang chạy), migration, observe 1–2 ngày, đọc báo cáo, bật filter có kiểm soát, Telegram, kill switch, rollback.<br>Mọi nhãn UI, chuỗi log, reason và giá trị `hb=` đều đối chiếu với code. Phát hiện lúc đối chiếu: nút **Check** chỉ đọc map tick (không đọc heartbeat), nút lưu là **Save**; tài liệu đã ghi đúng theo đó.<br>**Cần rà lại sau Phase 6:** số đo, edge thật, mọi lỗi phát hiện khi đặt lệnh demo |
| 2026-11-03 | 6 | Nghiệm thu demo end-to-end + soak 1 h | (chưa commit) | — (không đổi code) | A/B = Connext-Demo 538216/538217, title "Demo Account" kiểm trước và sau; C = Exness 213656047 chỉ đọc giá. Chủ dự án tự chạy SQL: ngưỡng −100, `min_profit_to_close=0`, HWND mới; `max_total_opens=1`, chỉ bật Open Buy | **Phiên `20261103_141615`** (14:16–15:46, `fast_leg=filter`, thông số Long):<br>• **6-1 ✅** 36 cặp Auto mở, 35 auto close + 1 manual close, cuối phiên A/B flat. Mọi Open có \|move\| < 5 (34 cặp nhóm 0–2, 2 cặp nhóm 4–5). OPEN DEFER 726 / RELEASE 724 / ABANDON 2 (`cycle_reset`).<br>• **6-2 ✅** 5 Normal Close bị hoãn vì TooFast (move −25.6 … +5.8), thả sau 95–455 ms, đóng đủ hai chân.<br>• **6-6 ✅** Close per-pair (`ManualPairClose`, owner=Manual) không bị sàn C ảnh hưởng.<br>• **Router:** 1 lần `SIGNAL_OPEN_BLOCKED reasonCode=FAST_LEG_STALE`: engine phát lúc tuổi tick C đúng 1000 ms (biên, chưa Stale), router re-check vài ms sau thấy > 1000 ⇒ signal mất, đúng [R-F8]; 1/37.<br>• **6-7 ✅ soak 1 h** (14:45–15:45): RAM 176–196 MB, thread 19–24, handle 1216–1243, không tăng. 0 ERROR, 0 `long_cycle`, `[LOGGER][HEALTH] dropped_* = 0`, 0 pending-close retry. STATS: toofast 18–20 %, stale 3–9 % (= `stale_hb_alive_pct` ⇒ do feed), down 0.<br>• **Báo cáo Phase 7:** 36 = 36 KHỚP, markout 36/36. Chặn Open 30.1 % thời gian ở cấu hình Long (gap-tick). Slippage ≈ −20 pt mỗi cặp là do `b_bid_offset_pts=20`, không phải trượt thật. Chưa đánh giá được edge của C: A/B cùng broker nên gap gần như không đổi, nhóm \|move\| lớn chỉ có 2 cặp.<br>**Phát hiện vận hành:**<br>(1) Toolbox MT5 phải ở tab **Trade** thì native close mới đóng lệnh; ở tab Experts close vẫn báo `result=ok` nhưng lệnh không đóng ⇒ pending retry lặp (đã thấy 378 lần trước khi sửa). Không thuộc sàn C.<br>(2) HWND `trade*` phải là list tab Trade (10 cột).<br>(3) Đóng chart thì EA bị gỡ ⇒ giá A/B đứng ⇒ guard price-freeze chặn mọi Open.<br>**Lỗi có sẵn (không sửa, ngoài phạm vi §0.2):** `PortfolioCoordinatorAdapter.BeginWaitAfterClose` gọi `MarkSlotCloseConfirmed` (đặt `LastCloseConfirmedAtUtc`, log POST_CLOSE_LOCK) rồi `ClearCoordinatorPostCloseAnchorForLegacyWait()` xoá ngay ⇒ lock Rule C neo tại confirm không có hiệu lực, log gây hiểu nhầm (thấy Open 0.3 s sau dòng "Block 14s").<br>**Chưa chạy:** 6-3 SOS lúc TooFast (chỉ unit test), 6-4/T5 (theo chủ dự án chỉ cần lý thuyết; notifier đang no-op vì token rỗng; auto mode từ chối tắt terminal C vì là tài khoản thật), 6-5 Reconnect với `fast_leg=null` (cần ghi DB; đã có T1 + unit test), restart giữa chu kỳ.<br>**Kết thúc:** Buy Off, Stop, A/B flat. Chủ dự án đã chạy `phase6-restore.sql`; đọc lại DB khớp bản sao lưu (10/5/10/5, min_profit 100). HWND giữ bản mới, `fast_leg` vẫn `filter` |
| 2026-11-03 | (ngoài phạm vi sàn C) | Sửa lỗi Rule C phát hiện ở Phase 6 (chủ dự án chọn hướng 1) | (chưa commit) | 1791 pass / 11 fail = đúng 11 tên baseline; +4 test `BeginWaitAfterCloseMultiSlotTests` | `recheck.ps1 -Phase 8 -Exit`: A2–A8 PASS (A1 WARN vì chưa commit) | **Rủi ro §0.1:** đổi cooldown theo hướng CHẶT hơn: Auto Open chờ đủ post-close lock tính từ Close confirm (đúng CLAUDE.md Rule C). Không tạo open/close mới, không đụng Manual/Recovery (mốc chỉ đặt khi owner = Auto). Bình thường confirm chỉ trễ ~0.4 s so với dispatch nên gần như không đổi; khác biệt lớn chỉ khi close xác nhận trễ.<br>**Làm:** `PortfolioCoordinatorAdapter.BeginWaitAfterClose` nhánh có `closingPairId` không gọi `ClearCoordinatorPostCloseAnchorForLegacyWait()` nữa; nhánh legacy không pairId giữ nguyên (test tương thích `TradingFlowEngine` không đổi). UI khoá (`RandomLockStatusBuilder`) giờ hiện đúng khoá từ confirm. CLAUDE.md Rule C ghi lại. Chưa chạy runtime |
| 2026-11-03 | 8 | Rà lại tài liệu sau Phase 6 | (xem git log) | — (chỉ tài liệu) | — | **Runbook:** thêm số đo Phase 6 (chặn Open 30 %, hoãn Open p50 ~160 ms / max ~1 s, hoãn Close 95–455 ms, router 1/37, chưa đo được giảm trượt). Thêm bước "đừng đụng cửa sổ MT5 của A/B" (đóng chart ⇒ gỡ EA; Toolbox phải ở tab Trade; mở lại MT5 ⇒ đổi HWND).<br>**Telegram:** `TelegramNotifier` trong repo để trống token từ commit `init` ⇒ MỌI cảnh báo Telegram của app là no-op. Runbook / README sàn C / LOGS.md / CLAUDE.md sửa thành: tín hiệu chắc chắn là dòng `[FAST_LEG][ERROR] FAST_LEG_*` trên panel và `trade-log`; Telegram chỉ khi bản build có token.<br>**CLAUDE.md** mục "Closing wrong trade": pitfall MT5 native close cần Toolbox ở tab Trade + HWND `trade*` là list 10 cột |
| 2026-11-03 | 6b | Rule C runtime + 6-6 restart + 6-5 | (chưa commit) | — (không đổi code) | Build Release mới (có `7131773`), 3 warning = baseline. A/B Connext-Demo, title kiểm trước mỗi Start. Chủ dự án tự chạy `phase6b.sql` từng bước | **Rule C (phiên `20261103_161908`) ✅:** 4/4 lần Open sau Close đều SAU hạn khoá tính từ Close confirm (khoá 13/11/18/17 s ⇒ Open lúc confirm + 14.0/11.5/19.0/17.5 s). Khoá ở `CanOpenNewSlot` (`POST_CLOSE_LOCK (remaining …)`) chặn 338 lần; ở phiên `_141615` bản cũ là 0. 0 ERROR.<br>**6-6 restart giữa chu kỳ (phiên `_163241`) ✅:** Buy Off ⇒ Stop ⇒ thoát app khi cặp `AUTO-0005` đang mở ⇒ mở lại ⇒ Start. `[SLOT][RECOVERY] Restored slot 1` đúng ticket A/B ⇒ 2' sau tự đóng theo signal Gap (owner=Auto, `CLOSE_SELECT` resolve đúng ticket), 0 retry, 0 ERROR. **Ghi nhận (hành vi có sẵn, không sửa):** slot khôi phục lấy `openConfirmedAt` = lúc restart (09:32:41Z) thay vì lúc mở thật (09:31:38Z) ⇒ post-open lock và tuổi slot (ưu tiên `max_life_time`) tính lại từ lúc restart.<br>**6-5 ✅:** `fast_leg.enabled=false` + Reconnect ⇒ `[CONFIG] fast_leg=disabled`; sau đó 0 dòng `[FAST_LEG]`, 0 `fastVerdict`, gap-tick 1049 dòng không còn `c_*` (đúng format cũ), signal vẫn chạy (276 Open cycle trigger), dải "Sàn C" ẩn. Bật lại ⇒ `[CONFIG] fast_leg=filter|…`.<br>**Kết thúc:** ngưỡng trả về 10/5/10/5, min_profit 100, `fast_leg` bật `filter` (đọc lại DB khớp). Stop, A/B flat |
| 2026-11-03 | (ngoài phạm vi sàn C) | Sửa resync mất giờ Open (chủ dự án duyệt) | (chưa commit) | 1792 pass / 11 fail = đúng 11 tên baseline; +1 `RecoverSlotsFromPersisted_RebuildWithKnownOpenTime_KeepsOriginalOpenTime` | `recheck.ps1 -Phase 8 -Exit`: A2–A8 PASS | **Rủi ro §0.1:** slot khôi phục giữ đúng tuổi thật ⇒ có thể đủ điều kiện Close sớm hơn bản cũ (post-open lock, `max_life_time`, hạn gate min_profit) và Open ngược chiều được sớm hơn. Không tạo open/close mới.<br>**Nguyên nhân:** `ApplyResyncedOpenSlots` gán `OpenConfirmedAtUtc = UtcNow` cho mọi slot. Lần chạy thử đầu cho thấy còn đường thứ hai: watchdog self-heal chạy cả khi đã Stop (coordinator bị xoá ⇒ under-count) ⇒ rebuild bằng "bây giờ" rồi persist `watchdog-self-heal` đè lên `current_slots` (cặp mở 10:28:02, DB thành 10:28:16.85 = 0.3 s sau Stop).<br>**Làm:** `_resyncOpenTimeHintByPairId` chụp trong `ResetTradingLogicState` (Start/Stop/Reconnect) trước `Reset()`, làm mới ở mỗi `ApplyResyncedOpenSlots` (chỉ giữ pair đang mở); khi rebuild ưu tiên slot coordinator đang giữ, rồi tới bản chụp, cuối cùng mới "bây giờ". CLAUDE.md mục Config & recovery ghi pitfall.<br>**Runtime ✅ (phiên `20261103_173058` → `_173141`):** cặp mở 10:31:04.5171149Z ⇒ Buy Off ⇒ Stop ⇒ chờ 8 s (watchdog chạy) ⇒ thoát app ⇒ mở lại ⇒ Start ⇒ `[SLOT][RECOVERY] Restored slot 1 … openConfirmedAt=2026-11-03T10:31:04.5171149Z` (trùng từng chữ số), `current_slots` persist đúng giờ đó, cặp tự đóng theo signal lúc 17:33:10, 0 ERROR. Stop, A/B flat |

## Verification tổng
1. Mỗi phase chạy `recheck.ps1 -Phase N` (vào) và `-Exit` (ra).
   - A1–A5 luôn xanh.
   - A6 và A7 từ Phase 2 (A7 SKIP khi không có map, WARN khi tick cũ).
2. `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj`: không có tên fail mới so với baseline; nhóm khoá A4 PASS.
3. Build Release không tăng warning.
4. Runtime trên laptop với 3 terminal **DEMO** đã xác minh (Phase 2, 3, 6). Không bao giờ chạy trên VPS/live.
5. Bằng chứng "F tắt = y hệt cũ":
   - Test byte-identical của gap-tick.
   - Toàn bộ test engine cũ PASS.
   - Kịch bản 6-5.
