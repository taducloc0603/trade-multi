# PrimeXBT sàn B — MASTER PLAN (thực thi + test + recheck)

> **File duy nhất cần mở để làm việc.** Nó cho biết đang ở phase nào, phải recheck gì trước khi làm, làm gì,
> test gì, ghi kết quả ở đâu. Các file phase là **tài liệu chi tiết** — MASTER-PLAN link tới, không thay thế.
>
> Dữ liệu nền: [00-scan-findings.md](00-scan-findings.md) · Kiến trúc/rủi ro/quyết định: [README.md](README.md) ·
> Fixture thật: [fixtures/](fixtures/) · Script recheck: [tools/recheck.ps1](tools/recheck.ps1)

---

## §0 Cách dùng (đọc đầu tiên)

1. Mỗi phiên, chỉ cần nói: **"Chạy tiếp MASTER-PLAN"** (hoặc **"Chạy Phase N theo MASTER-PLAN"**).
2. Đọc **§1 Bảng trạng thái** → chọn phase đầu tiên chưa ✅ mà phụ thuộc đã ✅.
3. Chạy **§2 Vòng lặp chuẩn** (8 bước) với **thẻ phase** ở §4.
4. Ghi mọi kết quả vào **§6 Nhật ký** và cập nhật **§1**.

Quy tắc cứng:
- **Không bao giờ bỏ Bước 1 (recheck)**, kể cả khi phase trước vừa xong trong cùng phiên.
- 🛑 = **dừng, chờ chủ dự án đồng ý trong chính lượt đó**: đặt lệnh (kể cả demo), chọn track, thêm package,
  đổi schema DB, chạy live.
- Không commit nếu chủ dự án chưa yêu cầu trong lượt đó; chỉ đề xuất message `primexbt P<N>: <tóm tắt>`.
- Nhánh `primexbt` thêm **song song**; không refactor nhánh `mt4/mt5/ctrader` (CLAUDE.md §0.2).

---

## §1 Bảng trạng thái (nguồn sự thật duy nhất)

| Phase | Tên | Phụ thuộc | Đặt lệnh? | Chi tiết | Trạng thái | Commit | Ngày |
|---|---|---|---|---|---|---|---|
| S | Quét web + 2 lệnh demo | — | 2 lệnh demo (đã đồng ý) | [00-scan-findings](00-scan-findings.md) | ✅ | — | 2026-10-09 |
| 0 | Spike ngoài app + GO/NO-GO | S | Demo ≤ 60 🛑 | [phase-0](phase-0-spike.md) | ✅ **GO-W** | — (spike ngoài repo) | 2026-10-09 |
| M | Track MT5 của PrimeXBT | 0 = GO-M | Demo 🛑 | [track-m](track-m-mt5.md) | ⏭ bỏ (chọn W) | | |
| 1 | Platform `primexbt` + Null executor | 0 = GO-W | Không | [phase-1](phase-1-platform-enum.md) | ✅ (auto + smoke S1–S4) | df8e971 | 2026-10-09 |
| 2 | Config + token + đăng nhập | 1 | Không | [phase-2](phase-2-config-auth.md) | ✅ (auto + smoke P2-1…P2-6 do agent tự chạy qua UIA) | df8e971 | 2026-10-09 |
| 3 | Lõi giao thức offline | 2 | Không | [phase-3](phase-3-protocol-core-offline.md) | ✅ | df8e971 | 2026-10-09 |
| 4 | Socket + luồng giá (chỉ đọc) | 3 | Không | [phase-4](phase-4-quote-feed.md) | ✅ (A2 rút gọn theo chủ dự án; F4-1 sửa; F4-3 guard N=2) | 116b9c9 | 2026-10-09 |
| 5 | Vị thế mở (chỉ đọc) | 4 | User mở tay trên web | [phase-5](phase-5-open-positions.md) | ✅ | 116b9c9 | 2026-10-09 |
| 6 | Lịch sử (chỉ đọc) | 5 | User mở tay trên web | [phase-6](phase-6-history.md) | ✅ (A1 ở mức map — tab chỉ hiện ticket app) | 4b209e7 | 2026-10-09 |
| 7A | Executor cô lập trên demo | 6 | Demo 🛑 | [phase-7](phase-7-execution.md) | ✅ | (xem §6) | 2026-10-09 |
| 7B | Cặp đầy đủ trên demo | 7A | Demo 🛑 | [phase-7](phase-7-execution.md) | ✅ | (xem §6) | 2026-10-09 |
| 7C | Live | 7B | **Live** 🛑 | [phase-7](phase-7-execution.md) | 🛑 chờ chủ dự án đồng ý (live) | | |
| 8 | Hardening & vận hành | 7C | Theo kịch bản | [phase-8](phase-8-hardening.md) | 🛑 chờ chủ dự án đồng ý (live) | | |

Ký hiệu: ✅ xong · ⏳ làm được · 🔄 đang làm · ❌ fail, đang sửa · ⛔ chưa đủ phụ thuộc · ⏭ bỏ (do chọn track khác).

### Baseline (điền ở Phase 0; chỉ đổi khi chủ dự án duyệt)

| Mục | Giá trị |
|---|---|
| Commit baseline | `0662196` (2026-10-09) |
| Test pass / fail | 1038 / 11 (tổng 1049). CLAUDE.md §6 còn ghi "19 fails" — số thực tế hiện là 11, baseline dùng 11 |
| Danh sách test fail | [tools/baseline-failed-tests.txt](tools/baseline-failed-tests.txt) — 8 `CloseSignalEngineTests`, 1 `PortfolioBlockedSignalTests`, 2 `SosCloseConfigResolverTests` |
| Số warning build Release (`--no-incremental`) | 3 — [tools/baseline-warnings.txt](tools/baseline-warnings.txt) |
| Secret-like có sẵn được phép | 1 (trong `TradeDesktop.Infrastructure/DependencyInjection.cs`, có từ trước dự án) — [tools/baseline-secret-hits.txt](tools/baseline-secret-hits.txt) chỉ lưu đường dẫn + hash |
| `client-version` PrimeXBT lúc spike | (lúc quét: 34866) |
| Kết luận cổng 0 | **GO-W** (2026-10-09, chủ dự án chấp nhận rủi ro Q9) |

---

## §2 Vòng lặp chuẩn — áp dụng cho MỌI phase

| Bước | Việc | Đầu ra | Dừng khi |
|---|---|---|---|
| **1. Recheck vào** | `powershell -ExecutionPolicy Bypass -File docs/plans/primexbt/tools/recheck.ps1 -Phase N` **và** checklist tay §3 cho mọi mục có "Từ phase" < N | Bảng PASS/FAIL ghi vào §6 | Bất kỳ FAIL → quay lại sửa ở phase cũ, **không** bắt đầu phase N |
| **2. Chốt câu hỏi** | Bảng "Chốt trước khi code" trong file phase | Quyết định ghi §6 | 🛑 còn câu hỏi chưa chốt |
| **3. Đánh giá rủi ro** | CLAUDE.md §0.1: có đụng logic giao dịch / luồng dữ liệu / side effect ngoài phạm vi? | 3 dòng kết luận ghi §6 | Có rủi ro chưa được phép → 🛑 báo chủ dự án |
| **4. Làm** | Chỉ đúng mục "Làm" của thẻ phase §4 / file phase | Diff | Gặp việc ngoài phạm vi → ghi lại, hỏi, không tự làm |
| **5. Unit test** | Viết "Test mới" (happy + ≥ 2 edge mỗi method public — CLAUDE.md §6), chạy `dotnet test` | Test mới PASS | Test cũ fail khác baseline |
| **6. Kiểm chứng runtime** | Kịch bản "Nghiệm thu" của phase (demo/smoke) | Bảng ✅ / ❌ / "CHƯA KIỂM — cần X" | Còn ❌ |
| **7. Recheck ra** | `recheck.ps1 -Phase N -Exit` + checklist tay §3 của **chính phase N** | PASS | FAIL |
| **8. Bàn giao** | Cập nhật §1 (trạng thái, ngày), §6; đề xuất commit; cập nhật README/CLAUDE.md nếu có pitfall mới | — | — |

Báo cáo cho chủ dự án **sau Bước 1** (bảng recheck) và **sau Bước 7** (bảng nghiệm thu) — trước khi code và trước khi đóng phase.

---

## §3 Recheck tích luỹ (regression set)

### Tự động — `tools/recheck.ps1` (chạy ở mọi phase)

| ID | Kiểm tra | Áp dụng từ | FAIL khi |
|---|---|---|---|
| A1 | `git status` + `HEAD` | 0 | Có thay đổi ngoài `docs/plans/primexbt/` mà chưa khai báo (chỉ cảnh báo ở Bước 1; ở `-Exit` chỉ liệt kê) |
| A2 | `dotnet build -c Release` | 0 | Build lỗi hoặc số warning > baseline |
| A3 | `dotnet test TradeDesktop.Tests` → tách tên test Failed, so `baseline-failed-tests.txt` | 0 | Có **tên test fail mới** (test cũ fail nay pass chỉ báo INFO) |
| A4 | Test khoá phải PASS: `PlatformNormalizationTests`, `PlatformBSwitchGuardTests`, `ManualPairClosePolicyTests`, `TryClaimSlotClose*`, `ManualClaim_DoesNotPreventAutoFromClaimingAnotherPair`, `PartialOpenRecoverySlot_CannotBeClaimedByAutoClose`, và `TradeDesktop.Tests.PrimeXbt.*` (từ Phase 3) | 0 | Bất kỳ test khoá nào fail |
| A5 | Secret scan: repo (trừ `bin/obj/.playwright-mcp/.git`) + log phiên mới nhất trong `Desktop/trade-log` | 0 | Thấy JWT `eyJ…`, `auth-guard=`, `refresh_token=` |
| A6 | Grep cấm trong `TradeDesktop.*`: `positions/close/all`, `"positions/close"` | 3 | Có xuất hiện |
| A7 | `probe` của spike `scan/primexbt`: so route/trường/kiểu với `fixtures/` | 4 | Có diff (cập nhật [00-scan-findings](00-scan-findings.md) + fixture trước) |

### Tay — tích luỹ (phase N chạy mọi dòng có "Từ phase" < N; Bước 7 chạy thêm dòng của chính N)

| ID | Từ phase | Kiểm tra nhanh | Thời gian |
|---|---|---|---|
| T0 | 0 | Spike `watch` 5 phút ổn; token demo còn hạn ≥ 24 h | 6 phút |
| T1 | 1 | Smoke ma trận: Start 2 phút `mt5/mt5` và `mt5/ctrader`, log không ERROR mới; `platform_b=primexbt` đọc/ghi Supabase không bị thành `mt5`; lệnh B fail rõ "PrimeXBT chưa được kích hoạt" (chỉ đến hết Phase 6) | 10 phút |
| T2 | 2 | Mở cửa sổ Config không reset cấu hình B đang chạy; đăng nhập/đăng xuất PrimeXBT → file token tạo/xoá; log không có token | 5 phút |
| T4 | 4 | B = primexbt: bid/ask khớp web ≤ 1 tick; rút mạng 60 s → `IsConnectedB=false` → cắm lại tự hồi, không bão reconnect | 5 phút |
| T5 | 5 | User mở tay Buy + Sell 0.01 trên web demo → Trades map B có **2** record; trước sync là `MapNotFound`; user đóng tay → record biến mất | 5 phút |
| T6 | 6 | Sau T5: tab History có đúng 2 dòng đóng, profit tự tính (không phải `rpl` 0) | 2 phút |
| T7 | 7 | 1 cặp Auto demo open→close đủ 2 chân; Manual per-pair close đóng đúng pair; tài khoản flat sau test 🛑 | 10 phút |

Nếu một dòng tay không chạy được (vd thiếu máy MT5), ghi "CHƯA KIỂM — cần X" vào §6 và **báo chủ dự án quyết** có cho đi tiếp không.

---

## §4 Thẻ phase

Mỗi thẻ tóm tắt đủ để thực thi; khi cần chi tiết (đường dẫn file, bất biến, tiêu chí đầy đủ) mở file phase.

### Phase 0 — Spike + GO/NO-GO · [chi tiết](phase-0-spike.md)
- **Mục tiêu:** chứng minh client .NET ngoài trình duyệt giữ được `fws`, refresh được token; đo số liệu thật; đánh giá Track M.
- **Chốt trước:** spike C# console tại `C:\Users\laptop\source\tranminhman\scan\primexbt\` (ngoài repo); token lấy qua Chrome thật + CDP; trần ≤ 60 lệnh demo 0.01 oz 🛑.
- **Làm:** lệnh `login`, `watch`, `refresh`, `concurrent`, `reject`🛑, `cycle --n 20`🛑, `drop`🛑, rate nhẹ, `probe`; đọc ToS; Track M: MT5 demo PrimeXBT + EA, so giá 1 h.
- **Test:** chính là các lệnh spike; `recheck.ps1 -Phase 0 -WriteBaseline` tạo baseline.
- **Nghiệm thu:** A1–A9 trong file phase (bắt buộc cho GO-W: A1 socket ≥ 60 phút, A2 refresh ≥ 2 lần, A6 khớp order→position 20/20). Repo `TradeDesktop.*` không đổi.
- **Ra:** memo (§ Memo trong file phase) + điền Baseline §1 + kết luận **GO-W / GO-M / NO-GO** 🛑.

### Track M — MT5 của PrimeXBT · [chi tiết](track-m-mt5.md)
- **Mục tiêu:** cặp A ↔ B(MT5 PrimeXBT) bằng đường MT5 hiện có, **không code mới**.
- **Làm:** tài khoản MT5 demo, EA DataExporter, config `platform_b=mt5` + map/HWND/volume, chạy đọc 1 h, rồi ≥ 5 cặp demo 🛑.
- **Nghiệm thu:** M-A1…M-A4. **Rollback:** trả config broker cũ.

### Phase 1 — Platform `primexbt` · [chi tiết](phase-1-platform-enum.md)
- **Làm:** 5× `NormalizePlatform` **cùng commit**; `PrimeXbtRoutingRules` (map `PRIMEXBT_B_Trades/_History`); `TradeLegPlatform.PrimeXbt = 3`; router ctor/validate/resolve; `NullPrimeXbtTradeExecutor`; reject `platform_a=primexbt`; `PlatformBSwitchGuard`; `ResolveTradeLegPlatform`.
- **Test mới:** normalization 5 bản, routing rules, switch guard, ConfigService reject A, router với Null executor.
- **Nghiệm thu:** 1-A1…1-A4. **Rollback:** revert; đổi `platform_b` trong DB **trước** khi chạy bản cũ (bản cũ đọc `primexbt` thành `mt5`).

### Phase 2 — Config + token + đăng nhập · [chi tiết](phase-2-config-auth.md)
- **Chốt trước:** trường `sans_json.primexbt`; cột `primexbt_confirm_latency_b` (migration SQL 🛑); token DPAPI cục bộ, không lên Supabase; cách đăng nhập (WebView2 → thêm package 🛑, hoặc Chrome CDP).
- **Làm:** `PrimeXbtConfig`, `SansJsonHelper`, repo/record/load-result/service, `RuntimeConfigState` (**sentinel** cho tham số mới), `PrimeXbtTokenStore`, Config UI + login window.
- **Test mới:** `SansJsonPrimeXbtTests`, `PrimeXbtConfigTests`, `PrimeXbtConfirmLatencyBTests`, sentinel overload, `PrimeXbtTokenStoreTests`.
- **Nghiệm thu:** 2-A1…2-A6.

### Phase 3 — Lõi giao thức offline · [chi tiết](phase-3-protocol-core-offline.md)
- **Làm:** `Application/Services/PrimeXbt/`: Envelope, QuoteParser, SymbolResolver (`XAU/USD` ≠ `XAU/USD.24`), PositionsParser (**chỉ sub-position**), TicketCodec (bit 61), OrderPlanner (không có close-all), OrderMatcher (D3), HistoryParser, ErrorMapper. Copy fixture vào `TradeDesktop.Tests/PrimeXbt/Fixtures/`.
- **Test mới:** 6 class trong file phase — đặc biệt hedge 2 sub dưới dòng gộp `qty 0` ⇒ **2** vị thế; codec cTrader⇄PrimeXBT từ chối nhau.
- **Nghiệm thu:** 3-A1…3-A3. Từ phase này A6 bật.

### Phase 4 — Socket + giá · [chi tiết](phase-4-quote-feed.md)
- **Làm:** `PrimeXbtWsTransport` (heartbeat `time` 20 s/16 s, backoff, storm breaker), `PrimeXbtAuthSession` (refresh trước hạn), `PrimeXbtQuoteSession.Read()` fail-closed, nhánh trong `SharedMemoryMarketDataReader`, `PrimeXbtSessionMonitor`, DI.
- **Test mới:** `PrimeXbtQuoteSessionTests` (fake transport), `PrimeXbtReconnectStormTests`, `PrimeXbtAuthSessionTests`, reader B=mt5/ctrader không đổi.
- **Nghiệm thu:** 4-A1…4-A6 (soak demo **24 h**, rút mạng 60 s). Từ phase này A7 bật.

### Phase 5 — Vị thế · [chi tiết](phase-5-open-positions.md)
- **Làm:** `PrimeXbtPositionCache` (Version theo nội dung; `MapNotFound` trước sync/khi rớt; HEDGE bắt buộc), `PrimeXbtTradeSession` (đọc), `PrimeXbtAwareTradesReader`, `ToTradeRecords` (Lot = oz/100), resync/hedge-volume nhận ticket PrimeXBT.
- **Test mới:** `PrimeXbtPositionCacheTests`, `PrimeXbtAwareTradesReaderTests`.
- **Nghiệm thu:** Layer 1 (5-A1, 5-A2) + Layer 2 user mở tay (5-B1…5-B5), switch Auto **tắt**.

### Phase 6 — Lịch sử · [chi tiết](phase-6-history.md)
- **Làm:** `PrimeXbtHistoryProjector` (FIFO 528, profit tự tính), `PrimeXbtAwareHistoryReader`, poll `report/orders2` khi sub-position biến mất + 10 s.
- **Test mới:** `PrimeXbtHistoryProjectorTests`. **Nghiệm thu:** 6-A1…6-A3.

### Phase 7 — Đặt/đóng lệnh · [chi tiết](phase-7-execution.md)
- **Chốt trước:** `Success` = ack không lỗi (vẫn confirm qua Trades map); timeout 5 s ⇒ `TIMEOUT_UNCERTAIN` + đối soát, **không gửi lại**; chỉ đóng bằng `positions/id/close`.
- **Làm:** `SendMarketOrderAsync/ClosePositionAsync` (đường duy nhất), `PrimeXbtReconciler`, `PrimeXbtTradeExecutor`, DI thay Null (giữ Null làm kill switch), lot check cặp đầu.
- **Nghiệm thu:** 7A-1…7A-5 🛑 → 7B-1…7B-8 🛑 (+ test Rule F) → 7C live 🛑. **Dừng ngay** khi có lệnh mồ côi, rollback nhầm, `Unknown`, storm reconnect.

### Phase 8 — Hardening · [chi tiết](phase-8-hardening.md)
- **Làm:** vòng đời token + Telegram; probe drift lúc Start; cảnh báo cuối tuần/HMR (không thêm logic chặn/đóng — Rule E); kịch bản rớt mạng giữa lệnh; tài liệu kill switch; README §7.1 + CLAUDE.md pitfall.
- **Nghiệm thu:** 8-A1…8-A4 (soak 7 ngày qua ≥ 1 lần refresh JWT).

---

## §5 Chiến lược test (3 tầng)

| Tầng | Ở đâu | Chạy khi | Ví dụ bắt buộc |
|---|---|---|---|
| **Unit offline** | `TradeDesktop.Tests/PrimeXbt/*` dùng fixture thật | Mọi phase từ 3 (trong A3/A4) | hedge 2 sub ≠ 0; `XAU/USD.24` không được chọn; qty sai step bị từ chối; codec không va chạm |
| **Fake transport** | `FakePrimeXbtTransport` (đẩy frame, rớt socket, trễ, timeout) | Phase 4–7 | mỗi điều kiện fail-closed của `Read()`; storm breaker; `TIMEOUT_UNCERTAIN` → reconciler |
| **Live demo** | Spike `scan/primexbt` + app với tài khoản DEMO | Nghiệm thu phase 0, 4–8 | soak 24 h; Layer 2 user mở tay; 7B ≥ 10 cặp |

Quy tắc an toàn khi chạm tài khoản: chỉ DEMO trừ 7C; chỉ đóng vị thế do chính phiên mở; **cấm** `close all`;
không in/ghi token, cookie, email; kết thúc mỗi phiên test tài khoản phải **flat** (ghi vào §6).

---

## §6 Nhật ký thực thi (append-only — thêm dòng, không sửa dòng cũ)

| Ngày | Phase | Bước | Commit | Test pass / fail (fail mới?) | Recheck (auto / tay) | Kết luận / quyết định |
|---|---|---|---|---|---|---|
| 2026-10-09 | S | — | — | — | — | Quét xong; 2 lệnh demo Buy/Sell đã đóng, tài khoản flat; tạo plan + fixtures |
| 2026-10-09 | 0 | 0.R2/0.R3 baseline | 0662196 | 1038 / 11 (không có fail mới) | auto PASS: A1–A5 PASS, A6/A7 SKIP · tay: chưa chạy | Tạo MASTER-PLAN + `tools/recheck.ps1`; ghi baseline. Phase 0 vẫn ⏳ (chưa làm spike 0.1–0.13) |
| 2026-10-09 | 0 | 1. Recheck vào | 0662196 | 1038 / 11 (không fail mới) | auto PASS (A1–A5) | Vào Phase 0 |
| 2026-10-09 | 0 | 4–6 spike | 0662196 (repo không đổi) | — | probe OK | Spike dựng tại `scan\primexbt`. Q1 ✅ (JWT + cookie `fws_token`, không cần auth-guard), Q3 ✅, Q6 ✅ (RTT ~240 ms). **Q9 ❌: T&C cấm arbitrage/latency arbitrage (5.2.4, 16.5.16) và bot tự động (5.4)** → dừng thí nghiệm đặt lệnh, chờ chủ dự án quyết 🛑. `watch` 60' đang chạy; Q2 refresh chạy sau |
| 2026-10-09 | 0 | 2. Chốt | — | — | — | **Chủ dự án: "Tiếp tục, tôi chấp nhận rủi ro" (Q9).** Cho phép ≤ 60 lệnh demo cho Q4/Q5/cycle. Quyết định live vẫn hỏi lại ở 7C |
| 2026-10-09 | 0 | 6. Kiểm chứng | 0662196 | — | — | Q4 ✅ (2 dạng lỗi), Q5 ✅ (lệnh vẫn khớp khi rớt socket → đối soát, không gửi lại), D3 ✅ 20/20, Q2 ✅ refresh 2/2 (cookie không xoay, socket cũ không rớt). 46/60 lệnh demo đã dùng, tài khoản flat. Track M bỏ qua (chủ dự án chọn W). Còn A1 (soak 60') đang chạy |
| 2026-10-09 | 1 | Ghi chú quy trình | — | — | — | **Lệch quy trình có chủ đích:** bắt đầu Phase 1 khi Phase 0 chỉ còn A1 (soak thời gian). Nếu A1 FAIL → revert toàn bộ Phase 1 trước khi làm gì khác |
| 2026-10-09 | 1 | 1. Recheck vào | 0662196 | 1038 / 11 | auto PASS (A1–A5); T0 spike watch ổn | Vào Phase 1 |
| 2026-10-09 | 1 | 3. Rủi ro | — | — | — | Logic giao dịch mt4/mt5/ctrader: không đổi (chỉ thêm nhánh). Luồng dữ liệu: **thêm** `CurrentMapName2 = PRIMEXBT_B` khi B=primexbt (kéo từ Phase 2 lên, lý do an toàn: không đọc nhầm MMF EA MT cũ → không có signal giả → không mở chân A rồi rollback). Side effect: bản app CŨ đọc `primexbt` thành `mt5` (đã ghi ở Rollback) |
| 2026-10-09 | 1 | 4–5 Làm + test | (chưa commit) | 1086 / 11 (không fail mới; +48 test) | — | 5× NormalizePlatform; `PrimeXbtRoutingRules`; enum `PrimeXbt=3`; router validate/resolve; `NullPrimeXbtTradeExecutor` + DI; reject A=primexbt; `PlatformBSwitchGuard`; `ResolveTradeLegPlatform`; `CurrentMapName2`. Test: normalization (+primexbt), switch guard (+primexbt), `PrimeXbtRoutingRulesTests` mới |
| 2026-10-09 | 1 | 7. Recheck ra | (chưa commit) | 1086 / 11 | auto PASS (A1 WARN = thay đổi Phase 1) · tay T1/1-A1/1-A2: **CHƯA KIỂM — cần chủ dự án chạy app** | Phần tự động đạt |
| 2026-10-09 | 1→2 | 2. Chốt | — | — | — | Chủ dự án: **dừng chờ smoke tay Phase 1** (hướng dẫn: [phase-1 § Hướng dẫn smoke](phase-1-platform-enum.md#hướng-dẫn-smoke-cho-chủ-dự-án)); **giữ thứ tự 1→2→3**; Phase 2 đăng nhập bằng **WebView2** trong app; latency B lưu trong **`sans_json.primexbt`** (không migration DB) |
| 2026-10-09 | 1 | 6. Smoke (chuẩn bị) | (chưa commit) | — | — | Tạo dòng `configs` cho `laptop-eoj2n95d` (sao chép `win-hfa1234`, [sql/phase1-smoke.sql](sql/phase1-smoke.sql) khối BA). Save Config báo 4 HWND "không tồn tại" — **HWND đúng** (kiểm Win32: chart A 0x00180EBA / trade A 0x00EC0DE0 thuộc MT5-1 tk 538216; chart B 0x000611A6 / trade B 0x000210F6 thuộc MT5-2 tk 538217). Nguyên nhân: `bin\Release` thiếu `mt5engine_capi.dll` → probe nuốt `DllNotFoundException`. Đã chép DLL từ `bin\Debug` (hash khớp). Đề xuất riêng (chưa làm, ngoài phạm vi): log rõ lỗi nạp DLL thay vì báo "cửa sổ không tồn tại" |
| 2026-10-09 | 0 | 7–8 Cổng ra | — | — | A1 ✅ 60' (0 heartbeat timeout) | **Phase 0 đóng: GO-W.** Tick p95 1 021 / p99 1 773 / max 4 579 ms → đề xuất `confirmLatencyB` 2 000 ms; snapshot `positions` rất thưa khi flat (max 6') → liveness dùng heartbeat; lệch đồng hồ −774 ms. Điều kiện "revert Phase 1 nếu A1 FAIL" không còn áp dụng |
| 2026-10-09 | 1 | 6. Smoke (chuẩn bị) | (chưa commit) | — | — | Sau khi chép DLL: Save Config OK — DB `laptop-eoj2n95d`: mt5/mt5, HWND chartA 0x00180EBA, tradeA 0x00EC0DE0, chartB 0x000611A6, tradeB 0x000210F6. Chờ S1–S4 |
| 2026-10-09 | 1 | 2. Chốt | — | — | — | Chủ dự án: laptop dev toàn tài khoản **DEMO** → smoke **bật Auto**, đặt lệnh thật để phủ đủ case. Cập nhật S1 (≥ 1 cặp open→close thật = regression router Phase 1), S3; thêm **4-A7** (chân B Null fail → rollback chân A) vào Phase 4 vì Phase 1 không có giá B nên chưa chạy tới được |
| 2026-10-09 | 1 | 6. Smoke **S1 ✅** | (chưa commit; build Release có Phase 1) | — | log `20261009_120047-trade-log.log`: `[ERROR]`=0, `Invalid/Unsupported platform`=0 | mt5/mt5 demo, config S1-FAST (nạp sau Reconnect 13:33:15). 13:33:15.637 `[ROUTER] Open pair` Mt5/Mt5 → OK 13 ms → `[SLOT][OPEN_CONFIRMED]` ticket A 77460990 / B 77460991; 13:33:46.752 close theo gap (sau hold 30 s) → `[ROUTER] Close pair` đúng ticket + trade HWND → `[SLOT][CLOSE_CONFIRMED]` −71 pt (≈ 2 spread). `POST_CLOSE_OPEN_LOCK` chặn mở lại, `DUAL_SIDE_TRIGGER_DROPPED` đúng thiết kế. Lần đầu không có lệnh vì **chưa Reconnect** sau khi đổi config |
| 2026-10-09 | 1 | 6. Smoke **S2–S4 ✅** | (chưa commit) | — | log `20261009_133834` (S3) + `20261009_134022` (S4): `[ERROR]`=0 | **S2**: sau Config → Save, phiên S3 khởi động với `platform_b=primexbt` (không bị thành mt5). **S3** (13:38:34–13:39:47): map `PRIMEXBT_B_Trades/_History` = False, B không có tick (`tps=-`), không signal, không lệnh; `Resync failed` = fail-closed đúng. **S4**: `platform_b=mt5` → mở cặp 13:40:23 (A 77462099 / B 77462098) → đóng theo gap 13:40:54, −68 pt → khoá mở lại. Ghi chú: DB vẫn còn số **S1-FAST** — cần chạy S1-RESTORE + Reconnect |
| 2026-10-09 | 1 | 7–8 Cổng ra | (chưa commit) | 1086 / 11 (auto, trước đó) | auto PASS · tay S1–S4 ✅ | **Phase 1 đóng ✅.** 1-A1 ✅ (S2) · 1-A2 ✅ (S3) · 1-A3: mt5/mt5 ✅ thật; mt5/ctrader chỉ qua unit test (laptop không có phiên cTrader) · 1-A4 ✅ |
| 2026-10-09 | 2 | 1. Recheck vào | (chưa commit) | 1086 / 11 | auto PASS (A2 lần đầu FAIL do `TradeMulti.exe` khoá `bin\Release` → script đổi sang build `OutDir` tạm, chạy lại PASS); 2.R3 spike refresh ✅ | Vào Phase 2 |
| 2026-10-09 | 2 | 3. Rủi ro | — | — | — | Thêm nhánh, không đổi hành vi mt4/mt5/ctrader: `BuildSans` 5 tham số trả **đúng từng byte** như cũ khi primexbt rỗng (test khoá); `CurrentConfirmLatencyMsBEffective` ưu tiên ctrader như cũ rồi mới tới primexbt; HWND B không bắt buộc khi B=primexbt (`IsExchangeBWithoutHwnd`), kiểm lot cặp đầu vẫn chỉ cTrader. Thêm 2 package đã duyệt: WebView2 1.0.4258.31 (App), ProtectedData 8.0.0 (Infrastructure) |
| 2026-10-09 | 2 | 4–5 Làm + test | (chưa commit) | 1137 / 11 (không fail mới; +51 test) | — | `PrimeXbtConfig` (không có trường `env` — suy từ tiền tố Account D/L); `SansJsonHelper` parse/build khối `primexbt`; `ConfigLoadResult.PrimeXbt` + Save `primeXbt:`; `IRuntimeConfigProvider.CurrentPrimeXbtConfig`; `RuntimeConfigState.UpdatePrimeXbt` (không qua `Update(...)` ⇒ không dính bẫy sentinel); `PrimeXbtRoutingRules.ResolveConfirmLatencyB`; `IPrimeXbtTokenStore` + `PrimeXbtSession` + `PrimeXbtJwt`; `PrimeXbtTokenStore` DPAPI; `PrimeXbtLoginWindow` (WebView2) + `IPrimeXbtLoginDialog`; Config UI radio + panel PrimeXBT. Test: `PrimeXbtConfigTests`, `SansJsonPrimeXbtTests`, `ConfigServicePrimeXbtTests`, `PrimeXbtTokenStoreTests`, `PrimeXbtSessionTests` |
| 2026-10-09 | 2 | 7. Recheck ra (auto) | (chưa commit) | 1137 / 11 | auto PASS (A1 WARN = thay đổi Phase 1–2) · tay 2-A1…2-A5: **CHƯA KIỂM — cần chủ dự án** | |
| 2026-10-09 | 2 | 6. Smoke (agent tự chạy) | (chưa commit) | — | — | Chủ dự án yêu cầu agent tự thao tác: điều khiển app bằng **Windows UI Automation** (PowerShell) + đọc/ghi DB qua Supabase REST (chỉ dòng `laptop-eoj2n95d`); chủ dự án chỉ tự gõ mật khẩu PrimeXBT. Trước đó chạy S1-RESTORE (DB trả về 10/5/125/100…). **P2-1** panel PrimeXBT, ẩn Map/HWND B ✅ · **P2-3** Save không đòi HWND B, DB `platform_b=primexbt`, khối `primexbt` đủ trường, mapNames/HWND MT giữ nguyên, **không token trên DB** ✅ · **P2-4** mở lại giữ đủ 6 giá trị ✅ · **P2-2** lần đầu ❌: lấy được JWT nhưng **không lấy được cookie** — cookie api.primexbt.com đặt theo **path** (`fws_token=/v2/fws`, `ws_token=/v2/pws`, `bws_token=/v2/bws`, `refresh_token=/v2/auth/refresh`) → sửa `PrimeXbtLoginWindow` hỏi từng path → ✅ tự lấy phiên (profile WebView2 giữ đăng nhập), file DPAPI không lộ JWT/cookie, **mở `fws` thật bằng phiên đã lưu: demo=true, HEDGE** · **P2-5** đăng xuất xoá file, đăng nhập lại tự động ✅ · **P2-6** về MT5: DB mt5 + khối primexbt vẫn giữ, Start 60 s `[ERROR]`=0 ✅ |
| 2026-10-09 | 2 | 7–8 Cổng ra | (chưa commit) | 1137 / 11, warning 3 | auto PASS; quét log hôm nay: 0 chuỗi giống token | **Phase 2 đóng ✅.** Ghi nhận ngoài phạm vi (không sửa): app chỉ persist `current_slots` lúc open-confirmed, không lúc close-confirmed → dòng slot cũ trên DB tới lần Start sau (START_RECONCILE dọn) — có từ trước Phase 1 |
| 2026-10-09 | 3 | 1. Recheck vào | (chưa commit) | 1137 / 11 | auto PASS (A6 bật, A4 có nhóm PrimeXbt); 3.R3 probe OK | Vào Phase 3 |
| 2026-10-09 | 3 | 4–5 Làm + test | (chưa commit) | 1215 / 11 (không fail mới; +78 test) | — | `Application/Services/PrimeXbt/`: `PrimeXbtEnvelope` (build/parse, 2 dạng lỗi), `PrimeXbtErrorMapper`, `PrimeXbtSymbolResolver` (khớp chính xác, `.24` không nhầm), `PrimeXbtQuoteParser`, `PrimeXbtPositionsParser` (**chỉ sub-position**, dòng gộp có qty mà thiếu subs ⇒ invalid), `PrimeXbtTicketCodec` (bit 61, loại trừ cTrader/MT), `PrimeXbtTradeSettings` + `PrimeXbtOrderPlanner` (chỉ PlanOpen/PlanClose-by-id), `PrimeXbtOrderReportParser`, `PrimeXbtOrderMatcher` (D3, Ambiguous ⇒ không đoán), `PrimeXbtHistoryProjector` (profit tự tính, rpl chỉ đối chiếu). Fixture thật copy vào `TradeDesktop.Tests/PrimeXbt/Fixtures`. Plan body khớp **byte-đúng** frame web app đã bắt |
| 2026-10-09 | 3 | 7–8 Cổng ra | (chưa commit) | 1215 / 11, warning 3 | auto PASS (A6 PASS) · 3-A2: không tham chiếu mạng/file | **Phase 3 đóng ✅** |
| 2026-10-09 | 1–3 | Commit | df8e971 | — | — | Chủ dự án yêu cầu commit (không push): Phase 0 spike doc + Phase 1–3 |
| 2026-10-09 | 4 | 1. Recheck vào | df8e971 | 1215 / 11 | auto PASS (A1–A7, **A7 probe OK** lần đầu bật) | Vào Phase 4 |
| 2026-10-09 | 4 | 4–5 Làm + test | (chưa commit) | 1246 / 11 (không fail mới; +31 test) | — | `IPrimeXbtQuoteSession` + `PrimeXbtLogMasker`; `IPrimeXbtTransport` + `ClientWebSocketPrimeXbtTransport` (cookie theo path); `PrimeXbtAuthClient.RefreshAsync`; `PrimeXbtFwsSession` (EnsureState idempotent + generation như cTrader, heartbeat 20/16 s, backoff 0.5→10 s chỉ reset khi resolve symbol, ngắt mạch > 5 lần/60 s + tự thử 3 lần/5', refresh JWT trước hạn 24 h, `Read()` fail-closed theo thứ tự, `[STATS]` mỗi phút); nhánh primexbt trong `SharedMemoryMarketDataReader` (nhánh ctrader/MMF không đổi); `PrimeXbtSessionMonitor` (file `{date}-primexbt.log` + Telegram). Test: `PrimeXbtFwsSessionTests` (fake transport + đồng hồ giả), `PrimeXbtLogMaskerTests`, `PrimeXbtReaderTests`. Test bắt được 1 lỗi: `StopTransport` ghi đè status "NGẮT MẠCH" → đã sửa. Live bắt thêm 1 lỗi: `_quoteSequence` không reset khi tăng generation ⇒ cửa sổ `[STATS]` đầu sau mỗi reconnect cộng dồn tick phiên cũ (`ticks=1383`) → sửa + test `StatsTicks_AfterSessionRestart_*` (1247 test) |
| 2026-10-09 | 4 | 6. Kiểm chứng live (agent tự chạy, UIA + REST) | (chưa commit; Release build có Phase 4) | — | — | `fws` nối: demo=true, HEDGE, symbolId 1019, digits 2. **4-A1 ✅** 151/154 mẫu khớp phiên độc lập · **4-A4 ✅** signal bắn, 169× `SIDE_DISABLED`, guard 190/190 PASS, `confirm_latency_ms=2000`, would_skip 0 · **4-A7 ✅** A mở 77470314 → B Null fail → rollback sau 31.6 s → A đóng, slot gỡ, không mở lại · **4-A5 ✅** mt5 ↔ primexbt qua Reconnect · **4-A6 ✅** 0 token trong log. **Phát hiện F4-1** (có từ trước, mọi platform): barrier non-auto không `END` sau rollback một chân ⇒ Auto kẹt tới Stop — **chưa sửa, chờ chủ dự án** ([phase-4 §Phát hiện](phase-4-quote-feed.md#phát-hiện)). DB laptop đã trả số S1-RESTORE; `platform_b=primexbt` để soak |
| 2026-10-09 | 4 | 7. Recheck ra | (chưa commit) | 1246 / 11, warning 3 | auto PASS (A1 WARN = thay đổi chưa commit) | **Chưa đóng:** 4-A2 soak 24 h bắt đầu 14:50 (logic Stopped); 4-A3 cần chủ dự án tắt Wi‑Fi 60 s |

| 2026-10-09 | 4 | 2. Chốt | — | — | — | Chủ dự án: (1) đã tắt Wi‑Fi cho 4-A3; (2) soak chỉ cần 5–10 phút, lúc chạy thực tế sẽ re-check; (3) **đồng ý sửa F4-1** |
| 2026-10-09 | 4 | 6. Kiểm chứng live (tiếp) | (chưa commit) | 1256 / 11 | — | **4-A3 ✅** socket lỗi 14:52:04 ⇒ giá xoá, backoff tới 10 s, 0 ngắt mạch, Telegram mất/có lại, nối lại 14:53:11; phiên câm sau đó bị heartbeat cắt 14:53:47. **4-A2 ✅ rút gọn** (14:38–14:45: 0 rớt, ~155 tick/phút, tuổi tick p95 ≤ 797 ms). Spike chạy song song rớt **cùng thời điểm** với app (14:56:04) ⇒ mạng laptop chập chờn sau khi bật lại Wi‑Fi. **F4-1 đã sửa** (PendingCloseConfirmation, +9 test) — live: barrier END ~1 s sau khi MMF xác nhận. **F4-3 (cần quyết):** B fail liên tục ⇒ Auto lặp mở A/rollback ~33 s một lần, mọi platform. **F4-4:** 4003 No pongs ×2 trước khi tắt Wi‑Fi — thêm 	p_threads/tp_pending vào [STATS], re-check lúc chạy thực tế. DB laptop trả số thường, platform_b=primexbt |
| 2026-10-09 | 4 | 7–8 Cổng ra | (chưa commit) | 1256 / 11, warning 3 | auto PASS (A1 WARN = chưa commit, A7 probe OK) | **Phase 4 đóng ✅** |

| 2026-10-09 | 5 | 1. Recheck vào | (chưa commit) | 1256 / 11 | auto PASS (A7 probe OK); 5.R3 demo HEDGE + flat | Vào Phase 5 |
| 2026-10-09 | 5 | 4–5 Làm + test | (chưa commit) | 1278 / 11 (+22 test) | — | **Khác plan:** không mở socket thứ hai — PrimeXbtFwsSession cài thêm IPrimeXbtTradeSession (positions chung socket ws). PrimeXbtPositionCache (snapshot đầy đủ, content-version, stamp lần-đầu-thấy, snapshot hỏng ⇒ mất sync), ReadTrades fail-closed (chưa kết nối / chưa symbol / chưa snapshot / heartbeat / auth / ≠ HEDGE), PrimeXbtAwareTradesReader bọc ngoài decorator cTrader, event PositionModeInvalid → Telegram PRIMEXBT_NOT_HEDGE, nhánh PrimeXBT cho [HEDGE_VOLUME]. Test: PrimeXbtPositionCacheTests, ReadTrades_*, PrimeXbtAwareTradesReaderTests |
| 2026-10-09 | 5 | 6. Kiểm chứng live | (chưa commit) | — | — | Vị thế demo mở/đóng bằng spike (open/close/list, ngoài app; laptop demo). **5-A1 ✅ 5-B1 ✅** (≤ 1.7 s, ticket bit 61 đúng) **5-B2 ✅** (hedge 2 record) **5-B3 ✅ 5-B4 ✅** (Unpairable ⇒ PAUSE, 0 lệnh) **5-B5 ✅** (mt5/mt5). Ghi nhận hành vi sẵn có: watchdog RESUMED Auto Open sau 5 s dù B còn vị thế mồ côi. Demo đã flat |
| 2026-10-09 | 5 | 7. Recheck ra | (chưa commit) | 1278 / 11, warning 3 | auto PASS (lần đầu A3 FAIL do race trong test mới → sửa test) | Chờ 5-A2 (tắt Wi‑Fi) để đóng Phase 5 |
| 2026-10-09 | 5 | 2. Chốt | — | — | — | Chủ dự án: đã tắt Wi‑Fi cho 5-A2; **F4-3: N = 2** |
| 2026-10-09 | 4–5 | 4–6 F4-3 | (chưa commit) | 1285 / 11 (+7 test) | — | Guard chuỗi partial-open rollback: 2 lần liên tiếp ⇒ CanOpenNewSlot chặn PARTIAL_OPEN_ROLLBACK_STREAK + Telegram OPEN_PARTIAL_ROLLBACK_STREAK; reset khi cặp mở đủ hai chân hoặc Start/Stop/Reconnect. Live: 2 rollback rồi chặn, đúng 2 lệnh Open |
| 2026-10-09 | 5 | 6–8 Cổng ra | (chưa commit) | 1285 / 11, warning 3 | auto PASS | **5-A2 ✅** (map B True→False lúc heartbeat quá hạn, False→True sau snapshot mới). **Phase 5 đóng ✅**. DB laptop trả số thường, demo flat |
| 2026-10-09 | 4–5 | Commit | 116b9c9 | — | — | Chủ dự án: commit rồi làm hết các phase, chỉ nhắn khi có vấn đề |
| 2026-10-09 | 6 | 4–8 | (chưa commit) | 1302 / 11, warning 3 | auto PASS | PrimeXbtHistoryBook + lịch eport/orders2 (sync / 0.5 s sau khi đóng / 10 s) + PrimeXbtAwareHistoryReader. Live: 2 vị thế spike ⇒ 2 record đúng giá/profit (rpl sàn = 0 do làm tròn), idle 6 request/phút. Tab History chỉ hiện ticket app tạo (sẵn có) ⇒ hiển thị kiểm ở 7B. **Phase 6 đóng ✅** |
| 2026-10-09 | 6 | Commit | 4b209e7 | — | — | |
| 2026-10-09 | 7 | 4–7 (7A + 7B demo) | (chưa commit) | 1313 / 11, warning 3, Rule F 8/8 | auto PASS | Executor thật (SendOrderAsync đường duy nhất, không gửi lại, đối soát chỉ báo cáo). **7A** harness dùng đúng session app: 4/4 (7A-4 FILLED đúng). **7B** 26 lệnh Open: ≥ 17 cặp Auto, manual per-pair, external close, restart giữa chu kỳ, partial (qty > max) + F4-3, lot check, Rule A/B/D (2 slot). open→confirm p50 1.5 / p95 2.2 s. **F7-1** khuyến nghị close_pending_time_ms ≥ 2500 cho primexbt (1015 ⇒ retry đóng thừa, vô hại). **F7-2** race rollback vs external-close có từ trước (đóng cùng ticket A hai lần, vô hại). Laptop trả config gốc, demo flat |
---

## §7 Prompt mẫu để bắt đầu một phiên

> Đọc `docs/plans/primexbt/MASTER-PLAN.md`. Chạy phase đầu tiên chưa ✅ theo §2. Sau Bước 1 báo bảng recheck
> trước khi code. Dừng ở mọi 🛑. Không commit.

Biến thể:
- Chỉ recheck: *"Chạy Bước 1 của MASTER-PLAN cho Phase N, không làm gì thêm."*
- Làm lại một phase bị ❌: *"Phase N đang ❌, đọc §6 dòng cuối, sửa rồi chạy lại Bước 5–7."*
