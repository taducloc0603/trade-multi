# Phase 0 — Khảo sát demo + GO/NO-GO

> Chạy **ngoài repo**. Không file nào trong `TradeDesktop.*` được đổi ở phase này.
> Mọi lệnh đặt ra 🛑 chỉ được chạy trên **OPEN_DEMO**, và chỉ khi chủ dự án đồng ý trong chính lượt đó (CLAUDE.md §0.0).

## Mục tiêu

1. Trả lời Q1–Q12 bằng số liệu thật (dữ liệu nền: [00-scan-findings.md](00-scan-findings.md) §7).
2. Chứng minh **.NET** nói chuyện được với hermes (RSocket + Protobuf). Đây là rủi ro kỹ thuật lớn nhất của việc port sang C#.
3. Thu fixture thật (frame base64 + JSON hera) cho test offline ở Phase 3.
4. Ghi baseline repo (test pass/fail, warning) cho `tools/recheck.ps1`.

## Chốt trước khi làm

| # | Câu hỏi | Đề xuất |
|---|---|---|
| C1 | Đo bằng công cụ nào? | Q1–Q12 dùng Node `scan\trade-locker` sẵn có (thêm lệnh `soak`, `probe`). Bằng chứng .NET: console C# nhỏ ở `C:\Users\laptop\source\tranminhman\scan\tradelocker-spike\` |
| C2 | Trần lệnh demo | ≤ 60 lệnh demo, lot tối thiểu, tài khoản phải **flat** khi kết thúc mỗi phiên 🛑 |
| C3 | Symbol | XAUUSD nếu có (Q3); nếu không, chủ dự án chọn symbol thay thế trước khi đi tiếp |
| C4 | Package cho spike C# | Spike được dùng `Google.Protobuf` (ngoài repo, không ảnh hưởng solution). Việc thêm package vào repo vẫn 🛑 ở Phase 3 |

## Bằng chứng demo (bắt buộc trước MỌI lệnh)

- kerber `GET v1/accounts` ⇒ ghi `type`, `accName`, `engine`, `cluster` của account dùng để test. Chỉ đi tiếp khi rõ ràng là demo.
- Phase 0 không chạm MT5; nếu có so giá với MT5 A thì chỉ đọc.
- Không xác minh được ⇒ coi là THẬT và dừng.

## Câu hỏi

| ID | Câu hỏi | Cách đo | Đặt lệnh? | Tiêu chí / ảnh hưởng thiết kế |
|---|---|---|---|---|
| Q1 | JWT account 5 phút hết hạn thì socket hermes có bị cắt không? Web xử lý thế nào? | `soak` ≥ 30 phút không reconnect chủ động, log thời điểm close/ERROR; bắt CDP của web khi để tab mở 15 phút | Không | Nếu bị cắt: session phải mint JWT mới và **nối lại chủ động trước hạn**, không đợi rớt. Quyết định chính sách ở Phase 4 |
| Q2 | Hedge: Buy + Sell cùng symbol cùng lúc, ≥ 3 position song song cùng chiều? | Mở 2 Buy + 1 Sell, đọc `position-aggregates` | 🛑 3–6 lệnh | Bắt buộc là position riêng biệt. Nếu netting ⇒ **NO-GO** (multi-slot cần hedge) |
| Q3 | XAUUSD: có không? `lot_size`, `tick_size`, `lot_step`, `minimum_lot`, `maximum_lot`, `trading_status` | `npm run trade -- symbols XAU` | Không | Ra `contractSizeB`, digits/point cho `PointDigitsConsistencyChecker` |
| Q4 | Độ lớn position id | Ghi id các position ở Q2 | (dùng Q2) | Phải < 2^63 để codec bit 63 dùng được. Nếu không ⇒ chuyển sang bảng ánh xạ id ↔ ticket (đổi thiết kế, báo chủ dự án) |
| Q5 | Server có gửi frame fragment (cờ FOLLOWS) không, nhất là `tradable-unit-aggregates`? | Spike C# log header từng frame | Không | Có ⇒ codec Phase 3 phải ghép fragment (đã dự trù) |
| Q6 | Timeout/rớt socket ngay sau khi gửi `:open` thì sao? Gửi `:close` cho position đã đóng thì sao? | Gửi rồi cắt socket ngay; đóng 2 lần | 🛑 ≤ 6 lệnh | Xác nhận quy tắc `TIMEOUT_UNCERTAIN` không gửi lại; lấy mã lỗi cho `ErrorMapper` |
| Q7 | Nhịp quote XAUUSD, tuổi tick p50/p95/p99/max | `soak` 60 phút giờ thị trường mở | Không | Đề xuất `confirmLatencyB` |
| Q8 | Mã `ProtoError` cho: sai lot step, lot < min, market đóng, symbol halted | Gửi lệnh sai cố ý (lot sai step); market đóng thì đo cuối tuần | 🛑 ≤ 4 lệnh (bị reject) | Bảng mã lỗi cho Phase 3 |
| Q9 | Rate limit | Gửi tuần tự 10 lệnh open/close cách nhau 1 s | 🛑 ≤ 20 lệnh | Giới hạn an toàn cho executor |
| Q10 | Tuổi thọ refresh token Keycloak | Đọc claim `exp` của refresh token (không in token); thử refresh sau 24 h | Không | Chu kỳ phải đăng nhập lại; cảnh báo Telegram trước hạn |
| Q11 | Broker live (khi chọn) có chạy APOLLO / `app.tradelocker.com` không, brand/cluster là gì? | Hỏi chủ dự án; nếu đã có tài khoản thì chỉ ĐỌC `v1/accounts` | Không | Không phải APOLLO ⇒ cần client khác (REST Public API cũ) ⇒ báo chủ dự án |
| Q12 | T&C của TradeLocker/broker có cấm bot, arbitrage, latency arbitrage không? | Đọc T&C | Không | Bài học PrimeXBT Q9: phát hiện cấm ⇒ dừng, chủ dự án quyết |
| Q13 | Bằng chứng .NET: SETUP + `tradable-unit-aggregates` + `tickers.{id}.quotes` + `position-aggregates` bằng `ClientWebSocket` (header `Origin`) | Spike C# chạy 10 phút | Không | **Bắt buộc cho GO** |

## Nghiệm thu (A = bắt buộc cho GO)

| ID | Tiêu chí |
|---|---|
| 0-A1 | Q13 ✅: spike C# giữ socket ≥ 10 phút, nhận quote + positions, decode đúng (so với Node chạy song song) |
| 0-A2 | Q1 có kết luận rõ và chính sách JWT/reconnect được ghi lại |
| 0-A3 | Q2 = hedge thật |
| 0-A4 | Q3 có XAUUSD, hoặc chủ dự án chốt symbol khác |
| 0-A5 | Q4 id < 2^63, hoặc chủ dự án duyệt thiết kế bảng ánh xạ |
| 0-A6 | Q12 không cấm, hoặc chủ dự án chấp nhận rủi ro bằng văn bản trong §6 MASTER-PLAN |
| 0-A7 | Fixture đã lưu vào `docs/plans/tradelocker/fixtures/` (đã che JWT/token/email, chỉ giữ id demo) |
| 0-A8 | Tài khoản demo **flat**; số lệnh đã dùng ≤ trần C2 |
| 0-A9 | `tools/recheck.ps1 -Phase 0 -WriteBaseline` tạo baseline; repo `TradeDesktop.*` không đổi |

## Kết luận cổng 0

- **GO**: 0-A1…0-A9 đều ✅ (hoặc có quyết định của chủ dự án ghi ở §6).
- **NO-GO**: netting (Q2), không giữ được socket từ .NET (Q13), hoặc broker live không phải APOLLO mà chủ dự án không muốn làm client thứ hai.

## Memo (điền khi xong)

| Mục | Giá trị |
|---|---|
| Ngày / người chạy | 2026-10-26, agent. Công cụ: Node `scan\trade-locker\src\scripts\soak.ts` (mới, chỉ đọc) + spike C# `scan\tradelocker-spike` (mới, chỉ đọc, không có request-response nên không thể gửi lệnh) |
| Account demo (type / engine / cluster) | `PRDTL#O#1791342294192995800`, `OPEN_DEMO`, brand "TradeLocker Demo", APOLLO, `apl0`. Là account duy nhất của profile |
| Q1 kết luận + chính sách | ✅ (1 mốc) JWT hết hạn 04:09:36 UTC. Phút 04:10:39 vẫn có 123 tick và 0 lần disconnect ⇒ server **không** cắt socket khi JWT hết hạn (auth chỉ kiểm lúc SETUP). Chính sách đề xuất: chỉ mint JWT mới khi (re)connect và khi gọi hera REST; không chủ động nối lại theo hạn JWT. Xác nhận lại bằng spike C#: JWT hết hạn 04:19:33, kết nối vẫn sống thêm ≥ 8 phút. Soak dài (≥ 1 h) để ở T0/Phase 4 |
| Q2 hedge | ✅ **Có bằng chứng sẵn, không cần lệnh mới**: SPX500 đang có BUY `895596180090928800` và SELL `895678797842759191` mở song song (hai position riêng, mở 2026-10-07) |
| Q3 XAUUSD lot_size / tick_size / step / min / max | ✅ `lotSize=100.0` (1 lot = 100 oz), tick 0.01, step 0.01, min 0.01, max 1000.0, `tradingStatus=1`. 33 instrument trên demo |
| Q4 id lớn nhất thấy được | ✅ Position id ~8.96e17 (< 2^60) ⇒ codec bit 63 dư sức |
| Q5 fragment | ✅ Không thấy: `tradable-unit-aggregates` ~28 KB về trong **một** frame; 0 frame FOLLOWS. Codec Phase 3 vẫn giữ ghép fragment (rẻ, phòng xa) |
| Q7 tuổi tick p50 / p95 / p99 / max ⇒ `confirmLatencyB` đề xuất | XAUUSD phiên Á 2026-10-26. Node 04:04–04:10 UTC: p50 ~465 ms, p95 ~1.2 s, p99 ~2.5 s. Spike C# 15' (04:14–04:29, 1 414 tick): p50 469 ms, p95 1 719 ms, p99 3 984 ms, max 7 922 ms. Spread ~0.26. Ngưỡng 2 500 ms sẽ chặn khoảng 2–3 % thời gian. Chốt `confirmLatencyB` ở Phase 4 sau soak phiên Âu/Mỹ |
| Q6 timeout / đóng trùng | ✅ (2026-10-26, `experiments.ts`). **Đóng position đã đóng** và **đóng id không tồn tại** đều trả ERROR `NOT_FOUND` (`messageArguments: entity=POSITION`). **Cắt socket 4 ms sau khi gửi `:open`**: request lỗi "Closed." (không có ack) nhưng sau reconnect **có position BUY mới OPEN** ⇒ lệnh vẫn khớp dù client thấy lỗi. Quy tắc bắt buộc: lỗi transport sau khi gửi = `TIMEOUT_UNCERTAIN`, **không bao giờ gửi lại**, đối soát qua positions (giống PrimeXBT Q5) |
| Q8 bảng mã lỗi | ✅ Lỗi validation trả **đồng bộ** qua frame ERROR của request-response (base64 `ProtoError`), không qua `v3.notifications` (0 notification `orderRejected`). Lot 0.015 và 0.001 (< min) ⇒ `QUANTITY_NOT_ON_LOT_STEP_GRID`. Lot 0 ⇒ `INPUT_VALIDATION`, có `inputValidationErrorDetails` (`ordersList[0].marketOrder.quantity`, `POSITIVE`). Đóng sai ⇒ `NOT_FOUND`. Market đóng / symbol halted: chưa đo (cần cuối tuần) |
| Q9 rate limit | ✅ 5 vòng open/close cách ~1.5 s: không lỗi. Ack ~250 ms (p max 339), open→OPEN 519–646 ms, close→xoá 471–654 ms |
| R9 khớp "position mới" | ❌ **Phát hiện quan trọng**: 2 lệnh BUY gửi đồng thời ⇒ cách khớp của client Node ("position đầu tiên chưa thấy") trả **cùng một id** `902413599387337493` cho cả hai; position thứ hai `902413599383143131` không được ai nhận (script bắt được, đã đóng). Hệ quả thiết kế: executor **bắt buộc** chỉ có một Open in-flight mỗi symbol (chờ tới khi khớp xong hoặc hết hạn rồi mới gửi lệnh kế); matcher gặp ≥ 2 position mới ⇒ `Ambiguous`, không đoán |
| Q10 hạn refresh token | Refresh token Keycloak: `exp - iat` = **30 ngày** (cấp 2026-10-08, hết 2026-11-07); chưa đo có trượt khi refresh không |
| Q12 T&C | ❌ **CẤM** (xem mục dưới) ⇒ chủ dự án **chấp nhận rủi ro, đi tiếp** (2026-10-26) |
| Q13 .NET | ✅ **0-A1 đạt**: `watch` (04:14:37–04:29 UTC) giữ **một** kết nối ≥ 13 phút, 0 reconnect, 77–117 tick/phút, positions = 3 ổn định; JWT hết hạn 04:19:33 mà tick vẫn đều tới phút 13 (8 phút sau hạn). SETUP bearer, 3 stream, keepalive 5 s chạy đúng, giá khớp Node. Fixture đã vào [fixtures/](fixtures/) (0-A7 ✅) |
| Số lệnh demo đã dùng / flat? | Chủ dự án đồng ý 2026-10-26. XAUUSD 0.01: **9 lệnh mở khớp, 9 lệnh đóng, 3 lệnh bị reject (Q8), 2 lệnh đóng lỗi NOT_FOUND (Q6)** (≤ trần 60). Mọi position do phiên mở **đã đóng**. Còn 3 position có từ đợt quét 2026-10-07 (SPX500 BUY + SELL, XAUUSD SELL), không đụng tới vì chủ dự án chưa yêu cầu |
| Kết luận | **Đề xuất GO** (0-A1…0-A9 ✅; 0-A8 chỉ thiếu phần 3 position cũ). Chờ chủ dự án chốt 🛑 |

### Hệ quả thiết kế từ Phase 0 (đưa vào Phase 3 / 7A)

1. `ErrorMapper` đọc `ProtoError.code` từ frame ERROR của request-response. Mã đã thấy: `QUANTITY_NOT_ON_LOT_STEP_GRID`, `INPUT_VALIDATION`, `NOT_FOUND`. `v3.notifications` chỉ là kênh phụ.
2. Close nhận `NOT_FOUND` thì coi là "đã đóng" **chỉ khi** PositionStore cũng không còn id đó; ngược lại báo lỗi.
3. Lỗi transport sau khi đã gửi ⇒ `TIMEOUT_UNCERTAIN`, không gửi lại, đối soát qua positions (Q6b chứng minh lệnh vẫn khớp).
4. Một Open in-flight **mỗi symbol** (R9). Matcher: đúng một position mới cùng unit + side + qty ⇒ khớp; ≥ 2 ⇒ `Ambiguous`.
5. JWT: mint khi (re)connect và khi gọi REST; không chủ động nối lại theo hạn JWT (Q1).
6. Planner kiểm lot theo `lot_step` / `minimum_lot` / `maximum_lot` trước khi gửi, để không tốn round-trip vào lỗi `QUANTITY_NOT_ON_LOT_STEP_GRID`.

### Q12 — điều khoản TradeLocker (đọc ngày 2026-10-26 tại tradelocker.com/terms-and-conditions)

Trang gồm Terms chung và phần **"TRADELOCKER DEMO ACCESS — TERMS AND CONDITIONS"** (hiệu lực 2026-04-01), áp dụng trực tiếp cho OPEN_DEMO đang dùng:

- **Demo Terms (a):** cấm dùng Demo Service "for any commercial purpose, including without limitation to develop, test, or optimise automated trading strategies, algorithms, or systems intended for commercial deployment, unless separately authorised in writing by Tradelocker".
- **Demo Terms (b):** cấm "reverse engineer, decompile, disassemble, or otherwise attempt to derive the source code, underlying algorithms, or architecture of the Tradelocker platform".
- **Demo Terms (d):** cấm "scrape, extract, harvest, or collect any data from the Demo Service by automated means, including through bots, crawlers, spiders, or similar technologies".
- **Terms chung:** cấm "scrape or collect data from the TradeLocker Platform by bots, crawlers, scripts or other automated means, except through tools we provide such as TradeLocker Studio". Mục 16.3: cấm "attempt to access or reverse-engineer the source code … or attempt to interfere with, override, alter, or bypass any security mechanisms".
- Không thấy điều khoản nào nói riêng về arbitrage/latency arbitrage. Với tài khoản live qua broker/prop firm, điều khoản của broker đó áp dụng thêm.
- Bot được phép qua **TradeLocker Studio** (công cụ chính thức).

Hệ quả: toàn bộ hướng tích hợp hiện tại thuộc diện bị cấm theo Demo Terms. Hướng này gồm giao thức hermes dò ngược từ bundle web, client tự động ngoài trình duyệt, và mục đích phát triển hệ thống giao dịch tự động. Bản thân việc soak/spike chỉ đọc ở Phase 0 cũng là "collect data by automated means". Agent đã dừng các tiến trình nền sau khi chốt Q1 và **không** chạy thêm thí nghiệm nào.
