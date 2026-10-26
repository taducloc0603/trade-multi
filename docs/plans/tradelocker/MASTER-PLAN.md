# TradeLocker sàn B — MASTER PLAN (thực thi + test + recheck)

> **File duy nhất cần mở để làm việc.** Nó cho biết đang ở phase nào, phải recheck gì trước khi làm, làm gì, test gì,
> ghi kết quả ở đâu.
>
> Tài liệu kèm theo:
> - Dữ liệu nền: [00-scan-findings.md](00-scan-findings.md)
> - Kiến trúc, quyết định, rủi ro, bản đồ code: [README.md](README.md)
> - Phase 0: [phase-0-spike.md](phase-0-spike.md)
> - Khuôn mẫu đã chạy thành công: [../primexbt/MASTER-PLAN.md](../primexbt/MASTER-PLAN.md)
>
> File phase 1–9 (`phase-N-*.md`) được viết **lúc vào phase đó** (Bước 2), dựa trên kết quả Phase 0. Thẻ phase ở §4 đủ để bắt đầu.

---

## §0 Cách dùng (đọc đầu tiên)

1. Mỗi phiên chỉ cần nói: **"Chạy tiếp MASTER-PLAN tradelocker"** (hoặc **"Chạy Phase N theo MASTER-PLAN tradelocker"**).
2. Đọc **§1** → chọn phase đầu tiên chưa ✅ mà phụ thuộc đã ✅.
3. Chạy **§2** (8 bước) với thẻ phase ở **§4**.
4. Ghi mọi kết quả vào **§6** và cập nhật **§1**.

Quy tắc cứng:
- **Không bao giờ bỏ Bước 1 (recheck)**, kể cả khi phase trước vừa xong trong cùng phiên. **Recheck phase cũ chưa xanh thì không làm phase mới.**
- **CLAUDE.md §0.0 đứng trên mọi thứ**: không bao giờ đặt/đóng lệnh trên tài khoản thật. Trước mọi lệnh phải xác minh
  CẢ HAI chân là demo:
  - TradeLocker: kerber `type`/`accName`.
  - MT5: "Demo Account".
  - Không xác minh được ⇒ dừng.
- 🛑 = **dừng, chờ chủ dự án đồng ý trong chính lượt đó**: đặt lệnh (kể cả demo), thêm package, đổi schema DB, mọi việc liên quan live.
- Không commit nếu chủ dự án chưa yêu cầu trong lượt đó; chỉ đề xuất message `tradelocker P<N>: <tóm tắt>`.
- Nhánh `tradelocker` thêm **song song**; không refactor nhánh mt4/mt5/ctrader/primexbt (CLAUDE.md §0.2).
- Nhánh git: tạo `feature/tradelocker` từ HEAD có primexbt + b-price-offset, **khi bắt đầu Phase 1**.

---

## §1 Bảng trạng thái (nguồn sự thật duy nhất)

| Phase | Tên | Phụ thuộc | Đặt lệnh? | Chi tiết | Trạng thái | Commit | Ngày |
|---|---|---|---|---|---|---|---|
| S | Quét web + source Node | — | Demo (đã làm 2026-10-07) | [00-scan-findings](00-scan-findings.md) | ✅ | — (repo ngoài) | 2026-10-07 |
| 0 | Khảo sát demo + GO/NO-GO | S | Demo ≤ 60 🛑 | [phase-0](phase-0-spike.md) | ✅ **GO** (Q12: chủ dự án chấp nhận rủi ro) | — (ngoài repo) | 2026-10-26 |
| 1 | Khung platform `tradelocker` + Null executor | 0 = GO | Không | §4 | ✅ (auto + smoke S1–S4 agent tự chạy qua UIA) | (chưa commit) | 2026-10-26 |
| 2 | Config + auth + đăng nhập Chrome | 1 | Không | §4 | ✅ (P2-2: đăng nhập 2 bước vì Google chặn Chrome có cổng debug) | (chưa commit) | 2026-10-26 |
| 3 | Lõi giao thức offline (Protobuf + RSocket codec) | 2 | Không (không package — D4 đổi) | §4 | ✅ | (chưa commit) | 2026-10-26 |
| 4 | Socket + luồng giá (chỉ đọc) | 3 | Không | §4 | ✅ live (gồm 4-A3 tắt Wi‑Fi) | (chưa commit) | 2026-10-26 |
| 5 | Vị thế mở → Trades map (chỉ đọc) | 4 | Mở tay trên web demo | §4 | ✅ live (Trades map + resync fail-safe) | (chưa commit) | 2026-10-26 |
| 6 | Lịch sử → History map (chỉ đọc) | 5 | Mở tay trên web demo | §4 | ✅ live (History map available) | (chưa commit) | 2026-10-26 |
| 7A | Executor cô lập trên demo | 6 | Demo 🛑 | §4 | ✅ (phủ qua 7B) | (chưa commit) | 2026-10-26 |
| 7B | Cặp đầy đủ trên demo | 7A | Demo 🛑 | §4 | ✅ (Auto ×3+, đóng ngoài, restart, Close per-pair) | (chưa commit) | 2026-10-26 |
| 8 | Hardening & vận hành | 7B | Demo | §4 | 🔄 code/docs ✅; **soak 24 h demo: CHƯA KIỂM — cần thời gian** | (chưa commit) | 2026-10-26 |
| 9 | Live: runbook cho **chủ dự án tự làm** | 8 | **Live, chỉ user** 🛑 | §4 | 📄 runbook xong ([phase-9-live-runbook.md](phase-9-live-runbook.md)); thực hiện = chủ dự án | — | 2026-10-26 |

Ký hiệu: ✅ xong · ⏳ làm được · 🔄 đang làm · ❌ fail, đang sửa · ⛔ chưa đủ phụ thuộc.

### Baseline (điền ở Phase 0; chỉ đổi khi chủ dự án duyệt)

| Mục | Giá trị |
|---|---|
| Commit baseline | `9a4bd8f` (2026-10-26) |
| Test pass / fail | 1393 / 11 (tổng 1404) |
| Danh sách test fail | [tools/baseline-failed-tests.txt](tools/baseline-failed-tests.txt): 8 `CloseSignalEngineTests`, 1 `PortfolioBlockedSignalTests`, 2 `SosCloseConfigResolverTests` (trùng baseline PrimeXBT) |
| Số warning build Release | 3: [tools/baseline-warnings.txt](tools/baseline-warnings.txt) |
| Secret-like có sẵn được phép | 1: [tools/baseline-secret-hits.txt](tools/baseline-secret-hits.txt) (chỉ đường dẫn + hash) |
| Phiên bản web TradeLocker lúc khảo sát | 3.95.2 (lúc quét 2026-10-07) |
| Kết luận cổng 0 | **GO** (2026-10-26, chủ dự án: "thực hiện tất cả các phase khi nào cần tôi thì thông báo") |

---

## §2 Vòng lặp chuẩn — áp dụng cho MỌI phase

| Bước | Việc | Đầu ra | Dừng khi |
|---|---|---|---|
| **1. Recheck vào** | `powershell -ExecutionPolicy Bypass -File docs/plans/tradelocker/tools/recheck.ps1 -Phase N` **và** checklist tay §3 cho mọi dòng có "Từ phase" < N | Bảng PASS/FAIL ghi vào §6 | Bất kỳ FAIL ⇒ quay lại sửa ở phase cũ, **không** bắt đầu phase N |
| **2. Chốt câu hỏi** | Viết `phase-N-*.md` (nếu chưa có) với bảng "Chốt trước khi code" | Quyết định ghi §6 | 🛑 còn câu hỏi chưa chốt |
| **3. Đánh giá rủi ro** | CLAUDE.md §0.1: có đụng logic giao dịch / luồng dữ liệu / side effect ngoài phạm vi? | 3 dòng kết luận ghi §6 | Có rủi ro chưa được phép ⇒ 🛑 báo chủ dự án |
| **4. Làm** | Chỉ đúng mục "Làm" của thẻ phase | Diff | Gặp việc ngoài phạm vi ⇒ ghi lại, hỏi, không tự làm |
| **5. Unit test** | "Test mới" (happy + ≥ 2 edge mỗi method public, CLAUDE.md §6); `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj` | Test mới PASS | Test cũ fail khác baseline |
| **6. Kiểm chứng runtime** | Kịch bản "Nghiệm thu" của phase | Bảng ✅ / ❌ / "CHƯA KIỂM — cần X" | Còn ❌ |
| **7. Recheck ra** | `recheck.ps1 -Phase N -Exit` + checklist tay §3 của **chính phase N** | PASS | FAIL |
| **8. Bàn giao** | Cập nhật §1, §6; đề xuất commit; cập nhật README/CLAUDE.md nếu có pitfall mới | — | — |

Báo cáo chủ dự án **sau Bước 1** (bảng recheck) và **sau Bước 7** (bảng nghiệm thu).

---

## §3 Recheck tích luỹ (regression set)

### Tự động — `tools/recheck.ps1` (tạo ở Phase 0, chép khung từ `../primexbt/tools/recheck.ps1`)

| ID | Kiểm tra | Từ phase | FAIL khi |
|---|---|---|---|
| A1 | `git status` + `HEAD` | 0 | Có thay đổi ngoài phạm vi phase mà chưa khai báo (Bước 1 cảnh báo; `-Exit` liệt kê) |
| A2 | `dotnet build -c Release` vào `OutDir` tạm (tránh khoá `bin\Release`) | 0 | Build lỗi hoặc warning > baseline |
| A3 | `dotnet test`, tách tên test Failed, so `baseline-failed-tests.txt` | 0 | Có **tên test fail mới** |
| A4 | Test khoá PASS: `PlatformNormalizationTests`, `PlatformBSwitchGuardTests`, `ManualPairClosePolicyTests`, `TryClaimSlotClose*`, `ManualClaim_DoesNotPreventAutoFromClaimingAnotherPair`, `PartialOpenRecoverySlot_CannotBeClaimedByAutoClose`, `TradeDesktop.Tests.PrimeXbt.*`, `BPriceOffset*`, và `TradeDesktop.Tests.TradeLocker.*` (từ Phase 1) | 0 | Bất kỳ test khoá nào fail |
| A5 | Secret scan repo (trừ `bin/obj/.git`) + log phiên mới nhất trong `Desktop/trade-log` | 0 | Thấy `eyJ…`, `refresh_token`, `access_token` có giá trị |
| A6 | Grep cấm trong `TradeDesktop.*` (trừ Tests): `close-all`, `closeAll`, `:close-all` | 3 | Có xuất hiện |
| A7 | Probe: SETUP + subscribe các route đang dùng, so tên trường/kiểu với `fixtures/` | 4 | Có diff ⇒ cập nhật [00-scan-findings](00-scan-findings.md) + fixture trước |

### Tay — tích luỹ (phase N chạy mọi dòng có "Từ phase" < N; Bước 7 chạy thêm dòng của chính N)

| ID | Từ phase | Kiểm tra nhanh | Thời gian |
|---|---|---|---|
| T0 | 0 | Spike C# giữ socket 5 phút; refresh token demo còn hạn | 6 phút |
| T1 | 1 | Start 2 phút `mt5/mt5` và `mt5/primexbt`, log không ERROR mới. `platform_b=tradelocker` đọc/ghi Supabase không bị thành `mt5`. Lệnh B fail rõ "TradeLocker chưa được kích hoạt" (chỉ tới hết Phase 6) | 10 phút |
| T2 | 2 | Mở Config không reset cấu hình B đang chạy; đăng nhập/đăng xuất TradeLocker ⇒ file DPAPI tạo/xoá; log không có token | 5 phút |
| T3 | 3 | Toàn bộ `TradeLocker.*` offline PASS trên fixture; A6 PASS | 2 phút |
| T4 | 4 | B = tradelocker: bid/ask khớp web ≤ 1 tick; rút mạng 60 s ⇒ `IsConnectedB=false` ⇒ cắm lại tự hồi, không bão reconnect; qua ≥ 1 mốc hết hạn JWT 5' không mất giá | 8 phút |
| T5 | 5 | Mở tay Buy + Sell trên web demo ⇒ Trades map B có **2** record; trước sync là `MapNotFound`; đóng tay ⇒ record biến mất | 5 phút |
| T6 | 6 | Sau T5: tab History đúng 2 dòng, cột $ đúng đơn vị tiền | 2 phút |
| T7 | 7 | 1 cặp Auto demo open→close đủ 2 chân; Manual per-pair close đúng pair; tài khoản flat 🛑 | 10 phút |

Dòng tay không chạy được ⇒ ghi "CHƯA KIỂM — cần X" vào §6 và **báo chủ dự án quyết**.

---

## §4 Thẻ phase

### Phase 0 — Khảo sát demo + GO/NO-GO · [chi tiết](phase-0-spike.md)
- **Mục tiêu:** trả lời Q1–Q13; chứng minh .NET giữ được hermes; thu fixture; ghi baseline.
- **Làm:**
  - Node `scan\trade-locker` thêm `soak` và `probe`.
  - Console C# ngoài repo `scan\tradelocker-spike` (SETUP + quotes + positions).
  - Lệnh demo cho Q2/Q6/Q8/Q9 🛑.
  - Đọc T&C (Q12).
  - Tạo `tools/recheck.ps1` + baseline.
- **Nghiệm thu:** 0-A1…0-A9. Repo `TradeDesktop.*` không đổi.
- **Ra:** memo + Baseline §1 + **GO / NO-GO** 🛑.

### Phase 1 — Khung platform `tradelocker`
- **Làm:**
  - 5 bản `NormalizePlatform` **cùng commit**.
  - `TradeLockerRoutingRules` (`ChannelMapName = "TRADELOCKER_B"`, `IsTradeLockerPlatform`, `IsTradeMap`, `IsHistoryMap`).
  - `TradeLegPlatform.TradeLocker = 4`; router validate/resolve.
  - `NullTradeLockerTradeExecutor` + DI.
  - Reject `platform_a=tradelocker`; `PlatformBSwitchGuard`; `ResolveTradeLegPlatform`.
  - `CurrentMapName2 = TRADELOCKER_B` (không đọc nhầm MMF EA cũ ⇒ không có signal giả).
  - `TradeLockerTicketCodec` (bit 63, `Encode`/`TryDecode`/`IsTradeLockerTicket`).
- **Test mới:**
  - Normalization (5 bản).
  - Routing rules, switch guard, ConfigService reject A, router với Null executor.
  - Codec: round-trip; không decode được ticket cTrader/PrimeXBT/MT và ngược lại; round-trip qua `SlotPersistence` JSON và chuỗi `current_tick_b`.
- **Nghiệm thu:**
  - Save Config `tradelocker` ⇒ DB giữ nguyên chữ.
  - B=tradelocker ⇒ không tick, không signal, không lệnh.
  - `mt5/mt5` 1 cặp demo open→close vẫn đúng (laptop demo, đã xác minh).
- **Rollback:** đổi `platform_b` trong DB **trước** khi chạy bản cũ (bản cũ đọc `tradelocker` thành `mt5`).

### Phase 2 — Config + auth + đăng nhập
- **Làm:**
  - `TradeLockerConfig`: `accountId`, `brand` (mặc định `PRDTL`), `symbol`, `volumeBLots`, `contractSizeB`, `volumeALots`, `confirmLatencyB`; có `Normalize`, `GetMissingRequiredFields`.
  - `SansJsonHelper` parse/build khối `tradelocker` (**byte-identical** khi rỗng); `ConfigLoadResult.TradeLocker`; `IRuntimeConfigProvider.CurrentTradeLockerConfig`; `RuntimeConfigState.UpdateTradeLocker` (không qua `Update(...)`).
  - `ITradeLockerTokenStore` + `TradeLockerTokenStore` (DPAPI, chỉ refresh token + exp).
  - `KeycloakClient.RefreshAsync`; `KerberClient` (`ListAccounts` với `X-ODA-Brand`, `MintAccountJwt`, cache, gia hạn trước 60 s).
  - `TradeLockerChromeBrowserService`: profile `chrome-tradelocker`, `--remote-debugging-port=0`; đọc sessionStorage key `tlx-oidc:user:…` qua CDP; ghi chỉ khi `TradeLockerSessionSyncDecision.ShouldSave` (không lùi token). Đăng xuất = `StopSync()` **trước** `Clear()`.
  - Panel Config.
  - Nếu helper Chrome của PrimeXBT cần tham số hoá ⇒ 🛑 hỏi (đổi code PrimeXBT).
- **Test mới:** `TradeLockerConfigTests`, `SansJsonTradeLockerTests` (byte-identical), giữ giá trị khi `Update` thiếu tham số, `TradeLockerTokenStoreTests`, `SessionSyncDecisionTests`, Keycloak/Kerber với HTTP giả (400 thiếu brand, accountId sai dạng).
- **Nghiệm thu:**
  - Đăng nhập demo thật trên laptop.
  - DB không có token.
  - Mở lại Config giữ đủ trường.
  - Kerber trả account demo (`type`, `engine=APOLLO`, `cluster`).

### Phase 3 — Lõi giao thức offline
- **Làm:**
  - 🛑 thêm `Google.Protobuf` (Infrastructure).
  - `.proto` subset (Quote, Position*, Order*, MarketOrder*, CommandAck, Error, TradableUnit/Instrument/Ticker aggregate, Notification) sinh từ `descriptors.json` + C# sinh sẵn + script regen.
  - `RSocketFrameCodec`: encode/decode 8 loại frame, composite metadata (auth bearer, routing), ghép fragment FOLLOWS, ERROR base64 ⇒ `ProtoError`.
  - `TradeLockerSymbolIndex` (khớp chính xác code/name).
  - `TradeLockerPositionStore` (port `view()`).
  - `TradeLockerOrderPlanner`: PlanOpen market IOC INCREASE, kiểm min/step/max; PlanClose(id) đúng một id.
  - `TradeLockerOrderMatcher`: position mới cùng unit + side + qty; Ambiguous ⇒ không đoán.
  - `TradeLockerErrorMapper` (mã từ Q8).
  - `TradeLockerHistoryProjector`: hera JSON ⇒ `HistorySharedRecord`, money = pnl + commission theo `HistoryProfitCalculator`.
  - Copy fixture vào `TradeDesktop.Tests/TradeLocker/Fixtures/`.
- **Test mới:**
  - Frame BUY encode **khớp từng byte** frame web đã bắt (00-scan-findings §4).
  - Hedge 2 position ≠ net.
  - Lot sai step bị từ chối.
  - Fragment ghép đúng.
  - `positions_deleted` xoá order kèm theo.
- **Nghiệm thu:** không tham chiếu mạng/file ngoài fixture; bật A6.

### Phase 4 — Socket + luồng giá (chỉ đọc)
- **Làm:**
  - `ITradeLockerTransport` + `ClientWebSocket` (header `Origin: https://app.tradelocker.com`).
  - `RSocketConnection`: SETUP bearer, keepalive 5 s / lifetime 15 s, REQUEST_N bù credit, CANCEL.
  - `TradeLockerHermesSession`:
    - `EnsureState(platformB, cfg)` idempotent, có generation như PrimeXBT/cTrader.
    - Backoff 0.5→10 s; ngắt mạch khi bão reconnect.
    - Chính sách JWT theo Q1.
    - `Read()` fail-closed theo thứ tự: chưa nối / chưa symbol / tick quá tuổi / auth lỗi.
    - Latency = tuổi tick.
    - `[STATS]` mỗi phút.
  - Nhánh trong `SharedMemoryMarketDataReader` **trước** `BPriceOffsetApplier.Apply`.
  - `CurrentConfirmLatencyMsBEffective` có nhánh tradelocker.
  - `PointDigitsConsistencyChecker`.
  - `TradeLockerSessionMonitor` (`{date}-tradelocker.log` + Telegram).
  - `TradeLockerLogMasker`.
- **Test mới:** fake transport + đồng hồ giả: từng điều kiện fail-closed, reconnect, ngắt mạch, hết hạn JWT, generation reset `[STATS]`.
- **Nghiệm thu:**
  - Giá B khớp web.
  - Gap đúng (có/không offset B).
  - Rút mạng ⇒ fail-closed rồi tự hồi.
  - Qua ≥ 6 mốc JWT 5' không mất giá.
  - Chân B fail ⇒ rollback chân A đúng (guard `PARTIAL_OPEN_ROLLBACK_STREAK` sẵn có).
  - Bật A7.

### Phase 5 — Vị thế mở → Trades map (chỉ đọc)
- **Làm:**
  - `ITradeLockerTradeSession.ReadTrades/TryGetOpenPosition/OpenBlockReason`.
  - `TradeLockerAwareTradesReader` bọc ngoài cùng.
  - `MapNotFound` tới khi có snapshot đầu.
  - Lot = quantity.
  - Content-version.
  - Snapshot hỏng ⇒ mất sync.
  - Nhánh `[HEDGE_VOLUME]`; resync `TryRebuildCoordinatorFromMmf` nhận ticket bit 63.
- **Test mới:** position cache, ReadTrades fail-closed, decorator chỉ chặn map `TRADELOCKER_B_*`.
- **Nghiệm thu:** T5; restart app rebuild slot đúng; `mt5/mt5` không đổi.

### Phase 6 — Lịch sử → History map (chỉ đọc)
- **Làm:**
  - `TradeLockerHistoryBook`: polling `closed-positions-history` lúc sync / 0.5 s sau khi đóng / 10 s; phân trang `links.next`; Bearer = account JWT.
  - `TradeLockerAwareHistoryReader`.
- **Test mới:** phân trang, projector tiền, idle ≤ 6 request/phút.
- **Nghiệm thu:** T6 (tab History chỉ hiện ticket app tạo ⇒ kiểm hiển thị ở 7B).

### Phase 7A — Executor cô lập trên demo 🛑
- **Làm:**
  - `TradeLockerTradeExecutor` (OpenLeg/CloseLeg; pair-level trả lỗi rõ).
  - `SendOrderAsync(plan)` là đường **DUY NHẤT** gửi lệnh.
  - Outcome:
    - `NOT_CONNECTED`
    - `REJECTED` (notification `orderRejected` khớp `correlation_id`)
    - `FILLED`
    - `TIMEOUT_UNCERTAIN`: không gửi lại; đối soát chỉ báo cáo.
  - Mỗi symbol+side một Open in-flight.
  - `ITradeLegOpenReadiness` (refresh lỗi / socket rớt / symbol không trading ⇒ `LEG_B_NOT_READY`).
  - Thay Null bằng executor thật trong `App.xaml.cs` (comment kill switch).
- **Test mới:** fake transport: fill, reject, timeout, rớt socket sau gửi, close position không tồn tại.
- **Nghiệm thu:** harness dùng đúng session app: open/close từng chân demo; demo flat.

### Phase 7B — Cặp đầy đủ trên demo 🛑 (A = MT5 demo, B = TradeLocker demo)
- **Làm:**
  - Nhánh Dashboard (`IsExchangeBWithoutHwnd`, lot check cặp đầu, `CanRetryCloseLegB`, `[BUILD]`, reload).
  - Khuyến nghị `close_pending_time_ms` theo số đo.
- **Kịch bản:**
  - ≥ 10 cặp Auto.
  - Manual per-pair close.
  - Partial-open rollback (cắt socket B).
  - Đóng ngoài trên web.
  - Restart giữa chu kỳ (`current_slots`).
  - Popup lệch lot cặp đầu.
  - Rule A/B/D với ≥ 2 slot.
- **Nghiệm thu:** smoke UIA trên laptop; log không ERROR/WARN bất thường; Rule F 8/8; demo flat.

### Phase 8 — Hardening & vận hành
- **Làm:**
  - `[DRIFT]` (route/trường lạ, frame hỏng) ⇒ `OpenBlockReason` + Telegram.
  - Cảnh báo refresh token sắp hết hạn.
  - Thông báo market đóng (chỉ báo, không chặn/đóng thêm: Rule E).
  - `KILL-SWITCH.md`.
  - Soak 24 h demo.
  - README §7.1/§13 + CLAUDE.md pitfall "Sàn B TradeLocker".
- **Nghiệm thu:** soak sạch, qua ≥ 1 lần refresh Keycloak; kill switch thử được.

### Phase 9 — Live 🛑 (chỉ chủ dự án)
- AI chỉ viết **runbook**: brand/cluster broker live, cách xác minh account, bật dần lot nhỏ, theo dõi gì, khi nào kill switch.
- AI **không** đăng nhập/đặt lệnh/đổi config host live (CLAUDE.md §0.0). Thêm host live vào danh sách "đã biết là THẬT".

---

## §5 Chiến lược test (3 tầng)

| Tầng | Ở đâu | Chạy khi | Ví dụ bắt buộc |
|---|---|---|---|
| **Unit offline** | `TradeDesktop.Tests/TradeLocker/*` + fixture thật | Mọi phase từ 1 | codec không va chạm; frame BUY khớp byte; hedge ≠ net; lot sai step bị từ chối; fragment |
| **Fake transport** | `FakeTradeLockerTransport` (đẩy frame, rớt socket, trễ, timeout, ERROR) | Phase 4–7 | mỗi điều kiện fail-closed của `Read()`; JWT hết hạn; `TIMEOUT_UNCERTAIN` không gửi lại |
| **Live demo** | Spike + app với tài khoản DEMO đã xác minh | Nghiệm thu phase 0, 2, 4–8 | soak; T5 user mở tay; 7B ≥ 10 cặp |

---

## §6 Nhật ký thực thi (append-only: thêm dòng, không sửa dòng cũ)

| Ngày | Phase | Bước | Commit | Test pass / fail (fail mới?) | Recheck (auto / tay) | Kết luận / quyết định |
|---|---|---|---|---|---|---|
| 2026-10-25 | — | 2. Chốt | a64dc7b (+ b-price-offset chưa commit) | — | — | Chủ dự án chốt: **port sang C#** (không Node sidecar); làm trên **OPEN_DEMO**, broker live chưa chốt; trước mắt chỉ viết plan |
| 2026-10-26 | — | 1. Recheck code | 9a4bd8f | — | grep điểm chạm: khớp | Rà lại sau khi code đổi: trade-locker không đổi; trade-multi có `befe907` (b-price-offset, áp lên mọi nguồn B ⇒ nhánh TradeLocker phải trước `BPriceOffsetApplier.Apply`) và `9a4bd8f` (CLAUDE.md §0.0). Ticket `ulong` ở mọi tầng ⇒ chọn bit 63. Tạo MASTER-PLAN, README, 00-scan-findings, phase-0-spike. Chưa code, chưa commit |
| 2026-10-26 | 0 | 0.R baseline + 1. Recheck vào | 9a4bd8f | 1393 / 11 (không fail mới) | auto PASS: A1 WARN (`debug-logs/` có sẵn, không thuộc phase), A2–A5 PASS, A6/A7 SKIP | Tạo `tools/recheck.ps1` từ bản PrimeXBT: plan path; A4 thêm `PrimeXbt.*`, `BPriceOffset*`, `TradeLocker.*` (từ P1); A5 thêm token JSON Keycloak; A6 cấm `close-all` / `closeAllPositions` (không cấm `CloseAll` trần vì Select-String không phân biệt hoa thường, sẽ trúng `CloseAllowedAt`); A7 trỏ `scan\tradelocker-spike\probe.cmd`. Ghi baseline |
| 2026-10-26 | 0 | 4–6 khảo sát chỉ đọc | 9a4bd8f (repo không đổi) | — | `probe` OK | Account duy nhất là OPEN_DEMO, đã xác minh. **Q1 ✅** (không cắt socket khi JWT hết hạn, mới thấy 1 mốc) · **Q2 ✅** (hedge: SPX500 BUY + SELL đang mở) · **Q3 ✅** XAUUSD lotSize 100, tick/step/min 0.01 · **Q4 ✅** id ~9e17 · **Q5 ✅** không fragment · **Q7** p95 ~1.2 s, p99 ~2.5 s · **Q10** refresh 30 ngày · **Q13 ✅ một phần** (.NET 2.5 phút). **Q12 ❌: T&C TradeLocker (Demo Terms (a)(b)(d) + Terms chung + 16.3) cấm phát triển/kiểm thử hệ thống giao dịch tự động trên demo, cấm reverse-engineer, cấm thu thập dữ liệu bằng script** ⇒ dừng mọi tiến trình đọc, **không** đặt lệnh nào (0 lệnh), chờ chủ dự án quyết 🛑. Tài khoản demo **không flat từ trước**: 3 position của đợt quét 2026-10-07, chưa đóng |
| 2026-10-26 | 0 | 2. Chốt | — | — | — | **Chủ dự án chọn phương án 2: chấp nhận rủi ro T&C (Q12) và đi tiếp** (rủi ro: khoá tài khoản; giao thức dò ngược có thể vỡ khi web đổi). Lệnh demo cho Q6/Q8/Q9 vẫn phải hỏi riêng 🛑. Chạy lại phần chỉ đọc: `watch` 15' (0-A1, nhiều mốc JWT cho Q1) + đưa fixture vào repo (0-A7) |
| 2026-10-26 | 0 | 6. Kiểm chứng (chỉ đọc) | 9a4bd8f (repo không đổi) | — | — | **0-A1 ✅** spike C# 1 kết nối ≥ 13', 0 reconnect, sống ≥ 8' sau khi JWT hết hạn (Q1 ✅ củng cố). **0-A7 ✅** 3 fixture + `expected.json` (decoder tham chiếu) + byte frame BUY đã kiểm bằng encoder tham chiếu. Chủ dự án hỏi vì sao không cần đăng nhập ⇒ giải thích: dùng lại refresh token 30 ngày từ `npm run login` ngày 2026-10-08 (`scan\trade-locker\.auth\keycloak.json`); chủ dự án chọn **giữ nguyên** token. `experiments.ts` (Q6/Q8/Q9/R9, chặn nếu không phải OPEN_DEMO) đã viết + typecheck, **chưa chạy — chờ đồng ý đặt lệnh demo** 🛑 |
| 2026-10-26 | 0 | 6. Lệnh demo | 9a4bd8f (repo không đổi) | — | — | **Chủ dự án: "đồng ý chạy"** (không yêu cầu đóng 3 position cũ ⇒ để nguyên). OPEN_DEMO, XAUUSD 0.01: 9 mở / 9 đóng / 3 reject / 2 đóng lỗi. **Q8 ✅** lỗi đồng bộ qua ERROR frame (`QUANTITY_NOT_ON_LOT_STEP_GRID`, `INPUT_VALIDATION`). **Q6 ✅** đóng trùng/sai ⇒ `NOT_FOUND`; cắt socket ngay sau gửi ⇒ **lệnh vẫn khớp** ⇒ không gửi lại. **Q9 ✅** không rate limit ở nhịp ~1.5 s; ack ~250 ms, fill ~550 ms, close ~560 ms. **R9 ❌ phát hiện**: 2 Open đồng thời ⇒ matcher kiểu Node gán cùng một id, position thứ hai mồ côi (script bắt và đóng) ⇒ bắt buộc một Open in-flight mỗi symbol. Mọi position do phiên mở đã đóng. **Đề xuất GO**, chờ chủ dự án chốt 🛑 |
| 2026-10-26 | 0 | 7. Recheck ra | 9a4bd8f | 1393 / 11 (không fail mới) | auto PASS: A1 WARN (`debug-logs/` có sẵn), A2–A5 PASS, A6/A7 SKIP · tay T0: spike `watch` 15' ổn, refresh token còn hạn tới 2026-11-07 | Phase 0 sẵn sàng đóng khi chủ dự án chốt GO |
| 2026-10-26 | 0→1 | 8. Bàn giao + 2. Chốt | 9a4bd8f | — | — | **Chủ dự án: "thực hiện tất cả các phase khi nào cần tôi thì thông báo"** ⇒ Phase 0 **GO**; agent chạy liên tục các phase, chỉ dừng ở điểm cần chủ dự án (đăng nhập tay, lệnh ngoài demo đã xác minh, live, quyết định thiết kế đổi hành vi có sẵn). Không commit khi chưa được yêu cầu. Tạo nhánh `feature/tradelocker` từ `9a4bd8f`. **Đổi D4 (lý do kỹ thuật):** máy không có `protoc`, cache NuGet không có `Google.Protobuf`/`Grpc.Tools`, và lõi chỉ cần ~15 message ⇒ viết codec protobuf tối thiểu bằng tay (như `Pb.cs` của spike đã chạy đúng), khoá bằng test so với `expected.json` của decoder tham chiếu. Không thêm package ⇒ bỏ 🛑 package của Phase 3. `recheck.ps1`: nhóm khoá `TradeLocker.*` chỉ áp từ cổng **ra** Phase 1 (cổng vào Phase 1 chưa có test) |
| 2026-10-26 | 1 | 1. Recheck vào | 9a4bd8f | 1393 / 11 | auto PASS (A1 WARN `debug-logs/`) · T0 ✅ | Vào Phase 1 trên nhánh `feature/tradelocker` |
| 2026-10-26 | 1 | 3. Rủi ro | — | — | — | Logic giao dịch mt4/mt5/ctrader/primexbt: không đổi (chỉ thêm nhánh `tradelocker` song song). Luồng dữ liệu: thêm `CurrentMapName2 = TRADELOCKER_B` khi B = tradelocker (kéo lên từ Phase 2 như PrimeXBT, để không đọc nhầm MMF EA MT cũ ⇒ không signal giả). Side effect: bản app CŨ đọc `tradelocker` thành `mt5` ⇒ đổi DB trước khi chạy bản cũ |
| 2026-10-26 | 1 | 4–5 Làm + test | (chưa commit) | 1456 / 11 (+63 test, không fail mới) | — | 5× `NormalizePlatform` (+`tradelocker`); `TradeLockerRoutingRules` (`TRADELOCKER_B_Trades/_History`); `TradeLockerTicketCodec` (**bit 63**, `TryEncode` từ chuỗi id wire, `TryDecodeToWireId`); `TradeLegPlatform.TradeLocker = 4`; router field/validate/resolve/tryResolve; reject A = tradelocker (ConfigService + router); `NullTradeLockerTradeExecutor` + DI; `PlatformBSwitchGuard`; `ResolveTradeLegPlatform`; `CurrentMapName2`. Rà lại cast ticket sang có dấu: không có (chỉ 2 chỗ trong codec cTrader/PrimeXBT, tự an toàn). Test: normalization/matrix/Supabase payload (+tradelocker), switch guard, `TradeLockerRoutingRulesTests`, `TradeLockerTicketCodecTests` (loại trừ chéo cTrader/PrimeXBT/MT, round-trip `SlotPersistence` + chuỗi `current_tick_b`) |
| 2026-10-26 | 1 | 6. Smoke (agent tự chạy UIA + REST) | (chưa commit; Release build có Phase 1) | — | log `20261026_114933` + `20261026_115329`: `[ERROR]`=0 | Hai MT5 xác minh **Demo Account** (538216/538217). **S2 ✅** PATCH `platform_b=tradelocker` → app nạp "Sàn B (TRADELOCKER_B)"; mở Config (không radio B nào chọn) → Save thành công, cửa sổ đóng, DB vẫn `tradelocker`. **S3 ✅** Start 2': `[BUILD] platform_b=tradelocker`, map `TRADELOCKER_B_Trades/_History` = False, 0 signal, 0 `[ROUTER]`, Resync fail-closed. **S1/S4 ✅** `mt5/mt5` + S1-FAST: `[ROUTER] Open pair` Mt5/Mt5 OK 24 ms (A 80054920 / B 80054921) → đóng theo gap sau 30 s, OK 14 ms, `CLOSE_CONFIRMED` −48 pt. Config trả về đúng bản sao lưu (so khớp JSON = True), hai chân flat |
| 2026-10-26 | 1→2 | 7. Recheck ra P1 = 1. Recheck vào P2 | (chưa commit) | 1456 / 11 | auto PASS: A1 WARN, A2–A5 PASS (A4: 9 nhóm khoá gồm `TradeLocker.*`) | **Phase 1 đóng ✅.** Working tree lúc chạy recheck đã gồm phần đầu Phase 2 (`TradeLockerConfig`, khối `tradelocker` trong `SansJsonHelper`, `ConfigService`/`ConfigLoadResult`, `UpdateTradeLocker`, latency B) và không đổi sau đó ⇒ dùng làm recheck vào Phase 2 |
| 2026-10-26 | 2 | 3. Rủi ro | — | — | — | Thêm nhánh, không đổi hành vi nền tảng khác: `BuildSans` 6 tham số trả **đúng từng byte** bản 5 tham số khi `tradelocker` rỗng (test khoá); `CurrentConfirmLatencyMsBEffective` giữ thứ tự ctrader → primexbt rồi mới tradelocker; `IsExchangeBWithoutHwnd` thêm tradelocker (HWND B không bắt buộc, lot check cặp đầu áp dụng khi có Trades map ở Phase 5). Không thêm package (DPAPI `ProtectedData` đã có). Helper CDP của PrimeXBT dùng lại **nguyên trạng**, không sửa |
| 2026-10-26 | 2 | 4–5 Làm + test | (chưa commit) | +test (168 test nhóm liên quan pass) | — | `TradeLockerConfig` (accId số, brand mặc định PRDTL, lot, contract 100, latency B); `SansJsonHelper` parse/build khối `tradelocker`; `ConfigService`/`ConfigLoadResult.TradeLocker`; `IRuntimeConfigProvider.CurrentTradeLockerConfig`; `RuntimeConfigState.UpdateTradeLocker` (không qua `Update(...)`); `TradeLockerRoutingRules.ResolveConfirmLatencyB`; `TradeLockerSession` + `ITradeLockerTokenStore` + `TradeLockerTokenStore` (DPAPI `%LOCALAPPDATA%\TradeDesktop\tradelocker\{host}.bin`); `TradeLockerSessionSyncDecision` (không lùi refresh token); `TradeLockerAuthClient` (Keycloak refresh, kerber accounts + `X-ODA-Brand`, mint JWT); `TradeLockerAuthSession` (access/JWT gia hạn trước 60 s, chọn account theo accId hoặc đầu tiên, chặn engine ≠ APOLLO, lưu access mới cả khi Keycloak không xoay refresh); `TradeLockerCdpSessionReader` (sessionStorage `tlx-oidc:user:…`); `TradeLockerChromeBrowserService` (sinh từ bản PrimeXBT, profile `chrome-tradelocker`); Config UI radio + panel; DI + `StopSync` khi thoát. Test: `TradeLockerConfigTests`, `TradeLockerAuthTests` |
| 2026-10-26 | 2 | 6. Smoke (agent tự chạy UIA + REST) | (chưa commit; Release build có Phase 2) | — | — | **P2-1 ✅** radio TradeLocker, panel hiện, mặc định PRDTL/XAUUSD/100, ẩn Map Name 2 + HWND B. **P2-3 ✅** Save (accId 1791342294192995800, 0.01/0.01 lot, latency 2500) ⇒ DB `platform_b=tradelocker` + khối `tradelocker` đúng, `primexbt`/mapNames/HWND giữ nguyên, **không token trên DB**. **P2-4 ✅** mở lại giữ đủ 7 giá trị. Nút "Mở TradeLocker (Chrome)" mở Chrome profile riêng tới trang "Sign in to Tradelocker". **P2-2 CHƯA KIỂM — cần chủ dự án tự đăng nhập** trong cửa sổ Chrome đó (agent không gõ mật khẩu). DB laptop để `platform_b=tradelocker` cho Phase 4 |
| 2026-10-26 | 3 | 4–5 Làm + test | (chưa commit) | 1538 / 11 (+ test, không fail mới) | — | Không package (D4 đổi). `Application/Services/TradeLocker/`: `ProtoMessage`/`ProtoWriter` (wire 0/1/2/5, decode không throw); `TradeLockerMessages` (quote, position event + 6 loại order theo bảng số field, tradable units, CommandAck, ProtoError + base64 frame ERROR, notification `orderRejected`, encode lệnh mở market); `RSocketFrameCodec` (SETUP bearer, REQUEST_STREAM/RESPONSE, REQUEST_N, CANCEL, KEEPALIVE, decode PAYLOAD/ERROR/KEEPALIVE) + `RSocketFragmentAssembler`; `TradeLockerSymbolIndex` (khớp chính xác, mơ hồ ⇒ null); `TradeLockerPositionStore` (qty ròng, VWAP, content-version, first-seen stamp, Lot = quantity); `TradeLockerOrderPlanner` (chỉ PlanOpen/PlanClose-by-ticket, kiểm lot min/step/max + trading status); `TradeLockerOrderMatcher` (≥ 2 ứng viên ⇒ Ambiguous — R9); `TradeLockerErrorMapper`; `TradeLockerHistoryProjector` (gộp đóng từng phần). Fixture thật vào `TradeDesktop.Tests/TradeLocker/Fixtures`. **Frame BUY encode khớp từng byte frame web đã bắt** |
| 2026-10-26 | 3 | 7. Recheck ra | (chưa commit) | 1538 / 11 | auto PASS (A6 bật, PASS) | **Phase 3 đóng ✅.** Phase 2 auto PASS trong cùng lần chạy; chỉ còn P2-2 tay. Theo tiền lệ PrimeXBT (dòng "CHƯA KIỂM — cần chủ dự án"), đi tiếp Phase 4 phần code + test offline; xác minh live Phase 4 cần đăng nhập (P2-2) nên gộp vào một lần nhờ chủ dự án |
| 2026-10-26 | 4 | 3. Rủi ro | — | — | — | Thêm nhánh `tradelocker` trong `SharedMemoryMarketDataReader` **trước** `BPriceOffsetApplier` (nhánh MMF/ctrader/primexbt không đổi). Session chỉ mở socket khi `platform_b = tradelocker`. Không đổi logic signal/router |
| 2026-10-26 | 4 | 4–5 Làm + test | (chưa commit) | +24 test session | — | `ITradeLockerQuoteSession`/`ITradeLockerTradeSession` + `TradeLockerLogMasker`; `ITradeLockerTransport` + `ClientWebSocketTradeLockerTransport` (binary, `Origin`, dùng một lần); `TradeLockerHermesSession` (một socket cho giá + vị thế + lệnh; EnsureState idempotent + generation; SETUP bearer; subscribe units/positions/notifications rồi quotes khi resolve symbol; KEEPALIVE 5 s, lifetime 15 s ⇒ xoá giá + nối lại; backoff 0.5→10 s; ngắt mạch > 5 lần/60 s, tự thử sau 5 phút; REQUEST_N 100; ghép fragment; ERROR stream 0 ⇒ đóng; drift đếm + log có throttle; `Read()` fail-closed theo thứ tự chưa bật/cấu hình/đăng nhập/kết nối/symbol/digits/giá; latency = tuổi tick; `[STATS]` mỗi phút; JWT chỉ mint khi (re)connect theo Q1; account không phải demo ⇒ WARN + sự kiện, không chặn); nhánh reader; `TradeLockerSessionMonitor` (`{date}-tradelocker.log` + Telegram, mất kết nối > 30 s mới báo); DI. Test: `TradeLockerHermesSessionTests` (transport giả + đồng hồ giả) |
| 2026-10-26 | 5 | 4–5 Làm + test | (chưa commit) | (trong 172 test TradeLocker) | — | `ReadTrades` (MapNotFound tới khi có positions của KẾT NỐI HIỆN TẠI, về MapNotFound khi rớt), `TryGetOpenPosition`, `OpenBlockReason`; `TradeLockerAwareTradesReader` bọc NGOÀI CÙNG chuỗi PrimeXBT → cTrader → MMF; `LogHedgeVolumeConsistency` nhánh TradeLocker (lot × contract) + Telegram `TRADELOCKER_HEDGE_VOLUME_MISMATCH` |
| 2026-10-26 | 6 | 4–5 Làm + test | (chưa commit) | (trong 172 test) | — | Lấy **một trang hera thật** (chỉ đọc, demo) làm fixture: mới nhất trước, 20 dòng, `links.next = {"cursorId"}` (**object, không phải URL** — đã sửa parser), thời gian ISO, `pnl` là tiền (0.30 USD = 0.30 × 0.01 lot × 100). Session tải trang đầu khi kết nối, 0.5 s sau khi có position bị xoá, rồi mỗi 10 s; `ReadHistory` chỉ hợp lệ sau lần tải đầu của kết nối hiện tại; `TradeLockerAwareHistoryReader` bọc ngoài cùng |
| 2026-10-26 | 7A | 4–5 Làm + test | (chưa commit) | (trong 172 test) | — | `SendOrderAsync` (đường DUY NHẤT; chỉ route mở / đóng-một-id, route khác bị từ chối): Ack ⇒ Success; ERROR ⇒ REJECTED (mã ProtoError); Close `NOT_FOUND` ⇒ `ALREADY_CLOSED` **chỉ khi** position không còn trong store; rớt socket / quá 10 s ⇒ `TIMEOUT_UNCERTAIN`, **không gửi lại**, đối soát qua vị thế. **R9:** một Open in-flight mỗi tradable unit + chiều — lệnh thứ hai bị từ chối ngay (`OPEN_IN_FLIGHT`) tới khi FILLED / AMBIGUOUS (≥ 2 position mới, không đoán) / RECONCILE quá 10 s (kèm lý do reject từ notification nếu có). `TradeLockerTradeExecutor` (shim mỏng, `ITradeLegOpenReadiness` ⇒ `LEG_B_NOT_READY`) thay Null trong DI; `PendingCloseRetryEligibility` không đòi HWND cho TradeLocker. Test: 8 ca gửi lệnh với transport giả |
| 2026-10-26 | 4–7A | 7. Recheck (auto) | (chưa commit) | 1573 / 11 (không fail mới) | **auto PASS A1–A7** (A7 probe thật OK) | Release build có Phase 4–7A chạy trên laptop: B = tradelocker ⇒ `[TRADELOCKER][WARN] không mở được phiên: chưa đăng nhập TradeLocker — thử lại sau 30 s` (fail-closed đúng) + Telegram `TRADELOCKER_AUTH_REQUIRED`. Chrome profile riêng đang ở trang đăng nhập; sync đã attach. **Dừng chờ chủ dự án đăng nhập (P2-2)** để kiểm chứng live Phase 2 + 4–7A. `KILL-SWITCH.md` đã viết |
| 2026-10-26 | 2 | 6. P2-2 (chủ dự án đăng nhập) | (chưa commit) | — | — | **Phát hiện:** Google từ chối đăng nhập trên Chrome bật `--remote-debugging-port` ("Couldn't sign you in — This browser or app may not be secure"), dù scan 2026-10-07 từng qua được. **Cách xử lý đã kiểm chứng:** mở cùng profile `chrome-tradelocker` ở Chrome THƯỜNG (không cổng) ⇒ chủ dự án đăng nhập Google ⇒ đóng Chrome ⇒ bấm "Mở TradeLocker (Chrome)" (có cổng) ⇒ web tự đăng nhập lại bằng cookie phiên TradeLocker, không qua Google ⇒ sync lấy token: "✔ Đã đăng nhập — refresh token hết hạn 25/11 13:35". Đã đưa vào code: nút **Đăng nhập** (`ITradeLockerBrowserService.OpenForSignInAsync`, Chrome không cổng, từ chối nếu profile đang mở). **P2-2 ✅** |
| 2026-10-26 | 4–6 | 6. Kiểm chứng live (chỉ đọc) | (chưa commit; Release build Phase 4–7A) | — | log `20261026-tradelocker.log` + `20261026_133739-trade-log`: `[ERROR]`=0 | Session tự nối lại sau khi có token: `account PRDTL#O#1791342294192995800 type=OPEN_DEMO engine=APOLLO` ⇒ socket ⇒ `symbol XAUUSD … status=1`. **4-A1 ✅** dashboard hiện giá B (đã cộng offset B của laptop +20/−10 point). `[STATS]` 135 tick/phút, tuổi tick tối đa 1.5 s, drift 0. **5 ✅** Start với hai switch Open TẮT: `TRADELOCKER_B_Trades` available ⇒ app thấy 3 position cũ và `Resync blocked: unpairable open trades (A=0, B=3); auto-open paused` (fail-safe đúng, như PrimeXBT 5-B3). **6 ✅** `TRADELOCKER_B_History` available. `[HEDGE_VOLUME][INFO] TradeLocker: hedge ratio=1.00`. **Lỗi bắt được:** Telegram `TRADELOCKER_AUTH_REQUIRED` bắn mỗi 30 s khi chưa đăng nhập ⇒ sửa monitor chỉ báo lần đầu tới khi kết nối lại (chưa vào bản Release đang chạy). 4-A3 (rút mạng 60 s) **CHƯA KIỂM — cần chủ dự án tắt Wi‑Fi** (đã phủ bằng test transport giả) |
| 2026-10-26 | 7 | 2. Chốt | — | — | — | Chủ dự án: **"đóng 3 lệnh cũ" + "ok wifi"**. Đóng 3 position đợt quét 2026-10-07 bằng script theo đúng id (OPEN_DEMO đã kiểm) ⇒ tài khoản flat. Lệnh demo qua app theo ghi nhớ "laptop dev toàn demo"; trước mỗi phiên xác minh MT5 538216/538217 **Demo Account** + TradeLocker **OPEN_DEMO** |
| 2026-10-26 | 7A/7B | 6. Kiểm chứng live (agent tự chạy UIA + REST) | (chưa commit; Release build có Phase 4–7A + sửa Telegram + nút Đăng nhập) | — | log `20261026_134142`, `_134646`, `_134838`, `20261026-tradelocker.log`: `[ERROR]`=0 | Config 7B-FAST (ngưỡng −100, hold 30 s, post-close 15 s), chỉ bật Open Gap Buy. **7B-1 ✅ 3 vòng Auto** A MT5 Buy + B TradeLocker Sell: ack B 250–344 ms, FILLED 610–687 ms, `OPEN_CONFIRMED` ~1.1 s sau signal, đóng theo gap sau 30 s (ack 250 ms), ticket B bit 63 đúng (vd 10125818907978025365 = 2^63 + 902446871123249557). **7B-2 ✅ đóng ngoài**: đóng chân B bằng script ⇒ `[EXTERNAL CLOSE] OnlyAOpen` 4/4 ⇒ đóng A (`ExternalPartialCloseRecovery`, Rule E ngoại lệ 2). **7B-3 ✅ khởi động lại giữa chu kỳ** (hold 90 s): tắt app khi cặp mở ⇒ bật lại ⇒ `[SLOT][RECOVERY] Restored slot … ticketB=10125820184552195163` (bit 63 qua JSONB DB) ⇒ resync khớp Trades map B ⇒ đóng theo signal. **7B-4 ✅ nút Close per-pair**: `ManualPairClose`, owner Manual, 256 ms; dùng lại để đóng cặp còn mở lúc Stop (Start với hai switch Open TẮT ⇒ recovery ⇒ Close(1)). **7A** được phủ bởi các vòng trên (mỗi chân B qua `TradeLockerTradeExecutor` → `SendOrderAsync`). Ghi nhận hành vi sẵn có (không đổi): đóng tay/recovery không tạo post-close lock nên Auto mở lại ngay (Rule C). Kết thúc: TradeLocker demo flat, MT5 flat, ngưỡng DB trả về bản sao lưu, `platform_b` tạm giữ `tradelocker` cho bài thử Wi‑Fi |
| 2026-10-26 | 4 | 6. 4-A3 (chủ dự án tắt Wi‑Fi) | (chưa commit) | — | log `20261026-tradelocker.log` | **4-A3 ✅** 13:55:53 lifetime 15 s ⇒ xoá giá + đóng socket (fail-closed) ⇒ Telegram `TRADELOCKER_DISCONNECTED` sau 30 s ⇒ có mạng lại: 13:58:43 account → socket → symbol + Telegram `TRADELOCKER_RECONNECTED`. **Lỗi bắt được:** lỗi HTTP của kerber lúc mất mạng bị báo nhầm `AUTH_REQUIRED` và chờ cố định 30 s ⇒ sửa: `TradeLockerAuthClient.LastErrorIsTransient` (exception mạng, HTTP 5xx/429) ⇒ `TradeLockerConnectInfo.Transient` ⇒ session coi là mất kết nối, nối lại theo backoff, không đặt `_authProblem`, không báo AuthRequired (+3 test) |
| 2026-10-26 | 8 | 4–5 Làm | (chưa commit) | 1579 / 11 | — | Monitor chỉ báo `TRADELOCKER_AUTH_REQUIRED` lần đầu tới khi nối lại; nút **Đăng nhập** (Chrome thường); phân loại lỗi mạng/đăng nhập; `[DRIFT]` + Telegram `TRADELOCKER_PROTOCOL_DRIFT` (đã có từ Phase 4); `KILL-SWITCH.md`; `phase-9-live-runbook.md`; README §7.1 (khối `sans.tradelocker`) + §8; CLAUDE.md mục pitfall "Sàn B TradeLocker" + §9 dependency |
| 2026-10-26 | 8 | 7. Recheck ra | (chưa commit) | 1579 / 11 (không fail mới) | **auto PASS A1–A7** (A1 WARN = thay đổi chưa commit + `debug-logs/` có sẵn) | Laptop trả config gốc (`platform_b=mt5`, ngưỡng = bản sao lưu, so khớp JSON = True); hai tài khoản demo flat; app đã tắt. Còn: soak 24 h demo (8-A), commit khi chủ dự án yêu cầu, Phase 9 do chủ dự án tự làm |
| 2026-10-26 | 8 | 6. Soak 1 h (agent tự chạy, chủ dự án yêu cầu) | (chưa commit; Release build hiện tại) | — | log `20261026_141321-trade-log` + `20261026-tradelocker.log`; báo cáo 5 phút/lần | Config THẬT của laptop, chỉ `platform_b=tradelocker`, bật cả hai chiều, MT5 538216 Demo + TradeLocker OPEN_DEMO. 14:13–15:14: **13 cặp mở / 13 đóng** (đóng theo gap, 12 Buy + 2 Sell… đều qua router đủ hai chân), **0 ERROR, 0 rollback, 0 external, 0 watchdog**; TradeLocker: **0 WARN/ERROR, 0 DRIFT, 0 rớt socket, 0 UNCERTAIN, 0 Telegram**, 89–137 tick/phút. Tài nguyên ổn định: RAM 245–259 MB, thread 22–27, handle 1257–1282, CPU ~1 % ⇒ không thấy rò rỉ. Guard latency B chặn đúng 8 lần khi tick B đứng 7–8 s (14:27). **Ghi nhận (sẵn có, không sửa):** `[MARKET][WARN] Latency spike exchange=B thresholdMs=500` ~41 dòng/phút — ngưỡng cứng 500 ms so với TUỔI TICK của sàn B socket (tick TradeLocker ~500 ms); PrimeXBT/mt5 cũng có, chỉ là log (panel throttle 10 s). PnL cặp âm −9…−50 pt là do config/offset B của laptop, không phải lỗi. Kết thúc: tắt Open ⇒ chờ cặp cuối đóng ⇒ Stop; hai tài khoản demo flat; `platform_b=mt5`. Soak 24 h vẫn còn mở |
| 2026-10-26 | 8 | Sửa ghi nhận soak (chủ dự án: "xử lí vấn đề ở 2") | (chưa commit) | 1583 / 11 (không fail mới; +4 test) | — | Log `[MARKET][WARN] Latency spike` chân B: thêm `LatencySpikeThresholdResolver` (Application, thuần) — chân B socket (cTrader/PrimeXBT/TradeLocker) so tuổi tick với `CurrentConfirmLatencyMsBEffective`, chân A/MMF giữ 500 ms; guard 0 ⇒ giữ 500 ms. Chỉ đổi ngưỡng LOG, không chạm guard/router/signal. Kiểm thật 3 phút (B = tradelocker, hai switch Open TẮT, 0 lệnh): **0 dòng spike B** (trước ~41/phút). Laptop trả `platform_b=mt5`. CLAUDE.md mục "Latency hai chân" cập nhật |

---

## §7 Prompt mẫu để bắt đầu một phiên

> Đọc `docs/plans/tradelocker/MASTER-PLAN.md`. Chạy phase đầu tiên chưa ✅ theo §2. Sau Bước 1 báo bảng recheck
> trước khi code. Dừng ở mọi 🛑. Không commit.

Biến thể:
- Chỉ recheck: *"Chạy Bước 1 của MASTER-PLAN tradelocker cho Phase N, không làm gì thêm."*
- Làm lại một phase bị ❌: *"Phase N đang ❌, đọc §6 dòng cuối, sửa rồi chạy lại Bước 5–7."*
