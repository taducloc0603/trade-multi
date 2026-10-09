# PrimeXBT cho sàn B — index kế hoạch

> ▶ **Bắt đầu thực thi từ [MASTER-PLAN.md](MASTER-PLAN.md)** — plan tổng: trạng thái, vòng lặp 8 bước, recheck tích luỹ, script [tools/recheck.ps1](tools/recheck.ps1).

> Thêm **PrimeXBT** làm lựa chọn thứ tư cho sàn B (hiện có `mt4` / `mt5` / `ctrader`).
> Sàn A giữ nguyên MT4/MT5. Kế hoạch chia phase, **mỗi phase mở đầu bằng cổng recheck** để chứng minh
> phần đã làm trước đó vẫn đúng rồi mới làm phần mới.
>
> Dữ liệu nền: [00-scan-findings.md](00-scan-findings.md) (quét 2026-10-09 trên DEMO) + mẫu JSON ở [fixtures/](fixtures/).
> Khuôn mẫu: [docs/plans/ctrader-fix/](../ctrader-fix/README.md) — tích hợp cTrader đã đi qua đúng con đường này.

---

## 1. Bối cảnh

- App đọc giá hai sàn mỗi 50 ms, tính `GapBuy = (B.Bid − A.Ask) × Point`, mở/đóng cặp hedge theo signal.
- Sàn B không phải MT được cắm vào app theo kiểu **"giả làm nguồn MMF"** (cTrader): 3 điểm vào — giá
  (`SharedMemoryMarketDataReader`, 50 ms), Trades map (500 ms), History map (500 ms) — cộng một
  `ITradePlatformExecutor` cho đặt/đóng lệnh. Không có interface "B platform" chung; mọi chỗ rẽ nhánh
  đều so chuỗi `platform_b` (danh sách 15 chỗ: [§6](#6-bản-đồ-điểm-chạm-trong-code)).
- PrimeXBT có **hai sản phẩm** dùng được cho sàn B → [§2](#2-hai-track--quyết-định-ở-cổng-phase-0).

### Ma trận platform phải giữ xanh sau MỌI phase

| A \ B | mt4 | mt5 | ctrader | **primexbt** |
|---|---|---|---|---|
| mt4 | ✅ phải giữ | ✅ phải giữ | ✅ phải giữ | mới |
| mt5 | ✅ phải giữ | ✅ phải giữ | ✅ phải giữ | mới |

`platform_a = primexbt` bị **reject cứng** ở `ConfigService` (giống `ctrader`).

---

## 2. Hai track — quyết định ở cổng Phase 0

| | **Track M — MT5 của PrimeXBT** | **Track W — PXTrader 2.0 (web API)** |
|---|---|---|
| Cách cắm | Mở tài khoản MT5 tại PrimeXBT, cài EA DataExporter, `platform_b = mt5` | Client WebSocket JSON mới trong app, `platform_b = primexbt` |
| Code mới | **~0** (chỉ cấu hình + vận hành) | Lớn: phase 1–8 |
| Tính chính thức | API chính thức MT5 | **Reverse-engineered** từ web app; có thể đổi bất cứ lúc nào (`client-version` 34866) |
| Độ trễ xác nhận | Như MT5 hiện tại | Ack ~250–300 ms, nhưng thấy vị thế qua snapshot **1.0–1.8 s** |
| Rủi ro tài khoản | Thấp | Có thể vi phạm ToS → khoá tài khoản (Q9) |
| Điều kiện đi tiếp | Giá/spread MT5-PrimeXBT phù hợp chiến lược | Q1–Q5 của Phase 0 đều PASS |

**Đề xuất:** Phase 0 đánh giá **cả hai** song song (Track M rẻ, chỉ mất vài giờ). Cổng Phase 0 ra một trong ba
quyết định, **chủ dự án chốt**:
- `GO-M` → làm [track-m-mt5.md](track-m-mt5.md), dừng Track W.
- `GO-W` → đi Phase 1 → 8 bên dưới.
- `NO-GO` → ghi lý do vào [phase-0-memo.md](phase-0-spike.md#memo-kết-quả), không tích hợp.

---

## 3. Ý tưởng trung tâm (Track W)

Lặp lại kiến trúc cTrader, **thêm nhánh song song, không refactor nhánh cTrader** (CLAUDE.md §0.2):

```
                 ┌──────────────── PrimeXbtWsTransport (ClientWebSocket, 1 socket fws) ───────────────┐
                 │  envelope {type,rid,action,body} · heartbeat time/20s · reconnect + storm breaker  │
                 └──────┬───────────────────────────┬───────────────────────────────┬───────────────────┘
                        │ fx/market                 │ positions (snapshot ~1s)      │ orders/market/place
                        ▼                           ▼                               │ positions/id/close
           PrimeXbtQuoteSession.Read()   PrimeXbtPositionCache ─► Trades map        ▼
           → ExchangeMetrics (SanB)       report/orders2 ─► History map      PrimeXbtTradeSession
                        │                           │                               ▲
     SharedMemoryMarketDataReader (50ms)   PrimeXbtAware{Trades,History}Reader     PrimeXbtTradeExecutor
                                                                                    (ITradePlatformExecutor)
```

- **Một socket `fws`** (theo `accountId`) là đủ cho giá, vị thế, lệnh, báo cáo. Không cần `pws`.
- Ticket chân B = `PrimeXbtTicketCodec.Encode(subPositionId)` với namespace **bit 61** — không trùng MT, không
  trùng cTrader (bit 62). Codec cTrader phải tiếp tục **từ chối** ticket PrimeXBT và ngược lại.
- Khối lượng: `qty` tính bằng **ounce**; `Lot = qty / ContractSizeB` với `ContractSizeB = 100`.
- Point = `10^-priceScale` = 0.01; đối chiếu với `CurrentPoint` toàn cục bằng `PointDigitsConsistencyChecker`.

---

## 4. Bảng rủi ro P1–P12

| # | Rủi ro | Hậu quả | Chặn ở |
|---|---|---|---|
| **P1** | Socket không mở được ngoài trình duyệt (auth-guard gắn fingerprint, Cloudflare, Origin) | Track W bất khả | **Phase 0 (chặn dự án)** |
| **P2** | Token: JWT 7 ngày, refresh qua cookie httpOnly 30 ngày + auth-guard; đăng nhập có thể cần captcha/Google | App chết sau ≤ 7 ngày nếu refresh ngoài trình duyệt không được | Phase 0 (Q2), Phase 2, Phase 8 |
| **P3** | **Đọc dòng gộp thay vì sub-position** | Hedge Buy+Sell hiện `qty 0` → app tưởng đã đóng → **đóng nhầm chân A** | Phase 3 (parser), Phase 5 |
| **P4** | Map Trades báo "có sẵn" trước khi nhận snapshot `positions` đầu tiên (giống R2 cTrader) | Thấy B rỗng → recovery đóng nhầm chân A đang sống | Phase 5 |
| **P5** | Không có client order id → timeout lúc đặt lệnh là **mơ hồ** | Gửi lại mù ⇒ mở 2 lệnh; bỏ qua ⇒ lệnh mồ côi | Phase 7 (đối soát bằng `report/orders2` + `positions`) |
| **P6** | Xác nhận vị thế chậm 1.0–1.8 s (nhịp snapshot) | `open_pending_time` quá ngắn → rollback nhầm leg đã khớp | Phase 5/7 (đo + cấu hình) |
| **P7** | Tick không có timestamp; nhịp p90 772 ms, max ~1 s | Latency B = tuổi tick; ngưỡng < 1000 ms chặn signal liên tục | Phase 4 (config latency riêng) |
| **P8** | Tài khoản ở **NETTING** thay vì HEDGE | Lệnh đối chiều tự triệt tiêu, mô hình slot sai hoàn toàn | Phase 5 (fail-closed nếu `positionMode != HEDGE`) |
| **P9** | Giao thức đổi khi PrimeXBT deploy (`client-version`) | Parse sai ⇒ dữ liệu sai âm thầm | Phase 3 (parser strict + log trường lạ), Phase 8 (probe drift) |
| **P10** | `XAU/USD` vs `XAU/USD.24`; resolve symbol bằng tên dễ nhầm | Giao dịch sai sản phẩm | Phase 3/4 (khoá theo `symbolId` từ `markets2`, kiểm tra `symbol` khớp tuyệt đối) |
| **P11** | `rpl` làm tròn 2 số USD | History hiển thị 0 cho lãi/lỗ nhỏ | Phase 6 (tự tính `move × qty`) |
| **P12** | ToS / khoá tài khoản do dùng API web | Mất kênh sàn B đột ngột | Phase 0 (Q9), kill switch Phase 1 (Null executor) |

Ngoài ra **kế thừa nguyên** các rủi ro R5 (profit không bám lot), R6 (point global), R7 (5 bản `NormalizePlatform`),
R9 (mất session vẫn phục vụ tick cũ) từ [ctrader-fix §3](../ctrader-fix/README.md#3-bảng-rủi-ro-r1r11).

---

## 5. Quyết định thiết kế đề xuất (chốt ở Phase 0/2)

| # | Quyết định | Lý do |
|---|---|---|
| D1 | Chỉ đóng bằng `positions/id/close` theo **sub-position id**. **Cấm** `positions/close` và `positions/close/all` (assert trong code + test). | Rule F: đóng đúng pair, không đụng vị thế khác |
| D2 | Xác nhận OPEN/CLOSE **chỉ** qua Trades map (snapshot `positions`), không short-circuit bằng ack hay `trading/activity` | Giữ nguyên hợp đồng confirm của router (giống ctrader-fix §4.2) |
| D3 | Gắn order → sub-position bằng `openTime == executedAt` ∧ `openPrice == executedPrice` ∧ side ∧ qty ∧ symbol; không khớp duy nhất ⇒ fail-closed + log | Ack không có positionId |
| D4 | Không bao giờ tự gửi lại lệnh sau timeout; đối soát rồi để router xử lý như partial/rollback hiện có | P5 |
| D5 | Token **không** lưu Supabase; lưu máy cục bộ bằng DPAPI (CurrentUser). Supabase chỉ giữ `accountId`, symbol, volume, latency. | Refresh token xoay vòng, gắn máy (`cip` trong JWT) |
| D6 | Nhịp 50 ms và phát snapshot vô điều kiện giữ nguyên; `Read()` không chặn, không throw, fail-closed | Giống cTrader §4.3, §4.7 |
| D7 | Thêm nhánh `primexbt` song song, **không** gộp/refactor nhánh `ctrader` | CLAUDE.md §0.2 |

---

## 6. Bản đồ điểm chạm trong code

Mỗi chỗ đang rẽ nhánh theo `platform_b`/`"ctrader"` phải được rà và thêm nhánh `primexbt` (Phase nào làm ghi trong ngoặc):

| # | Vị trí | Việc |
|---|---|---|
| 1 | `NormalizePlatform` × **5**: `ConfigService.cs` (2), `RuntimeConfigState.cs`, `SupabaseConfigRepository.cs`, `ConfigViewModel.cs` | Thêm `primexbt` cùng một commit (P1). `PlatformNormalizationTests` khoá 5 bản giống nhau |
| 2 | `CTraderRoutingRules` | Thêm `PrimeXbtRoutingRules` riêng, map `PRIMEXBT_B_Trades` / `_History` (P1) |
| 3 | `RuntimeConfigState.CurrentMapName2`, `CurrentConfirmLatencyMsBEffective` | Nhánh `primexbt` (P2/P4) |
| 4 | `SharedMemoryMarketDataReader.PollLoopAsync` | Nguồn giá B từ `IPrimeXbtQuoteSession` (P4) |
| 5 | `CTraderAwareTradesReader` / `CTraderAwareHistoryReader` | Thêm decorator `PrimeXbtAware*` bọc ngoài (P5/P6) |
| 6 | `EnsureState(platformB, cfg)` của session | Session PrimeXBT chỉ mở socket khi `platform_b == primexbt` (P4) |
| 7 | `DashboardViewModel.ResolveTradeLegPlatform` | `"primexbt" => TradeLegPlatform.PrimeXbt` (P1) |
| 8 | `DashboardViewModel.IsExchangeBCTrader()` + 3 chỗ dùng (HWND health, `requiresExchangeBHwnd`, `CheckFirstPairLotOnce`) | Thêm `IsExchangeBPrimeXbt()`; B không cần HWND; lot check lần đầu áp dụng (P2/P7) |
| 9 | `LogHedgeVolumeConsistency`, `BuildResyncedOpenSlots` | Nhận ticket PrimeXBT (P5/P7) |
| 10 | `TradeExecutionRouter` ctor / `ValidatePlatformOrThrow` / `ResolveExecutor` | Enum `PrimeXbt = 3`, executor (P1) |
| 11 | `ConfigService.SaveByMachineHostNameAsync` | Reject `platform_a = primexbt` (P1) |
| 12 | `PlatformBSwitchGuard.Evaluate` | Chặn đổi B khi còn slot nếu thay đổi chạm `primexbt` (P1) |
| 13 | `ConfigViewModel` + `ConfigWindow.xaml` | Radio + panel PrimeXBT (P2) |
| 14 | `CTraderSessionMonitor` | `PrimeXbtSessionMonitor` → `{date}-primexbt.log` + Telegram (P4) |
| 15 | `App.xaml.cs` / `Infrastructure/DependencyInjection.cs` | Đăng ký session, executor, decorator (P1/P4) |

---

## 7. Tuân thủ Rule E / Rule F

- Executor PrimeXBT **chỉ** là đường thực thi; quyết định mở/đóng vẫn đến từ signal engine (Rule E). Không thêm path
  mở/đóng nào mới. Đóng theo `positions/id/close` dùng đúng ba nguồn hợp lệ: Auto, Manual per-pair, Recovery (Rule F).
- Mọi OPEN/CLOSE vẫn đi qua physical dispatch mutex của router; ticket B phải decode được bằng `PrimeXbtTicketCodec`
  và khớp slot, sai thì fail-closed (không fallback).
- Đổi `platform_b` khi còn slot mở bị `PlatformBSwitchGuard` chặn.

---

## 8. Giao thức recheck — áp dụng cho MỌI phase

Mỗi file phase có hai cổng. **Không được bỏ cổng vào** kể cả khi phase trước vừa xong.

### Cổng vào (recheck phần cũ trước khi làm mới)

1. `git status` sạch; ghi lại commit hiện tại.
2. Build Release sạch, **không warning mới**.
3. `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj` — số fail **≤ baseline ghi ở
   Phase 0** và **đúng cùng tên test** (baseline CLAUDE.md: 19 fail đã biết).
4. Chạy lại **toàn bộ checklist nghiệm thu của các phase trước** (mục "Recheck" trong từng file phase liệt kê cụ thể).
5. Smoke ma trận platform (§1): ít nhất `mt5/mt5` và `mt5/ctrader` chạy Start 2 phút, log không có ERROR mới.
6. (Từ Phase 4) **Probe giao thức**: chạy `primexbt-probe` (Phase 0 tạo) so fixture với live — route, tên trường, kiểu
   dữ liệu. Có sai lệch ⇒ **dừng**, cập nhật [00-scan-findings.md](00-scan-findings.md) trước.
7. Ghi kết quả 1–6 vào bảng "Nhật ký recheck" cuối file phase (ngày, commit, số test, kết luận).

Bất kỳ bước nào FAIL ⇒ **không bắt đầu phase mới**; sửa/khoanh vùng ở phase cũ và báo chủ dự án.

### Cổng ra

- Mọi tiêu chí nghiệm thu của phase: ✅ / ❌ / "CHƯA KIỂM — cần X" (không được để trống).
- Test mới cho mỗi service mới (happy path + ≥ 2 edge case — CLAUDE.md §6).
- Cập nhật bảng tiến độ §9 và README/CLAUDE.md nếu có pitfall mới.
- **Không commit** khi chưa được yêu cầu; đề xuất commit message dạng `primexbt P<n>: <tóm tắt>`.

---

## 9. Bảng tiến độ

| Phase | File | Chạm live? | Đặt lệnh? | Trạng thái |
|---|---|---|---|---|
| Quét | [00-scan-findings.md](00-scan-findings.md) | Demo | 2 lệnh demo (đã đồng ý) | ✅ 2026-10-09 |
| 0 | [phase-0-spike.md](phase-0-spike.md) | Demo | Demo, tối thiểu | ⏳ |
| M | [track-m-mt5.md](track-m-mt5.md) | Demo MT5 | Demo | ⏳ chờ cổng 0 |
| 1 | [phase-1-platform-enum.md](phase-1-platform-enum.md) | Không | Không | ⏳ |
| 2 | [phase-2-config-auth.md](phase-2-config-auth.md) | Login demo | Không | ⏳ |
| 3 | [phase-3-protocol-core-offline.md](phase-3-protocol-core-offline.md) | Không (fixture) | Không | ⏳ |
| 4 | [phase-4-quote-feed.md](phase-4-quote-feed.md) | Demo, chỉ đọc | Không | ⏳ |
| 5 | [phase-5-open-positions.md](phase-5-open-positions.md) | Demo, chỉ đọc | Không (vị thế mở tay trên web) | ⏳ |
| 6 | [phase-6-history.md](phase-6-history.md) | Demo, chỉ đọc | Không | ⏳ |
| 7 | [phase-7-execution.md](phase-7-execution.md) | Demo → Live | **Có** — từng bước có cổng đồng ý | ⏳ |
| 8 | [phase-8-hardening.md](phase-8-hardening.md) | Demo/Live | Theo kịch bản | ⏳ |

---

## 10. Nguyên tắc an toàn khi thao tác với tài khoản

- Mặc định **DEMO**. Mọi lệnh thật trên live cần chủ dự án đồng ý **trong chính lượt đó**.
- Chỉ đóng vị thế do chính app/phiên thử mở ra; **không bao giờ** `close all`.
- Không in/ghi JWT, `auth-guard`, cookie, email ra log, tài liệu, fixture (che bằng `<JWT>`, `<AG>`).
- Output Playwright MCP nằm trong `.playwright-mcp/` (đã gitignore). Token phiên đã lộ trong transcript quét
  2026-10-09 → nên **Sign out** phiên web đó để vô hiệu.
