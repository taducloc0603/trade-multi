# hold_confirm_mode: giữ chu kỳ signal theo số tick của chân chậm hơn

> Nhánh `feature/hold-confirm-mode` (tách từ `feature/exchange-c-fast-leg` @ `b154bd7`).
> Tham khảo: `dca-trade-d37` D34, `docs/plans/phase-7-hold-confirm-ticks.md` (chỉ tham khảo, không copy nguyên).

## Mục tiêu
Nhánh TIME hiện tại (`TIME_AND_MIN_SAMPLES`) chỉ công nhận một Cycle gap là Stable khi đủ hai điều kiện:
`MinStableSamples` mẫu, và `open_hold_confirm_ms` / `close_hold_confirm_ms`. Reader poll 50 ms và phát snapshot cả khi
giá không đổi. Vì vậy gap "ảo" vẫn đủ ms và trigger: chân nhanh đã chạy nhưng chân chậm chưa cập nhật.

Thêm mode `ticks`: thay điều kiện thời gian bằng **số tick của chân chậm hơn**. Mode `ms` là mặc định và giữ nguyên
hành vi lẫn từng ký tự log.

## Quyết định (user chốt 2026-11-15)
| Câu hỏi | Chốt |
|---|---|
| Cột DB `public.configs` | `hold_confirm_mode` text `'ms'`\|`'ticks'` default `'ms'` (một cột cho Open + Close), `open_hold_confirm_ticks`, `close_hold_confirm_ticks` int ≥ 0 default 0 |
| Phạm vi mode ticks | Open + Normal Close + SOS Close. TP giữ ms; legacy path (stability null) giữ ms |
| 1 tick | tick chân X = snapshot có (bid,ask) của chân X khác snapshot trước. Đếm `min(tickA, tickB)`; mẫu đầu Cycle không đếm |
| Stable ở mode ticks | đủ `MinStableSamples` **và** đủ N tick chân chậm, rồi Dispersion/Drift như cũ; `*_hold_confirm_ms` bị bỏ qua |
| Mode sai (`tick`, `MS`, `ms `, `''`) | từ chối config (`ConfigLoadResult.Failed`) |
| Chống `_gaps` phình | trần riêng mode ticks: 200 mẫu (hằng số) chưa đủ tick ⇒ NewCycle |
| Config UI / panel Signal Cycle | không đổi; số tick chỉ nằm trong log |
| Smoke | laptop demo, tự chạy thứ Hai 2026-11-16 ~07:15 khi market mở; user allow script trước |

## Cổng chung (G-common), chạy cuối mọi phase
1. Build solution: 0 error. Không thêm warning so với baseline (3 × CA1416 có sẵn ở Infrastructure, đo bằng `--no-incremental`).
2. Chạy `bash docs/plans/hold-confirm-mode/check-tests.sh`, yêu cầu `new_fail=0`. Script so tập test fail với
   `baseline-failures.txt`.
3. Test của các phase trước đều nằm trong suite. `HoldConfirmMsCharacterizationTests` pass **không sửa golden**.
4. `git diff --stat` chỉ chạm đúng các file đã liệt kê trong phase.
5. Ghi nhật ký vào đây, dừng lại báo user. Chỉ commit khi user yêu cầu.

## Phase
| Phase | Nội dung | Trạng thái |
|---|---|---|
| P0 | Baseline + test đặc tả mode ms (golden) | DONE |
| P1 | Model + lõi `GapCycleState` (đếm tick, trần 200) | DONE |
| P2 | `GapTickChangeTracker` + đánh dấu ở `PortfolioCoordinator.ProcessSnapshot` | DONE |
| P3 | Nối engine Open/Normal/SOS + log diagnostics | DONE |
| P4 | Config DB → runtime (fail-closed), log/cảnh báo, migration SQL | DONE |
| P5 | README + CLAUDE.md | DONE |
| P6 | Smoke laptop demo, chạy theo lịch thứ Hai 06:23 (xong trước 07:23, báo cáo 07:24) | DONE (PASS 2026-11-16 06:54) |

Chi tiết từng phase: plan đã duyệt (bản sao nằm trong phiên Claude Code; tóm tắt ở trên).

## Test khoá
- `TradeDesktop.Tests/HoldConfirm/HoldConfirmMsCharacterizationTests.cs` (6 test), so với golden ở
  `TradeDesktop.Tests/HoldConfirm/Golden/ms-*.txt`. Phủ các phần sau:
  - `GapCycleState`: Started, Joined, BecameStable, NewCycle (delta và đổi dấu), ResetTimestamp, ResetConfirmNotSatisfied,
    ResetMissingData, Rejected, BecameUnstable, ResetExplicitly.
  - Log và trigger của engine Open, Normal Close, SOS Close, TP.
  - Dòng `[SIGNAL_OUTCOME]`.
- Id ngẫu nhiên (Guid "N") được chuẩn hoá thành `ID1, ID2…`. Dòng `[GAP_STABILITY_RAW][CONFIG]` khử trùng lặp bằng state
  tĩnh, nên được ghi thành tập giá trị khác nhau; mỗi test dùng một `DiagnosticConfigId` riêng.
- Chỉ sinh lại golden khi CHỦ Ý đổi hành vi mode ms: đặt `HOLD_CONFIRM_GOLDEN_UPDATE=1` rồi chạy test. Không bao giờ làm
  việc này trong lúc triển khai hold_confirm_mode.

## Nhật ký
- 2026-11-15, P0:
  - Lúc 12:28 nhánh làm việc bị chuyển từ fast-leg sang `feature/tradelocker` (bên ngoài phiên). Thay đổi chưa commit ở
    `docs/plans/fast-leg/MASTER-PLAN.md` có trước đó không còn trong working tree. User chọn tạo
    `feature/hold-confirm-mode` từ `feature/exchange-c-fast-leg`.
  - Baseline trên `b154bd7`: build 0 error, 3 warning CA1416. Test 1803, 11 fail (`baseline-failures.txt`); con số 19 ghi
    trong CLAUDE.md đã cũ.
  - Golden sinh trên nhánh tradelocker khớp từng byte trên `b154bd7`. Lý do: Fast Leg tắt thì log mode ms giống hệt.
  - Golden ổn định qua 3 lần chạy riêng và 2 lần chạy cả suite (chạy song song với các test gọi `ResetSummaries`).
  - Gate P0: suite 1809 test, 11 fail, `new_fail=0`.
- 2026-11-15, P1:
  - `HoldConfirmMode` enum. `GapSignalSnapshot.IsNewTickA/B` thêm SAU `FastLeg`, mặc định true.
    `GapSignalConfirmationConfig.HoldConfirmMode/OpenHoldConfirmTicks/CloseHoldConfirmTicks` thêm cuối.
  - `GapCycleState.Process` thêm tham số optional (mode, ticks, cờ A/B).
    - Bộ đếm `_tickCountA/B` tăng ở nhánh join và bị xoá trong `Clear()`, nên mọi điểm reset/StartNewCycle đều xoá đếm.
    - `GapCycleSnapshot.TickCount` = tick chân chậm; được đếm ở cả hai mode nhưng chỉ mode ticks dùng.
    - Trần `TicksModeMaxCollectingSamples = 200`: chỉ áp khi mode ticks, đã đủ mẫu, chưa đủ tick ⇒ NewCycle.
      Đã Stable thì không áp; mode ms không áp.
  - Test: `HoldConfirmTicksTests` (15).
  - Gate P1:
    - 1824 test, 11 fail, `new_fail=0`.
    - Warning khớp baseline (`--no-incremental`).
    - Golden P0 pass, không sửa file mẫu.
    - Diff chỉ gồm 2 file production.
- 2026-11-15, P2:
  - `GapTickChangeTracker.Mark` so (bid,ask) A/B với snapshot trước. Được gọi ở dòng đầu
    `PortfolioCoordinator.ProcessSnapshot`, trước early-return của non-auto barrier.
    `PortfolioCoordinatorAdapter` đi qua coordinator nên cũng được đánh dấu.
  - Test: `GapTickChangeTrackerTests` (8 test). Trong đó có test coordinator chứng minh snapshot bị barrier chặn vẫn
    làm mốc so giá.
  - Gate P2:
    - 1832 test, 11 fail, `new_fail=0`.
    - Warning khớp baseline.
    - Golden P0 pass.
- 2026-11-15, P3:
  - `GapCycleDiagnostics.CreateTimeBranchPolicy` dựng policy chung cho engine Open và cho `ProcessStableCloseSide`
    (Normal + SOS).
  - Mode ticks đổi log như sau:
    - `confirmation_mode=TICKS_AND_MIN_SAMPLES`.
    - `count=n/N hold_ticks=t/T` thay cho `hold=…ms`.
    - Thêm `hold_confirm_ticks=T` ngay sau `hold_confirm_ms=` ở dòng `[GAP_STABILITY]` và RAW.
    - `hold_confirm_ignored=true`.
    - Chữ ký de-dup RAW thêm mode và ticks.
  - Mode ms giữ nguyên chuỗi.
  - **Lệch so với plan:**
    - `PortfolioCoordinator` `[SLOT][TP_CHECK] ... confirmationMode=TIME_AND_MIN_SAMPLES` được GIỮ NGUYÊN, vì đó là log
      của TP mà TP vẫn theo ms.
    - Dòng `[TP_CYCLE]` trong `CloseSignalEngine` cũng giữ nguyên.
    - `EffectiveCloseHoldMs` trên trigger vẫn là giá trị ms của config, không đổi nghĩa.
  - Test: `HoldConfirmTicksEngineTests` (7 test):
    - Open Buy/Sell chờ tick chân chậm.
    - Mode ms với cùng chuỗi vẫn trigger theo thời gian.
    - Normal Close, SOS Close.
    - TP vẫn theo ms.
    - End-to-end qua `PortfolioCoordinator` thật, có tracker tự tính cờ.
  - Gate P3:
    - 1839 test, 11 fail, `new_fail=0`.
    - Warning khớp baseline.
    - Golden P0 pass.
    - `GapCycleLoggingTests`, `GapStable*`, `SosClose*`, `FastLeg*` pass.
    - Sửa line ending `GapCycleDiagnostics.cs` về CRLF như các file khác.
- 2026-11-15, P4:
  - Repository đọc 3 cột ở CẢ HAI khối parse (exact / ilike).
    - `hold_confirm_mode` giữ chuỗi thô. Kiểu khác string được chuyển thành raw JSON để bị từ chối thay vì lặng lẽ về
      ms.
    - Cột thiếu hoặc null ra `null`/0/0.
  - `ConfigRecord` thêm cuối: `HoldConfirmModeRaw`, `OpenHoldConfirmTicks`, `CloseHoldConfirmTicks`.
  - `HoldConfirmModeParser` (strict, ordinal):
    - Mode sai ⇒ `ConfigLoadResult.Failed`.
    - Ticks âm ⇒ cũng Failed, kiểm ở cả hai mode.
  - `ConfigLoadResult` thêm 3 field cuối, gán qua `with { }` theo mẫu FastLeg. **Lệch so với plan:** không thêm tham số
    vào `Success(...)`; hiệu quả như nhau mà ít xâm lấn hơn.
  - Runtime:
    - `RuntimeConfigState.UpdateHoldConfirmMode` là updater riêng, `Update(...)` không đổi chữ ký.
    - Được gọi ở `DashboardViewModel` và `ConfigViewModel`.
    - `IRuntimeConfigProvider` thêm default Ms/0/0.
  - `DashboardViewModel`:
    - Dựng config có 3 field mới.
    - Log `[CONFIG] hold_confirm_mode=…`.
    - `DbInlineData` nối thêm ở cuối.
    - Nhánh lỗi load log `[CONFIG][ERROR] {Error}`. Trước đây Failed chỉ hiện "Không tìm thấy config", không có lý do.
  - `SignalGapOutcomeTracker`: mode ticks ghi `confirmation_mode=TICKS_AND_MIN_SAMPLES open_hold_confirm_ticks=…
    close_hold_confirm_ticks=…`; mode ms giữ nguyên chuỗi.
  - `GapThresholdConfigWarnings`, mode ticks:
    - Cảnh báo khi ticks = 0.
    - Cảnh báo khi confirm ≠ ngưỡng chốt.
    - `close_hold_confirm_ms=0` chỉ còn cảnh báo cho TP.
    - Bỏ cảnh báo `open_hold_confirm_ms=0`, vì cột này bị bỏ qua.
    - Bỏ cảnh báo cặp ngưỡng khi confirm == ngưỡng chốt, vì đó là cấu hình được khuyến nghị; confirm > ngưỡng chốt
      vẫn cảnh báo.
    - Mode ms giữ nguyên toàn bộ.
  - Migration `docs/HOLD-CONFIRM-MODE-MIGRATION.sql`: 3 cột, 2 check constraint, comment, thứ tự triển khai.
  - Test:
    - `Config/HoldConfirmModeConfigMappingTests`: repo exact/ilike/thiếu cột/giá trị thô, service, parser.
    - `HoldConfirm/HoldConfirmWarningsTests`.
  - Gate P4:
    - 1880 test, 11 fail, `new_fail=0`.
    - Warning khớp baseline.
    - Golden P0 pass.
    - Chỉ một chỗ production dựng `GapSignalConfirmationConfig` (DashboardViewModel), đã nối.
- 2026-11-15, P5:
  - README:
    - §3 thêm ghi chú về mode ticks.
    - §7.1 thêm 3 dòng bảng và tiểu mục "hold_confirm_mode", gồm định nghĩa tick, trần 200, log, cấu hình khuyến
      nghị, thứ tự triển khai.
  - CLAUDE.md, mục "Signal cycle & price-freeze": thêm pitfall về chỗ đánh dấu tick duy nhất, `Clear()` xoá bộ đếm,
    trần 200, fail-closed, updater riêng và golden.
  - Không sửa dòng "Pre-existing baseline: 19 fails" trong CLAUDE.md §6, vì nằm ngoài phạm vi; số đo thực tế là 11.
  - Gate P5: 1880 test, 11 fail, `new_fail=0`.
- 2026-11-15, P6a (chuẩn bị smoke):
  - User chốt:
    - Smoke **chỉ quan sát signal**: tắt switch Open Gap Buy/Sell trước Start, không đặt lệnh, nên không vướng HWND
      khi mở lại terminal.
    - **Tạm tắt `fast_leg`** của dòng laptop trong lúc smoke, rồi ghi lại nguyên bản. Không đụng terminal #4, vì đó là
      tài khoản thật.
  - Script `scan/hold-confirm-smoke/run-smoke.ps1` (UTF-8 BOM, CRLF), gồm các bước:
    - `precheck`: chỉ đọc.
    - `dryrun`: chạy luồng UI, không ghi DB.
    - `full`: ms 15' rồi ticks 15', ticks = 1.
    - `restore-db`.
  - Các lớp an toàn của script:
    - Guard demo (#1 = 538216, #2 = 538217, Connext-Demo, "Demo Account") chạy lúc đầu VÀ trước mỗi lần bấm Yes.
    - Terminal #3 đang chạy ⇒ dừng.
    - Không tắt được switch Open ⇒ dừng.
    - Chỉ PATCH dòng `hostname=laptop-eoj2n95d` và 4 cột hold/fast_leg.
    - `state.json` giữ bản gốc; khối `finally` luôn khôi phục DB.
  - Market chưa chạy (A hoặc B đổi giá < 5 lần sau 90 s) ⇒ exit 3 để job hẹn lại.
  - Laptop DB hiện tại:
    - `open_pts`/`confirm` = 10/5, `close` = 10/5, hold 300 ms, min samples 3.
    - Hai demo cùng broker nên gap vượt ngưỡng chủ yếu là gap ảo do lệch tick: đúng loại cần thử.
    - Mode ticks sẽ có `[DB][WARN]` vì confirm ≠ ngưỡng chốt; không đổi ngưỡng trên DB.
  - `precheck` 13:30: migration CHƯA chạy.
  - `dryrun` 13:30–13:38 (không ghi DB, market đóng):
    - Luồng UI chạy trọn: build Release vào `%LOCALAPPDATA%\hold-confirm-smoke\app`, mở terminal demo, guard OK, mở
      app, tắt hai switch, Start/Yes, kiểm market (0 lần đổi giá), Stop, báo cáo, đóng app.
    - Lần 1 lỗi đọc log đang bị khoá; đã sửa sang đọc với `FileShare.ReadWrite`.
    - Bổ sung: `DashboardViewModel.LogHoldConfirmModeConfiguration()` ghi `[CONFIG] hold_confirm_mode=…
      confirmation_mode=…` lúc Start. Lý do: config nạp trước khi phiên log mở, nên trước đây phiên không tự mô tả
      mode.
    - Lần 3 thấy dòng này trong log phiên.
    - `[FAST_LEG][ERROR] map_not_found` là do terminal C không chạy; bản `full` tạm tắt `fast_leg`.
  - Job cron one-shot `f9e312a1` lúc 2026-11-16 07:13, chỉ sống trong phiên Claude Code đang mở.
    - Chạy `precheck` rồi `full` ở chế độ nền.
    - Exit 3 (market đóng) ⇒ hẹn lại sau 30', tối đa 4 lần.
  - Gate cuối P6a: 1880 test, 11 fail, `new_fail=0`; build 0 error.
- 2026-11-15 13:50:
  - User đã chạy migration. `precheck`: DB có cột mới (ms, 0/0); terminal A/B Connext-Demo "Demo Account".
  - User cho phép: **smoke PASS thì tự commit, không push**.
  - Job `f9e312a1` thay bằng `0ff43e67` (07:13 16/11). Job mới thêm bước commit khi PASS:
    - Kiểm nhánh.
    - Ghi nhật ký P6.
    - Chạy `check-tests.sh`.
    - `git add` đúng tập 37 file, đối chiếu với `%LOCALAPPDATA%\hold-confirm-smoke\expected-staged.txt`.
    - `git commit -F %LOCALAPPDATA%\hold-confirm-smoke\commit-msg.txt`.
  - Sửa `.claude/settings.local.json` để thêm allow rule bị chặn (file quyền); user tự thêm nếu cần.
- 2026-11-15 (cập nhật): user đổi yêu cầu.
  - Chạy sớm hơn, vì XAUUSD mở khoảng 06:00 giờ VN.
  - **Xong trước 07:30.**
  - **Không tự commit, chỉ báo cáo**; user tự commit.
  - Job `0ff43e67` thay bằng `d495a454` (06:23 16/11).
  - Market chưa có giá (exit 3) trước 06:50 ⇒ thử lại đúng 1 lần lúc 06:53. Lần này xong khoảng 07:28.
  - `expected-staged.txt` / `commit-msg.txt` chỉ còn để user tham khảo.
- 2026-11-15 14:00–14:15, kiểm tra lại toàn bộ (user yêu cầu):
  - Test: 1880 test, 11 fail đúng baseline, `new_fail=0`.
  - Golden sinh lúc 12:52:47, trước mọi sửa production (sửa đầu tiên lúc 13:06).
  - Build:
    - Debug `--no-incremental`: warning y hệt baseline.
    - Release App: 0 error.
  - Diff:
    - Không file nào thuộc router, executor, quota, cooldown, recovery, Fast Leg, MT4/MT5 hay cTrader.
    - `PortfolioCoordinator` chỉ thêm `Mark`.
    - `Update(...)` không đổi.
    - Chỉ một chỗ dựng `GapSignalConfirmationConfig`.
    - Không chỗ nào so sánh bằng `GapCycleSnapshot`; `TickCount` chỉ được đọc ở nhánh ticks.
  - Supabase: 21/21 host là `ms`/0/0 sau migration, nên không host nào bị từ chối config.
  - V4, app thật ở mode ticks (laptop demo, switch Open tắt, market đóng nên giá đứng yên; phiên `20261115_141046`):
    - Có dòng `[CONFIG] hold_confirm_mode=ticks … TICKS_AND_MIN_SAMPLES`.
    - Open Cycle `hold_ticks=0/1`: 0 COMPLETED, 0 TRIGGERED, 0 ERROR.
    - Trần 200 mẫu bỏ cycle mỗi khoảng 10 s (`count=200/3`).
    - So với mode ms trên cùng giá đứng yên (phiên `20261115_133708`): 55 COMPLETED (`hold=347/300ms`), tức gap ảo
      qua được ở ms nhưng bị chặn ở ticks.
    - DB laptop đã khôi phục, khớp hoàn toàn bản gốc.
  - Phát hiện khi review (đã kiểm chứng):
    - (1) Trần 200 mẫu kết hợp N tick lớn ⇒ không bao giờ Stable. Đo trên demo: khoảng 3,5 tick/s mỗi chân; trong 200 mẫu
      chân chậm có p10 = 25, trung vị 38 tick. Vậy N ≤ ~20 thì an toàn, chưa có cảnh báo khi N lớn.
    - (2) Popup lỗi load config vẫn nói "Không tìm thấy config" (hành vi cũ của mọi Failed); lý do thật chỉ có ở
      `[CONFIG][ERROR]`.
    - (3) Script smoke đếm `stalled_tick_cap` sai mẫu (luôn ra 0); không ảnh hưởng gate.
    - Ngoài ra là các điểm nhỏ: cảnh báo trùng khi confirm > final ở mode ticks, chuỗi `confirmation_mode` lặp ở 2 chỗ,
      `Math.Max` lặp ba lần, một lần cấp phát record mỗi tick ở mode ms.
- 2026-11-15 (cập nhật): user yêu cầu smoke xong trước **07:23** và báo cáo sau đó.
  - Job `d495a454` thay bằng `90110fd5`:
    - Chạy lúc 06:23, xong khoảng 06:58.
    - Market chưa có giá ⇒ thử lại đúng 1 lần lúc 06:47, xong khoảng 07:22.
    - Sau 06:48 thì không khởi chạy nữa.
    - Báo cáo tự đếm số lần chạm trần 200 mẫu qua dòng `[OPEN_CYCLE][RESET] … count=200/`, vì `stalled_tick_cap` của
      script đếm sai.
  - Thêm job báo cáo tổng hợp `55877e0a` lúc 07:24:
    - Chỉ đọc trạng thái: result, `state.json`, TradeMulti, `precheck`, MASTER-PLAN.
    - Không đóng app, không ghi DB, không commit.
- 2026-11-16 06:23–06:54, P6b smoke trên market đang chạy: **SMOKE PASS** (exit 0).
  - Kết quả: `%LOCALAPPDATA%\hold-confirm-smoke\result-20261116_062315.txt`.
  - Precheck 06:23:
    - DB laptop ở `ms`, ticks 0/0.
    - Terminal A 538216 / B 538217 là Connext-Demo "Demo Account".
    - Không có app đang chạy, không có `state.json`.
  - Chạy với switch Open Buy/Sell tắt và ticks = 1/1.
  - So sánh hai mode:

    | | ms (`20261116_062351`) | ticks (`20261116_063908`) |
    |---|---|---|
    | Đổi giá A / B | 2869 / 2853 | 2828 / 2829 |
    | Dòng `[CONFIG]` | `TIME_AND_MIN_SAMPLES` | `TICKS_AND_MIN_SAMPLES` |
    | Open cycle STARTED / RESET / COMPLETED / TRIGGERED | 4 / 3 / 1 / 1 | 2 / 2 / 0 / 0 |
    | Dòng `hold_ticks=0/N` | 0 | 4 |
    | Lần chạm trần 200 mẫu (`[OPEN_CYCLE][RESET]` có `count=200/`) | 0 | 0 |
    | `long_cycle` / logger drop / ERROR / `[DB][WARN]` | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |

  - Gate:
    - `ms_has_time_mode_only`: True.
    - `ticks_config_logged`: True.
    - `ticks_has_ticks_mode`: True.
    - `no_long_cycle_warn`: True.
    - `no_logger_drop`: True.
  - Trigger duy nhất ở mode ms:
    - Thời điểm: 06:24:55.883, Buy, gap 12, `hold=343/300ms`.
    - Trong gap-tick 06:24:55.537 → .880, chân B **đứng yên** ở 3882.19/3882.17; chỉ chân A đổi giá 3 lần.
    - Lúc .927 chân B cập nhật thì gap về −7.
    - Đây là gap ảo do chân A đi trước. Mode ticks với N=1 sẽ không công nhận Stable, vì tick chân chậm = 0, và cycle bị
      reset khi gap đổi dấu.
    - Signal bị chặn ở `SIDE_DISABLED` (switch tắt): không có ROUTER hay dispatch, không đặt lệnh.
  - Ở mode ticks, cả 2 cycle đều reset sau 1 mẫu vì gap đổi dấu. Hai demo cùng broker nên gap thật hiếm, và mẫu này nhỏ.
  - Kết thúc:
    - DB laptop đã khôi phục (`ms`, 0/0, `fast_leg` như cũ).
    - App đã đóng, không còn `state.json`.
  - Chưa commit gì; user tự commit.
- 2026-11-17, P7 phát hành **v1.0.124** (user yêu cầu commit + push + phát hành):
  - Commit `75df93c` (37 file), push `origin/feature/hold-confirm-mode`. Đẩy kèm 7 commit fast-leg/resync chưa từng
    lên origin.
  - Gate trước commit: 1880 test, 11 fail, `new_fail=0`; build Release 0 error.
  - Phát hành bằng `gh workflow run build.yml --ref feature/hold-confirm-mode`, run #124 `success`, đủ 4 asset.
    - CI: 1880 test, 15 fail. Bốn fail thêm là `TradeSignalLogBuilderTests.BuildLogLines_*`: test kỳ vọng giờ GMT+7
      nhưng runner CI chạy UTC. Bốn test này cũng fail ở run 1.0.123, nên trên CI baseline thực tế là 15.
    - Tag `v1.0.124` trỏ `0662196`, là HEAD nhánh mặc định `dev-5-release-111-ctrader`. Đây là hành vi sẵn có của
      `action-gh-release` (tag 1.0.123 cũng vậy). Binary được build từ `75df93c`.
  - Giữ v1.0.124 ở pre-release trong lúc test thực tế exe đã phát hành.
    - SHA256 khớp asset, FileVersion 1.0.124.
    - `run-smoke.ps1` thêm `-AppExe`.
  - Lần chạy 15'+15' bị dừng lúc 10:21 theo yêu cầu cần kết quả trước 10:30. DB đã restore và precheck xác nhận.
  - Chạy lại 3'+3' (`result-20261117_102115.txt`): 4/5 gate True.
    - `ticks_has_ticks_mode=False` vì trong 3' mode ticks gap đứng ở `gap_buy` −13/−14, `gap_sell` +23, không lần nào
      chạm confirm 5, nên không có `[OPEN_CYCLE]` nào.
    - Đây không phải lỗi exe: dòng `[CONFIG]` vẫn ra `TICKS_AND_MIN_SAMPLES`, 0 ERROR, 0 ROUTER.
  - User chấp nhận kết quả ⇒ v1.0.124 thành **Latest** lúc 10:30.
  - Sau đó DB laptop là `ms` 0/0, app đóng, không còn `state.json`.
  - Không deploy VPS/live (Rule 0.0); user tự gửi khách và cập nhật VPS.
