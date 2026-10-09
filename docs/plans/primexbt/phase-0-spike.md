# Phase 0 — Spike ngoài app + cổng GO/NO-GO

> **Không sửa một dòng nào trong `TradeDesktop.*`.** Phase này trả lời các câu hỏi chặn (Q1–Q9 của
> [00-scan-findings §9](00-scan-findings.md#9-câu-hỏi-còn-mở-chuyển-sang-phase-0)) bằng một chương trình thử
> độc lập, rồi chọn Track M / Track W / NO-GO.

[Index](README.md) · Phase sau: [Phase 1](phase-1-platform-enum.md) (nếu GO-W) hoặc [Track M](track-m-mt5.md) (nếu GO-M)

---

## Mục tiêu

1. Chứng minh (hoặc bác bỏ) rằng một **client .NET ngoài trình duyệt** giữ được kết nối `fws` nhiều giờ, đọc giá,
   đọc vị thế, đặt/đóng lệnh demo, và tự làm mới token.
2. Đo số liệu thật làm đầu vào cho config: nhịp tick, ack, thời gian thấy vị thế, lệch đồng hồ.
3. Đánh giá Track M (MT5 của PrimeXBT) song song để so sánh.

## Phụ thuộc

- Bản quét [00-scan-findings.md](00-scan-findings.md) + [fixtures/](fixtures/).
- User có tài khoản DEMO PXTrader 2.0 (đã có) và sẵn sàng tạo tài khoản DEMO MT5 tại PrimeXBT cho Track M.

## Cổng vào (recheck)

| # | Kiểm tra | Cách làm |
|---|---|---|
| 0.R1 | Repo sạch, ghi commit gốc | `git status`, `git rev-parse HEAD` |
| 0.R2 | **Baseline test** — ghi số pass/fail **và danh sách tên test fail** | `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj` → lưu vào §Memo |
| 0.R3 | Build Release, số warning | `dotnet build -c Release` → lưu số warning làm baseline |
| 0.R4 | Fixture vẫn khớp live | Mở web demo bằng Playwright MCP, so `fx/market`, `positions`, `orders/market/place` với [fixtures/](fixtures/). `client-version` khác 34866 ⇒ ghi lại |

## Chốt trước khi code

| # | Câu hỏi | Đề xuất |
|---|---|---|
| 1 | Viết spike bằng gì, đặt ở đâu? | **C# console .NET 8** tại `C:\Users\laptop\source\tranminhman\scan\primexbt\` (ngoài repo, cạnh `trade-locker`). Phải là .NET vì Q1 hỏi đúng `ClientWebSocket` mà app sẽ dùng; TypeScript chỉ chứng minh được Node. |
| 2 | Lấy token ban đầu thế nào? | Như `trade-locker/src/ui/browser.ts`: mở **Chrome thật** + `--remote-debugging-port`, user tự đăng nhập, spike đọc `localStorage["prm-token"]`, `prm-device-print`, cookie `refresh_token` qua CDP, lưu `.auth/` (gitignore, DPAPI). |
| 3 | Lệnh demo được phép tối đa bao nhiêu? | Đề xuất ≤ 60 lệnh demo, 0.01 oz, chỉ XAU/USD, chỉ đóng theo `positionId` do spike mở. **Xin xác nhận trước khi chạy 0.6–0.8.** |

## Việc làm

| Bước | Nội dung | Trả lời |
|---|---|---|
| 0.1 | Tạo `scan/primexbt/` (README, `.gitignore` gồm `.auth/`, `.playwright-mcp/`, `bin/`, `obj/`), `memory.md` ("trao đổi bằng tiếng Việt; chỉ demo; không close-all") | — |
| 0.2 | `login`: mở Chrome thật qua CDP, chờ user đăng nhập, lưu token/cookie/device-print | Q8 (bắt luôn request sign-in để biết có captcha/2FA) |
| 0.3 | `watch`: `ClientWebSocket` → `fws`, gửi đúng chuỗi subscribe của [ws-session.json](fixtures/ws-session.json), heartbeat `time` 20 s. Thử 4 biến thể: (a) đủ `jwt`+`auth-guard`+`Origin: https://primexbt.com`; (b) bỏ `auth-guard`; (c) bỏ `Origin`; (d) `auth-guard` ngẫu nhiên | **Q1** |
| 0.4 | `watch --minutes 60`: thống kê khoảng tick (min/p50/p90/p99/max), tick trùng, reconnect, lệch `time` server vs máy | Đầu vào latency B (P7) |
| 0.5 | `refresh`: gọi `GET /v2/auth/refresh` ngoài trình duyệt; giải mã `exp` JWT mới; mở lại socket bằng JWT mới | **Q2** |
| 0.6 | `concurrent`: spike giữ socket trong khi user mở web cùng tài khoản 10 phút; ngược lại | **Q3** |
| 0.7 | `reject` (demo, cần đồng ý): `qty 0.001` (dưới min), `qty 0.015` (sai step), `symbol` không tồn tại, `positions/id/close` với id không tồn tại, đặt lệnh khi `isMarketOpen=false` (cuối tuần). Riêng `XAU/USD.24` chỉ đọc `trade-settings` để ghi khác biệt, **không đặt lệnh** | **Q4** — dạng `body.error` |
| 0.8 | `cycle --n 20` (demo, cần đồng ý): Buy → chờ sub-position → `positions/id/close`; đo ack, thấy vị thế, biến mất. Kiểm tra quy tắc khớp D3 (`openTime==executedAt` ∧ giá) đúng 20/20 | Đầu vào `open_pending_time` (P6), D3 |
| 0.9 | `drop` (demo, cần đồng ý): gửi `orders/market/place` rồi **đóng socket ngay**; reconnect; đối soát `report/orders2` + `positions`; đóng vị thế còn lại | **Q5** |
| 0.10 | Rate: 10 req/s `time` trong 10 s, quan sát có bị đóng/giới hạn | Q6 (nhẹ, không spam lệnh) |
| 0.11 | Đọc Terms of Service phần automated trading / API | Q9 — **chủ dự án quyết** |
| 0.12 | **Track M**: user tạo tài khoản DEMO MT5 tại PrimeXBT; cài EA DataExporter; chạy song song 1 h: so bid/ask MT5 với `fx/market` cùng thời điểm, spread, nhịp tick, giờ giao dịch, contract size, digits | **Q7** |
| 0.13 | Viết `probe`: kết nối, subscribe, so tên route/trường/kiểu với fixture, in diff. Dùng lại ở cổng vào Phase 4–8 | P9 |

## Rủi ro liên quan

P1, P2, P5, P6, P7, P9, P12 ([README §4](README.md#4-bảng-rủi-ro-p1p12)).

## Nghiệm thu

| # | Tiêu chí | Kết quả |
|---|---|---|
| A1 | Q1: socket `fws` mở và giữ ≥ 60 phút từ .NET, không cần trình duyệt chạy | |
| A2 | Q2: refresh ngoài trình duyệt thành công ≥ 2 lần liên tiếp | |
| A3 | Q3: chạy song song với web không làm rớt bên nào (hoặc có quy tắc rõ ràng) | |
| A4 | Q4: có mẫu `error` cho ≥ 2 kiểu từ chối, lưu vào `fixtures/trading.json` | |
| A5 | Q5: hành vi khi rớt socket sau khi gửi lệnh được mô tả + có thủ tục đối soát | |
| A6 | D3 khớp đúng 20/20 chu kỳ; không có trường hợp hai sub-position cùng `openTime` | |
| A7 | Số liệu tick/ack/position-visible có p50/p95/max | |
| A8 | Track M: bảng so sánh giá/spread/latency MT5 vs PXTrader | |
| A9 | Tài khoản demo **flat** sau phase; repo `TradeDesktop.*` không đổi (`git diff --stat` rỗng) | |

## Rollback

Không có gì để rollback trong repo. Xoá `scan/primexbt/.auth/` và Sign out mọi phiên web nếu dừng dự án.

## Cổng ra

Viết **Memo kết quả** (dưới đây) với một trong ba kết luận, chủ dự án ký:
- **GO-W**: A1, A2, A6 PASS bắt buộc; A3/A4/A5 PASS hoặc có biện pháp giảm thiểu được ghi rõ.
- **GO-M**: A8 cho thấy MT5-PrimeXBT đủ dùng và chủ dự án chọn đường ít rủi ro.
- **NO-GO**: A1 hoặc A2 FAIL không có cách khắc phục, hoặc Q9 cấm.

## Memo kết quả

> Spike: `C:\Users\laptop\source\tranminhman\scan\primexbt\` (C# .NET 8, `PrimeXbtSpike`). Kết quả máy đọc được ở `scan\primexbt\results\*.json`.

### Kết quả từng câu hỏi (2026-10-09)

| Q | Kết quả | Bằng chứng |
|---|---|---|
| **Q1** socket ngoài trình duyệt | ✅ **PASS.** Bắt buộc: `jwt` trên URL **+ cookie `fws_token`** (hoặc `ws_token`). **Không** cần `auth-guard` (bỏ hoặc random đều OK), `Origin`, `client-version`. Thiếu JWT hoặc thiếu cookie ⇒ HTTP 401 | `results/variants-*.json` |
| **Q2** refresh ngoài trình duyệt | ✅ **PASS 2/2.** `GET /v2/auth/refresh` (cookie `refresh_token`, không cần `x-auth-guard` thật) → `{access_token, expires_in}`; JWT mới hạn 7 ngày; **cookie KHÔNG xoay** (refresh_token dùng lại được); socket mở bằng JWT mới OK; **socket cũ đang chạy không bị rớt** khi refresh | `results/refresh-*.json` |
| **Q3** chạy song song với web | ✅ Spike (nhiều socket) và tab web cùng tài khoản chạy đồng thời, không bên nào bị đá | variants + watch trong lúc tab Trade mở |
| **Q4** lệnh bị từ chối | ✅ Hai dạng lỗi: nghiệp vụ `body:{id:null,error:"TOO_LOW_AMOUNT"\|"WRONG_ORDER_AMOUNT"}`; tham số: `error:{code:"WRONG_ARGS"\|"POSITION_NOT_FOUND",description}` ở cấp trên, không có `body`. ~250 ms | `results/reject-*.json`, `fixtures/trading.json` |
| **Q5** rớt socket sau khi gửi | ✅ Lệnh **VẪN KHỚP**; đối soát `report/orders2` (placedAt ≥ gửi−2s, side, qty, `openReason=CLIENT`) + `positions` tìm đúng 1 vị thế → đóng. ⇒ D4: **không bao giờ gửi lại** | `results/drop-*.json` |
| **A6** khớp order→position (D3) | ✅ **20/20** chu kỳ, mỗi lần đúng 1 ứng viên. Ack mở p50 261 / p95 318 / max 338 ms; thấy vị thế p50 1105 / p95 1700 / **max 1906 ms**; ack đóng p50 253 / max 609; biến mất p50 1680 / **max 1903 ms** | `results/cycle-*.json` |
| **Q6** rate | ✅ 100/100 request `time` ở 10 req/s, không giới hạn. **RTT ~240 ms** (p50 242, p99 283) — đây là sàn của ack lệnh | `results/rate-*.json` |
| Q7 Track M | ⏭ bỏ qua — chủ dự án chọn đi tiếp Track W ("Tiếp tục, tôi chấp nhận rủi ro") | |
| Q8 sign-in API | ⏭ không cần cho GO: token lấy qua `storageState` của trình duyệt đã đăng nhập + refresh được ngoài trình duyệt. Cách đăng nhập trong app chốt ở Phase 2 | |
| **Q9** Điều khoản | ❌ **Rủi ro cao — xem dưới** | T&C global `assets/documents/pdf/primexbt/terms-and-conditions.pdf` (73 trang) |
| **A1** giữ socket 60' | ✅ **PASS.** 60/60 phút, 0 heartbeat timeout, không reconnect; refresh token 2 lần giữa chừng không làm rớt | `results/watch-20261009-043055.json` |
| Nhịp tick (60', 7 768 tick) | p50 276 / p90 780 / **p95 1 021 / p99 1 773 / max 4 579 ms**; khoảng > 1 s: 642 (8,3 %), > 2 s: 54 (0,7 %), > 5 s: 0; 18,5 % tick lặp y hệt bid/ask | watch |
| Nhịp snapshot `positions` | Có vị thế: ~1 s (cycle). **Khi tài khoản flat: rất thưa** (p50 1,1 s nhưng p99 12,5 s, max **6 phút**) ⇒ KHÔNG dùng độ "tươi" của snapshot làm tín hiệu sống khi flat — dùng heartbeat | watch |
| Lệch đồng hồ server − máy | **−774 ms** (p50; dải −1 101…−719) — máy nhanh hơn server ~0,8 s. Đối soát theo `placedAt` (Q5) phải quy đổi bằng offset `time` hoặc nới cửa sổ ≥ 2 s | watch |
| Probe (0.13) | ✅ `PROBE OK` — không thiếu trường, không đổi kiểu; `symbolId` vẫn 1019 | `results/probe-*.json` |

Ghi chú hạ tầng: `primexbt.com` (Cloudflare) **cắt kết nối TLS của client không phải trình duyệt** (curl/schannel, Node); `api.primexbt.com` thì nhận client .NET bình thường.

### Q9 — trích Terms and Conditions (bản global, 73 trang)

| Điều | Nội dung (trích) | Liên quan |
|---|---|---|
| 5.2.4 | Cấm "voluntarily and/or involuntarily partaking in **arbitrage**, including but not limited to, **latency arbitrage** and swap arbitrage" | Chiến lược gap `B.Bid − A.Ask` giữa hai sàn là arbitrage giá liên sàn — áp dụng cho **cả Track M lẫn Track W** |
| 5.4 | "expressly prohibited to employ … **bots**, spiders, or other **automated** devices, programs, scripts, algorithms … to access, obtain, copy, or monitor any part of the properties" | Track W (client tự động qua API web) |
| 3.2.5 | Cấm reverse engineer chương trình vận hành dịch vụ | Track W (giao thức tự dò) |
| 16.5.16 | Latency abuse / latency arbitrage ⇒ **huỷ lệnh, tịch thu lợi nhuận, đóng tài khoản không báo trước** | Cả hai track |
| 16.5.20 | Cấm khai thác latency bằng automated bots / high-frequency trên nền tảng crypto | Cả hai track |
| 23.1 (p65) | Vi phạm khi "hedge your exposure using multiple Accounts … with whom you collude against PrimeXBT" | Cẩn trọng nếu dùng nhiều tài khoản PrimeXBT |

⇒ Theo tiêu chí cổng: "**NO-GO** khi Q9 cấm". Quyết định thuộc **chủ dự án** (🛑).
**Chủ dự án quyết 2026-10-09: "Tiếp tục, tôi chấp nhận rủi ro".**

### Bảng tổng kết

| Mục | Giá trị |
|---|---|
| Ngày / commit gốc | 2026-10-09 / `0662196` |
| Baseline test (pass / fail) | 1038 / 11 — danh sách: `tools/baseline-failed-tests.txt` |
| Baseline warning (Release, --no-incremental) | 3 |
| `client-version` lúc spike | 34866 |
| Tick interval p50 / p95 / p99 / max (ms) | 276 / 1 021 / 1 773 / 4 579 |
| Ack mở p50 / p95 (ms) | 261 / 318 |
| Position-visible p50 / p95 / max (ms) | 1 105 / 1 700 / 1 906 |
| Lệch đồng hồ server − máy | −774 ms |
| Đề xuất `confirmLatencyB` (tuổi tick) | **2 000 ms** (chặn ~0,7 % thời gian; 1 500 ms chặn ~3–4 %) — chốt ở Phase 2/4 |
| Đề xuất `open_pending_time` tối thiểu khi B = primexbt | ≥ 5 000 ms (max thấy vị thế 1,9 s + biên); config hiện tại 30 000 ms đã đủ |
| Lệnh demo đã dùng | 46 / 60; tài khoản flat |
| Nghiệm thu | A1 ✅ · A2 ✅ · A3 ✅ · A4 ✅ · A5 ✅ · A6 ✅ 20/20 · A7 ✅ · A8 ⏭ (chọn W) · A9 ✅ (flat; repo không đổi trong Phase 0) |
| **Kết luận** | **GO-W** (chủ dự án chấp nhận rủi ro Q9) |

| Mục | Giá trị |
|---|---|
| Ngày / commit gốc | |
| Baseline test (pass / fail + danh sách) | |
| Baseline warning | |
| `client-version` lúc spike | |
| Tick interval p50 / p95 / max (ms) | |
| Ack p50 / p95 (ms) | |
| Position-visible p50 / p95 / max (ms) | |
| Lệch đồng hồ server–máy | |
| Đề xuất `primexbt_confirm_latency_b` | |
| Đề xuất `open_pending_time` tối thiểu khi B = primexbt | |
| Kết luận | GO-W / GO-M / NO-GO |

## Nhật ký recheck

| Ngày | Commit | Test (pass/fail) | Kết luận |
|---|---|---|---|
| | | | |
