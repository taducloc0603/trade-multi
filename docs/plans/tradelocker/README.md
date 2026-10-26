# TradeLocker cho sàn B — index kế hoạch

> ▶ **Bắt đầu thực thi từ [MASTER-PLAN.md](MASTER-PLAN.md)**: trạng thái, vòng lặp 8 bước, recheck tích luỹ.

> Thêm **TradeLocker** (`app.tradelocker.com`, engine APOLLO) làm lựa chọn thứ năm cho sàn B (hiện có `mt4` / `mt5` /
> `ctrader` / `primexbt`). Sàn A giữ nguyên MT4/MT5.
>
> - Dữ liệu nền: [00-scan-findings.md](00-scan-findings.md), lấy từ source `scan\trade-locker` (Node/TS, demo 2026-10-07).
> - Khuôn mẫu: [docs/plans/primexbt/](../primexbt/README.md). TradeLocker gần như trùng mô hình PrimeXBT: WebSocket +
>   token lấy từ trình duyệt + đóng theo đúng một id.

---

## 1. Bối cảnh và quyết định đã chốt (2026-10-25)

| # | Quyết định | Lý do |
|---|---|---|
| D1 | **Port sang C#**, không chạy Node sidecar | Một process, fail-closed kiểm soát trọn, test xUnit, không phải cài Node lên VPS |
| D2 | Làm hết trên **OPEN_DEMO** (brand `PRDTL`, cluster `apl0`); broker/cluster live là config | Broker live chưa chốt |
| D3 | Ticket namespace **bit 63** (`0x8000_0000_0000_0000`, mask `0x8000…`) | Bit 61 = PrimeXBT (mask `0xE000…`), bit 62 = cTrader (mask `0xC000…`). `0x6000…` sẽ bị codec cTrader decode nhầm. Ticket là `ulong` ở mọi tầng (đã rà 2026-10-26) |
| D4 | Protobuf: **codec tối thiểu viết tay** cho ~15 message dùng tới (đổi ngày 2026-10-26, trước đây định dùng `Google.Protobuf`) | Máy không có `protoc`, NuGet cache không có `Google.Protobuf`/`Grpc.Tools`; reader tối thiểu của spike đã decode đúng. Không thêm package. Khoá bằng test so `fixtures/expected.json` (decoder tham chiếu) + frame BUY khớp từng byte |
| D5 | RSocket: tự viết framing tối thiểu | .NET không có thư viện RSocket đáng tin. Chỉ cần SETUP, REQUEST_RESPONSE, REQUEST_STREAM, REQUEST_N, CANCEL, PAYLOAD, ERROR, KEEPALIVE, composite metadata và ghép fragment |
| D6 | Đăng nhập bằng Chrome profile riêng `chrome-tradelocker` + CDP đọc sessionStorage; lưu refresh token bằng DPAPI cục bộ, không lên DB | Google SSO chặn Chrome do automation mở. Tái dùng `ChromeExecutableLocator`, `DevToolsActivePortFile`, `ChromeDevToolsClient` **nguyên trạng** |
| D7 | Volume B cấu hình bằng **lot**; `contractSizeB` lấy từ `lot_size` của ticker | Wire dùng quantity = lot (khác PrimeXBT dùng oz) |
| D8 | Nhánh song song, **không refactor** nhánh mt4/mt5/ctrader/primexbt | CLAUDE.md §0.2 |

### Ma trận platform phải giữ xanh sau MỌI phase

| A \ B | mt4 | mt5 | ctrader | primexbt | **tradelocker** |
|---|---|---|---|---|---|
| mt4 | ✅ giữ | ✅ giữ | ✅ giữ | ✅ giữ | mới |
| mt5 | ✅ giữ | ✅ giữ | ✅ giữ | ✅ giữ | mới |

`platform_a = tradelocker` bị **reject cứng** ở `ConfigService` (giống `ctrader` / `primexbt`).

---

## 2. Ý tưởng trung tâm

Sàn B không phải MT được cắm vào app theo kiểu **"giả làm nguồn MMF"**, gồm 3 điểm vào và một executor:

```
Chrome (profile riêng) ──CDP──► refresh token Keycloak (DPAPI)
                                   │ refresh 30'
                                   ▼
                        kerber: account JWT (5')
                                   │
                ┌──────────────────┴────────────────────┐
     hermes (RSocket/WS, 1 socket)                 hera REST
     ├─ tickers.{id}.quotes  ──► ITradeLockerQuoteSession.Read ──► SharedMemoryMarketDataReader (50 ms) ──► BPriceOffsetApplier ──► SnapshotReceived
     ├─ position-aggregates  ──► PositionStore ──► TradeLockerAwareTradesReader ("TRADELOCKER_B_Trades")
     ├─ tradable-unit-aggr.  ──► SymbolIndex
     ├─ notifications        ──► bắt orderRejected theo correlation_id
     └─ positions:open / positions.{id}:close ◄── SendOrderAsync ◄── TradeLockerTradeExecutor ◄── TradeExecutionRouter
                                                    closed-positions-history ──► TradeLockerAwareHistoryReader ("TRADELOCKER_B_History")
```

Một session (`TradeLockerHermesSession`) cài cả `ITradeLockerQuoteSession` lẫn `ITradeLockerTradeSession` (như
`PrimeXbtFwsSession`). Socket rớt thì giá, Trades map và History map cùng fail-closed (`MapNotFound`, không trả map rỗng).

---

## 3. Bảng rủi ro

| # | Rủi ro | Mức | Giảm thiểu | Phase |
|---|---|---|---|---|
| R1 | API không chính thức; web đổi schema/route thì vỡ | Cao | Probe A7 so fixture ở mọi phase từ 4; `[DRIFT]` ⇒ `OpenBlockReason`; fail-closed | 0, 4, 8 |
| R2 | Open không idempotent (không client order id) | Cao | Timeout ⇒ `TIMEOUT_UNCERTAIN`, **không gửi lại**; một Open in-flight mỗi symbol+side; đối soát chỉ báo cáo | 3, 7A |
| R3 | JWT 5 phút có thể cắt socket | Trung bình | Q1; chính sách mint + nối lại chủ động trước hạn | 0, 4 |
| R4 | Tài khoản netting thay vì hedge | Cao (NO-GO) | Q2 | 0 |
| R5 | Position id ≥ 2^63 | Thấp | Q4; phương án dự phòng: bảng ánh xạ | 0, 1 |
| R6 | T&C cấm bot/reverse-engineer/thu thập tự động | **Đã xác nhận (2026-10-26)**: Demo Terms (a)(b)(d), Terms chung, 16.3. Xem [phase-0 §Q12](phase-0-spike.md#q12--điều-khoản-tradelocker-đọc-ngày-2026-10-26-tại-tradelockercomterms-and-conditions) | Chủ dự án quyết đi tiếp hay không | 0 |
| R7 | Tự viết RSocket: lỗi credit/keepalive/fragment ⇒ stream treo im lặng | Trung bình | Test codec trên fixture thật; liveness = tuổi tick + keepalive; watchdog stream | 3, 4 |
| R8 | Refresh token hết hạn ⇒ mất phiên giữa chừng | Trung bình | Q10; `OpenBlockReason` + Telegram trước hạn; Close vẫn chạy khi socket còn | 2, 8 |
| R9 | Khớp "position mới" mơ hồ khi nhiều slot mở cùng chiều sát nhau | Trung bình | Một Open in-flight mỗi symbol+side; khớp thêm quantity; Ambiguous ⇒ không đoán (như `PrimeXbtOrderMatcher`) | 3, 7A |
| R10 | Broker live không chạy APOLLO | Chưa biết | Q11; nếu vậy cần client khác ⇒ báo chủ dự án | 0, 9 |

---

## 4. Bản đồ điểm chạm trong code (đã grep 2026-10-26 trên `feature/b-price-offset` @ `9a4bd8f`)

| # | Vị trí | Việc | Phase |
|---|---|---|---|
| 1 | `NormalizePlatform` × **5**: `Application/Services/ConfigService.cs:30` và `:537`, `App/State/RuntimeConfigState.cs:543`, `Infrastructure/Supabase/SupabaseConfigRepository.cs:261`, `App/ViewModels/ConfigViewModel.cs:1298` | Thêm `"tradelocker"` **cùng một commit**; `Tests/Config/PlatformNormalizationTests.cs` khoá | 1 |
| 2 | `ConfigService.SaveByMachineHostNameAsync` (`:189`, reject A ở `:205`) | Reject `platform_a = tradelocker` | 1 |
| 3 | `TradeLegPlatform` (`App/Services/ITradeExecutionRouter.cs:11`, hiện `PrimeXbt = 3`) | `TradeLocker = 4` | 1 |
| 4 | `TradeExecutionRouter`: ctor, `ValidatePlatformOrThrow` (`:816`), `ResolveExecutor` (`:839`), readiness (`:468–472`) | Executor + readiness `LEG_B_NOT_READY` | 1, 7A |
| 5 | `PlatformBSwitchGuard.Evaluate` (`Application/Services/CTrader/PlatformBSwitchGuard.cs`), gọi từ `ConfigViewModel.cs:877` | Chặn đổi B khi còn slot nếu chạm `tradelocker` | 1 |
| 6 | `DashboardViewModel.ResolveTradeLegPlatform` (`:3645`) | `"tradelocker" => TradeLegPlatform.TradeLocker` | 1 |
| 7 | `RuntimeConfigState.CurrentMapName2` (`:116`), `CurrentConfirmLatencyMsBEffective` (`:82`), `UpdatePrimeXbt` (`:689`) làm mẫu cho `UpdateTradeLocker` | Kênh `TRADELOCKER_B`; latency B riêng; update tách khỏi `Update(...)` (bẫy sentinel) | 1, 2 |
| 8 | `SansJsonHelper` (`Application/Helpers/SansJsonHelper.cs`), `ConfigService` (`:156` gắn `PrimeXbt`), `ConfigLoadResult` (`:321`) | Khối `sans_json.tradelocker`; build **byte-identical** khi rỗng | 2 |
| 9 | `ConfigViewModel` `IsPlatformB*` (`:354–386`), load (`:799`), save (`:877–934`); `ConfigWindow.xaml` | Radio + panel TradeLocker; `IsPlatformBWithoutHwnd` gồm tradelocker | 2 |
| 10 | `SharedMemoryMarketDataReader.PollLoopAsync` (`:124–145`) | Nhánh quote TradeLocker **trước** `BPriceOffsetApplier.Apply` (`:145`) | 4 |
| 11 | `Infrastructure/DependencyInjection.cs:29–46` | Session + decorator `TradeLockerAware*` bọc **ngoài cùng** chuỗi `PrimeXbtAware(CTraderAware(...))` | 4, 5, 6 |
| 12 | `App/App.xaml.cs:60–110` | Executor (Null ở P1, thật ở 7A), browser service, monitor (resolve eager), `StopSync` khi thoát | 1, 2, 4, 7A |
| 13 | `DashboardViewModel.IsExchangeBCTrader` / `IsExchangeBWithoutHwnd` (`:865–872`), dùng ở `:881`, `:1101` (lot check cặp đầu), `:4201` | B không cần HWND; lot check cặp đầu áp dụng | 2, 7B |
| 14 | `DashboardViewModel.CanRetryCloseLegB` (`:5321`) → `PendingCloseRetryEligibility` | Retry đóng chân B không đòi HWND | 7B |
| 15 | `DashboardViewModel.LogHedgeVolumeConsistency` (`:10078`), `[BUILD]` (`:1421`), reload config (`:7644`) | Nhánh TradeLocker + Telegram `TRADELOCKER_HEDGE_VOLUME_MISMATCH`; `UpdateTradeLocker` khi reload | 2, 5, 7B |
| 16 | `PrimeXbtSessionMonitor` (mẫu) | `TradeLockerSessionMonitor` → `{date}-tradelocker.log` + Telegram | 4 |

Ticket (D3): `TradeSharedRecord.Ticket`, `HistorySharedRecord.Ticket`, `PositionSlot.TicketA/B`, `SlotPersistence.TicketA/B`
đều là `ulong`; `current_tick_a/b` lưu string, parse bằng `ulong.TryParse` (`DashboardViewModel.cs:8767`).

---

## 5. Tuân thủ Rule E / F / 0.0

- Executor TradeLocker **chỉ** là đường thực thi; quyết định mở/đóng vẫn đến từ signal engine (Rule E). Không thêm path mở/đóng mới.
- Đóng theo `v3.positions.{id}:close` chỉ cho ba nguồn hợp lệ: Auto, Manual per-pair, Recovery (Rule F).
  `v3.positions:close-all` **không bao giờ** xuất hiện trong code production (grep A6).
- Mọi OPEN/CLOSE đi qua physical dispatch mutex của router. Ticket B phải decode được bằng `TradeLockerTicketCodec`
  và khớp slot; sai thì fail-closed, không fallback.
- **CLAUDE.md §0.0**: AI chỉ đặt lệnh khi đã xác minh cả hai chân là demo:
  - TradeLocker: kerber `type`/`accName`.
  - MT5 A: title/journal "Demo Account".
  - Khi có host TradeLocker live, thêm vào danh sách "tài khoản đã biết là THẬT" trong §0.0.

---

## 6. Nguyên tắc an toàn khi thao tác với tài khoản

- Mặc định **DEMO**. Live chỉ do chủ dự án tự làm theo runbook Phase 9.
- Chỉ đóng vị thế do chính app/phiên thử mở ra; **không bao giờ** close-all.
- Không in/ghi refresh token, access token, account JWT, email ra log, tài liệu, fixture (che bằng `<JWT>`, `<RT>`).
- Thư mục `scan\trade-locker\.auth\` chứa token + profile Chrome (đã gitignore ở repo đó). Không copy sang trade-multi.
- Kết thúc mỗi phiên test, tài khoản demo phải **flat** (ghi vào §6 MASTER-PLAN).
