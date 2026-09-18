# Bộ prompt thực thi — cTrader FIX API cho sàn B

[← Index](README.md)

## Cách dùng

1. **Mỗi phase một phiên Claude Code mới.** Prompt tự đủ, không dựa vào trí nhớ phiên trước.
2. Copy **nguyên khối** ```text``` của phase cần làm, dán làm message đầu tiên.
3. Phiên sẽ **dừng ở Bước 1** nếu phát hiện vấn đề ở phase trước — đọc bảng 4 cột, duyệt hoặc sửa hướng,
   rồi mới cho đi tiếp. Không bao giờ bỏ qua bước này.
4. Bước 2 có thể hỏi bạn (AskUserQuestion) nếu "Chốt trước khi code" còn câu chưa quyết.
5. Phase 4 và 7 sẽ **chờ bạn duyệt bằng chữ** ở Bước 3 trước khi chạm code live / đặt lệnh thật.

Mọi prompt dùng chung một **khung 6 bước**; phần in hoa "ĐẶC THÙ PHASE N" là phần khác nhau.

## Môi trường thực thi hiện tại (cập nhật 2026-09-16 — đọc trước khi dán prompt)

Các prompt bên dưới đã được điều chỉnh theo bối cảnh này; nếu bối cảnh đổi thì sửa mục này trước.

| Mục | Hiện trạng | Hệ quả lên cách chạy |
|---|---|---|
| OS | **Windows 11**, 2 terminal MT5 (`terminal64.exe`) đang chạy | Smoke test, ma trận 6 ô platform, test rollback đều làm được **tại chỗ**. Không còn "CHƯA KIỂM — cần Windows" |
| cTrader | **cTrader Web** (`ct.fxpro.com`) đã đăng nhập trong **Playwright MCP** — **chỉ đọc** (ràng buộc #2 README §1) | Claude đọc Symbol info / tab Positions / History / Bid-Ask làm bằng chứng đối chiếu. **Không bấm** New order / Close. Snapshot nằm ở `.playwright-mcp/` (đã `.gitignore`, chứa email đăng nhập) — chỉ chép số liệu vào memo, không chép file |
| Spike FIX | `ConsoleSample` của Spotware là **menu tương tác đọc stdin** | Claude **không tự chạy được** (Bash non-interactive). Mô hình: **người dùng chạy trong terminal riêng, gõ lệnh theo hướng dẫn, dán output đã che `554=`**; Claude phân tích + đối chiếu web. Claude có thể `git clone`/`dotnet restore` giúp |
| Mật khẩu FIX | = mật khẩu đăng nhập tài khoản 10649643 (panel FxPro) | Người dùng **tự gõ** vào `Config-dev.cfg` ở `C:\tmp\ctrader-spike\` (ngoài repo). Claude **không đọc file đó, không nhận qua chat, không in ra** |
| Giờ giao dịch XAUUSD | 05:00 → 03:59:45 hôm sau (UTC+7), nghỉ ~1 giờ/ngày | Mọi bước đặt lệnh thật (Phase 0 câu 1/4, Phase 7) phải nằm trong khung này |
| Baseline test | macOS = 11 fail kèm danh sách (memo §2.2); **Windows chưa đo** | Bước 1 mọi phase so **cả con số lẫn danh sách tên test** với memo §2. Một fail mới thay một fail cũ cũng là vấn đề |
| Tiến độ Phase 0 | Xong: hedging, symbol 41/digits 2/lot 100 Oz, digits chân A = 2, panel FIX khớp, R10, `open_pending_time_ms=30000` | Prompt Phase 0 là bản **TIẾP TỤC**, không làm lại |

---

## Mẫu bảng báo cáo vấn đề (Bước 1)

| # | Vấn đề (file:dòng, bằng chứng) | Giải pháp đề xuất | Ảnh hưởng (phạm vi, phase nào bị lùi) | Rủi ro (nếu sửa / nếu không sửa) |
|---|---|---|---|---|
| 1 | … | … | … | … |

---

## Phase 0 — Spike khử rủi ro

File plan: [phase-0-spike.md](phase-0-spike.md)

```text
Bạn đang thực hiện PHASE 0 của kế hoạch tích hợp cTrader FIX API cho sàn B — TIẾP TỤC, không làm lại.

ĐỌC TRƯỚC, THEO THỨ TỰ, KHÔNG BỎ QUA:
1. CLAUDE.md (toàn bộ — đặc biệt §0.1 Risk Assessment, §0.2 Không thay đổi logic, §2 Rule A–G)
2. docs/plans/ctrader-fix/README.md (R1–R11, §4, §8 mục "cTrader Web + Playwright MCP", Phụ lục A/B)
3. docs/plans/ctrader-fix/phase-0-spike.md (phase NÀY — để thực hiện)
4. docs/plans/ctrader-fix/phase-0-memo.md (tiến độ THẬT — §3 và §6 nói cái gì đã xong, cái gì còn)
5. docs/plans/ctrader-fix/PROMPTS.md mục "Môi trường thực thi hiện tại"

ĐÃ XONG — KHÔNG LÀM LẠI (bằng chứng trong memo §3): tài khoản demo 10649643 là Hedging; symbol 41 =
XAUUSD, digits 2, lot size 100 Oz (cTrader Web); chân A MT5 Digits 2 / Contract size 100; panel FIX API
khớp README §1 từng trường; R10 xác nhận; open_pending_time_ms = 30000; baseline macOS = 11 fail kèm
danh sách (memo §2.2); nhóm SOS 2/11 fail đã phân loại sơ bộ (memo §4.1).

BƯỚC 1 — CHUẨN BỊ (Phase 0 không có phase trước)
- `git status`: các file docs/plans/ctrader-fix/* và .gitignore đang sửa là CHẤP NHẬN (tài liệu cập
  nhật 2026-09-16). Bất kỳ file khác bị sửa → báo.
- Branch dev-5-release-111-ctrader; xác nhận `git merge-base --is-ancestor dev-5-gap-on-dinh HEAD`.
- ĐO BASELINE WINDOWS (memo §6.1 — bắt buộc, làm đầu tiên):
  DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj
  Ghi số pass/fail VÀ danh sách tên test fail vào memo §2; so với danh sách 11 của macOS. Lệch → ghi rõ
  test nào chỉ fail ở một OS.

Nếu phát hiện BẤT KỲ vấn đề nào, trình bày theo đúng bảng này rồi DỪNG, chờ tôi duyệt:

| # | Vấn đề (file:dòng, bằng chứng) | Giải pháp đề xuất | Ảnh hưởng (phạm vi, phase nào bị lùi) | Rủi ro (nếu sửa / nếu không sửa) |
|---|---|---|---|---|

Không sửa gì ở bước này. Không sang bước 2 khi bảng trên còn dòng chưa được tôi duyệt.
Nếu không có vấn đề: ghi rõ "Chuẩn bị Phase 0: OK" kèm baseline Windows.

BƯỚC 2 — CHỐT TRƯỚC KHI CODE
Cả 4 câu ở phase-0-spike.md đã ghi ĐÃ QUYẾT / ĐÃ XÁC NHẬN (SSL 5211/5212; mật khẩu plaintext trong
Config-dev.cfg ngoài repo; được đặt lệnh demo trong giờ mở; tài khoản Hedging). Nhắc lại, không hỏi lại.

BƯỚC 3 — ĐÁNH GIÁ RỦI RO THEO CLAUDE.md §0.1
Phase 0 là ZERO code production. Chỉ chạm docs/plans/ctrader-fix/phase-0-memo.md và README §7.
Nếu thấy cần chạm gì khác → bảng 4 cột và dừng.

ĐẶC THÙ PHASE 0 — spike bằng ConsoleSample của Spotware, ZERO code production.
CÁCH CHẠY: TÔI chạy, BẠN phân tích. ConsoleSample là menu tương tác đọc stdin nên bạn KHÔNG tự chạy được.
- Bạn `git clone https://github.com/spotware/quickfixnsamples.net C:\tmp\ctrader-spike` (NGOÀI repo) và
  `dotnet restore` trong ConsoleSample/ — đó là lúc trả lời luôn câu "QuickFIXn.Core + FIX4.4 restore
  được không" (kết luận layering, trên Windows).
- Bạn soạn 2 template Config-dev.cfg (TRADE cổng 5212, QUOTE cổng 5211; SSL) theo bảng phase-0-spike.md
  với dòng Password=<TỰ ĐIỀN>. TÔI tự gõ mật khẩu vào file. Bạn KHÔNG BAO GIỜ cat/đọc file đó,
  KHÔNG hỏi mật khẩu trong chat, KHÔNG in nội dung cfg ra output.
- TÁI CẤU TRÚC 2026-09-16: Phase 0 CHỈ còn các câu KHÔNG đặt lệnh — 2, 3, 7, 8, 9, 10 (câu 2/3/8/9/10
  đã xong trên live 8220816, memo §5). Câu 4/1/5/6 (NewOrderSingle tiền thật) chuyển sang PHASE 7 BƯỚC A.
  Cổng sang Phase 1 là "GO-READ" (phase-0-spike.md § Cổng sang Phase 1), KHÔNG cần câu 1.
- Còn lại duy nhất câu 7: tôi chạy `x` → `g` → `q` trên Config-dev.LIVE-TRADE.cfg, dán output + 2 dòng
  `35=A` từ log/ (che 554). Bạn kiểm Logon thứ hai có `34=1`, `141=Y`, không `35=2`.
- Policy chặn bạn kết nối/gửi lệnh trên tài khoản thật → mọi lệnh ConsoleSample do TÔI gõ; bạn phân
  tích output đã che `554=` và KHÔNG chép lại nếu tôi che sót.
- Giữ C:\tmp\ctrader-spike + Config-dev.LIVE-*.cfg cho Phase 7; xoá log/, store/ sau câu 7.

VIỆC SONG SONG (làm trong lúc tôi chuẩn bị spike) — ĐIỀU TRA 9 TEST FAIL CÒN LẠI (memo §4.2, §4.3):
CHỈ BÁO CÁO, KHÔNG SỬA LOGIC, KHÔNG SỬA TEST. Với từng test so ngữ nghĩa test viết ra với
CloseSignalEngine / PortfolioCoordinator hiện tại; phân loại "test cũ chưa cập nhật" vs "regression
thật"; nêu commit nghi vấn (2ce1bb1, 4165a45, 626cefb). Ghi vào memo §4.

BƯỚC 4 — THỰC HIỆN
Làm đúng phần "Việc làm" của phase 0. Không hơn. Không refactor "tiện tay". Không dọn code
xung quanh. Mỗi file chạm vào phải nằm trong danh sách của phase hoặc được tôi duyệt ở bước 3.

DELIVERABLE: docs/plans/ctrader-fix/phase-0-memo.md hoàn chỉnh: §2 baseline Windows + danh sách; §4
phân loại 11/11 fail; §5 câu 2/3/7/8/9/10 × (kết quả | log raw đã che 554 | kết luận), câu 1/4/5/6 ghi
"→ Phase 7 Bước A"; kết luận layering; ghi nhận QuickFixNApp.ToAdmin tự gắn 553/554; §7 GO-READ.

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 0: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần demo cTrader / giờ thị trường mở / soak nhiều ngày mà chưa có → ghi "CHƯA KIỂM — cần
  {demo|giờ mở|soak N ngày}", KHÔNG đánh ✅. (Windows đã có sẵn — không dùng lý do này.)
- Báo cáo trung thực. Không nói "xong" khi còn mục ❌ hoặc CHƯA KIỂM.

BƯỚC 6 — BÀN GIAO
- KHÔNG commit. Đề xuất commit message theo CLAUDE.md §7 (format "<phase>: <summary>").
- Cập nhật bảng tiến độ ở docs/plans/ctrader-fix/README.md §7 (trạng thái phase 0).
- Liệt kê những gì "Cổng sang Phase 1" còn thiếu. Nếu §4 kết luận có regression thật trong 11 fail →
  nêu rõ đó là quyết định riêng của tôi phải có TRƯỚC Phase 1.

QUY TẮC BẤT BIẾN
- Không log, không echo, không đưa vào output: mật khẩu FIX, tag 554, chuỗi sans_json thô.
- Không đổi business logic. Nếu thấy cần → bảng 4 cột (vấn đề/giải pháp/ảnh hưởng/rủi ro) và
  chờ tôi duyệt.
- Không dùng subagent trừ khi tôi yêu cầu.
- Playwright MCP với cTrader Web CHỈ ĐỌC: không bấm New order/Close; không chép snapshot
  .playwright-mcp/ vào repo hay memo — chỉ chép số liệu.
```

---

## Phase 1 — Nhận diện platform ctrader

File plan: [phase-1-platform-enum.md](phase-1-platform-enum.md)

```text
Bạn đang thực hiện PHASE 1 của kế hoạch tích hợp cTrader FIX API cho sàn B.

ĐỌC TRƯỚC, THEO THỨ TỰ, KHÔNG BỎ QUA:
1. CLAUDE.md (toàn bộ — đặc biệt §0.1 Risk Assessment, §0.2 Không thay đổi logic, §2 Rule A–G)
2. docs/plans/ctrader-fix/README.md (bảng rủi ro R1–R11, quyết định thiết kế §4, Phụ lục A/B)
3. docs/plans/ctrader-fix/phase-0-spike.md (phase TRƯỚC — để rà soát)
4. docs/plans/ctrader-fix/phase-1-platform-enum.md (phase NÀY — để thực hiện)

BƯỚC 1 — RÀ SOÁT PHASE 0 (bắt buộc, có cổng dừng)
Kiểm tra bằng CODE, TEST và LOG THẬT, không kiểm tra bằng cách đọc lại tài liệu:
- Mở phần "Nghiệm thu" của phase 0. Với TỪNG mục checklist: tìm bằng chứng nó đã pass
  (test tồn tại và xanh / dòng log / hành vi quan sát được). Ghi ✅ kèm bằng chứng, hoặc ❌.
- Chạy: DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj
  So với baseline Windows ghi ở docs/plans/ctrader-fix/phase-0-memo.md §2 — so CẢ con số LẪN danh sách
  tên test fail. Tăng = vấn đề; một fail mới thay một fail cũ cũng = vấn đề.
- Đọc lại diff của phase 0 (git log / git diff) với con mắt của CLAUDE.md §0.2: có dòng nào
  đổi hành vi ngoài phạm vi phase đó không? Có chạm signal engine / coordinator / router /
  Rule A–G không?
- Đối chiếu phần "Rủi ro liên quan" của phase 0: mỗi R# được nhắc có thực sự được xử lý
  trong code không, hay chỉ được nhắc trong tài liệu?

Riêng Phase 0 (spike, không có code) — cổng là GO-READ (tái cấu trúc 2026-09-16), KHÔNG đòi câu 1:
kiểm phase-0-spike.md § "Cổng sang Phase 1" đều [x]; memo §5 có log raw (che 554) cho câu 2/3/7/7b/8/9/10,
và câu 1/4/5/6 ghi "→ Phase 7 Bước A"; baseline WINDOWS có con số + danh sách (§2.2b); §4 phân loại đủ
11/11, 0 regression — nếu có "regression thật" mà chưa có quyết định của tôi → DỪNG; layering có mặt;
C:\tmp\ctrader-spike\ConsoleSample\store\ không còn (kiểm bằng ls). Config-dev.cfg / Config-dev.LIVE-*.cfg
được GIỮ CÓ CHỦ ĐÍCH cho Phase 7 — KHÔNG coi là vấn đề. Việc chờ tác giả xác nhận 11 fail và CLAUDE.md:535
ghi baseline 19 là việc mở đã biết, KHÔNG chặn Phase 1 (gate so với memo).

Nếu phát hiện BẤT KỲ vấn đề nào, trình bày theo đúng bảng này rồi DỪNG, chờ tôi duyệt:

| # | Vấn đề (file:dòng, bằng chứng) | Giải pháp đề xuất | Ảnh hưởng (phạm vi, phase nào bị lùi) | Rủi ro (nếu sửa / nếu không sửa) |
|---|---|---|---|---|

Không sửa gì ở bước này. Không sang bước 2 khi bảng trên còn dòng chưa được tôi duyệt.
Nếu không có vấn đề: ghi rõ "Phase 0: không phát hiện vấn đề" kèm danh sách bằng chứng.

BƯỚC 2 — CHỐT TRƯỚC KHI CODE
Mở phần "Chốt trước khi code" của phase 1. Với từng câu:
- Đã có đáp án ghi "ĐÃ QUYẾT" → nhắc lại đáp án, làm theo.
- Chưa có → hỏi tôi bằng AskUserQuestion, KHÔNG tự giả định. Chờ đáp án rồi mới tiếp.

BƯỚC 3 — ĐÁNH GIÁ RỦI RO THEO CLAUDE.md §0.1
Trước khi viết dòng code nào, liệt kê: việc làm của phase này có ảnh hưởng logic giao dịch
không? luồng dữ liệu không? có side effect ngoài phạm vi không? Nếu có bất kỳ "có" nào →
trình bày và chờ tôi cho phép. Nếu phase yêu cầu chạm vào hành vi hiện có (kể cả một dòng),
đó là "có".

ĐẶC THÙ PHASE 1:
- Sửa CẢ 3 bản NormalizePlatform (ConfigService :25, :488; RuntimeConfigState :460) trong MỘT commit
  (R7). Thiếu một chỗ = platform_b âm thầm về mt5.
- B1: assert exchange == "A" && platform == CTrader → throw, ngay trong ValidatePlatformOrThrow.
- KHÔNG đụng hai lời gọi executors.First(...) cho Mt4/Mt5 ở ctor router.
- NullCTraderTradeExecutor trả Success=false, KHÔNG throw.

BƯỚC 4 — THỰC HIỆN
Làm đúng phần "Việc làm" của phase 1. Không hơn. Không refactor "tiện tay". Không dọn code
xung quanh. Mỗi file chạm vào phải nằm trong danh sách của phase hoặc được tôi duyệt ở bước 3.
Giữ nguyên chữ ký public đang có; thêm tham số thì phải có default.

TEST BẮT BUỘC: parity hai NormalizePlatform cùng tập {mt4, mt5, ctrader}; platform_a=ctrader bị reject;
ma trận 8 dòng ở phần Nghiệm thu. Smoke tại chỗ (máy này có 2 MT5): cả 4 tổ hợp MT-MT không hồi quy;
platform_b=ctrader → app không crash, chân B fail "cTrader chưa được kích hoạt", chân A rollback.

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 1: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần demo cTrader / giờ thị trường mở / soak nhiều ngày mà chưa có → ghi "CHƯA KIỂM — cần
  {demo|giờ mở|soak N ngày}", KHÔNG đánh ✅. (Windows đã có sẵn — không dùng lý do này.)
- Báo cáo trung thực. Không nói "xong" khi còn mục ❌ hoặc CHƯA KIỂM.

BƯỚC 6 — BÀN GIAO
- KHÔNG commit. Đề xuất commit message theo CLAUDE.md §7 (format "<phase>: <summary>").
- Cập nhật bảng tiến độ ở docs/plans/ctrader-fix/README.md §7 (trạng thái phase 1).
- Liệt kê những gì "Cổng sang Phase 2" còn thiếu.

QUY TẮC BẤT BIẾN
- Không log, không echo, không đưa vào output: mật khẩu FIX, tag 554, chuỗi sans_json thô.
- Không đổi business logic. Nếu thấy cần → bảng 4 cột (vấn đề/giải pháp/ảnh hưởng/rủi ro) và
  chờ tôi duyệt.
- Không dùng subagent trừ khi tôi yêu cầu.
- Playwright MCP với cTrader Web CHỈ ĐỌC: không bấm New order/Close; không chép snapshot
  .playwright-mcp/ vào repo hay memo — chỉ chép số liệu.
```

---

## Phase 2 — Cấu hình UI + persistence

File plan: [phase-2-config.md](phase-2-config.md)

```text
Bạn đang thực hiện PHASE 2 của kế hoạch tích hợp cTrader FIX API cho sàn B.

ĐỌC TRƯỚC, THEO THỨ TỰ, KHÔNG BỎ QUA:
1. CLAUDE.md (toàn bộ — đặc biệt §0.1 Risk Assessment, §0.2 Không thay đổi logic, §2 Rule A–G)
2. docs/plans/ctrader-fix/README.md (bảng rủi ro R1–R11, quyết định thiết kế §4, Phụ lục A/B)
3. docs/plans/ctrader-fix/phase-1-platform-enum.md (phase TRƯỚC — để rà soát)
4. docs/plans/ctrader-fix/phase-2-config.md (phase NÀY — để thực hiện)

BƯỚC 1 — RÀ SOÁT PHASE 1 (bắt buộc, có cổng dừng)
Kiểm tra bằng CODE, TEST và LOG THẬT, không kiểm tra bằng cách đọc lại tài liệu:
- Mở phần "Nghiệm thu" của phase 1. Với TỪNG mục checklist: tìm bằng chứng nó đã pass
  (test tồn tại và xanh / dòng log / hành vi quan sát được). Ghi ✅ kèm bằng chứng, hoặc ❌.
- Chạy: DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj
  So với baseline Windows ghi ở docs/plans/ctrader-fix/phase-0-memo.md §2 — so CẢ con số LẪN danh sách
  tên test fail. Tăng = vấn đề; một fail mới thay một fail cũ cũng = vấn đề.
- Đọc lại diff của phase 1 (git log / git diff) với con mắt của CLAUDE.md §0.2: có dòng nào
  đổi hành vi ngoài phạm vi phase đó không? Có chạm signal engine / coordinator / router /
  Rule A–G không?
- Đối chiếu phần "Rủi ro liên quan" của phase 1: mỗi R# được nhắc có thực sự được xử lý
  trong code không, hay chỉ được nhắc trong tài liệu?

Riêng Phase 1: grep chắc cả 5 chỗ normalize (ConfigService ×2, RuntimeConfigState, SupabaseConfigRepository,
ConfigViewModel) đều nhận "ctrader"; NullCTraderTradeExecutor đăng ký ở App.xaml.cs; ValidatePlatformOrThrow có
assert chân A; PlatformNormalizationTests (22) xanh; smoke app Phase 1 đã pass (phase-1-platform-enum.md).

Nếu phát hiện BẤT KỲ vấn đề nào, trình bày theo đúng bảng này rồi DỪNG, chờ tôi duyệt:

| # | Vấn đề (file:dòng, bằng chứng) | Giải pháp đề xuất | Ảnh hưởng (phạm vi, phase nào bị lùi) | Rủi ro (nếu sửa / nếu không sửa) |
|---|---|---|---|---|

Không sửa gì ở bước này. Không sang bước 2 khi bảng trên còn dòng chưa được tôi duyệt.
Nếu không có vấn đề: ghi rõ "Phase 1: không phát hiện vấn đề" kèm danh sách bằng chứng.

BƯỚC 2 — CHỐT TRƯỚC KHI CODE
Mở phần "Chốt trước khi code" của phase 2. Với từng câu:
- Đã có đáp án ghi "ĐÃ QUYẾT" → nhắc lại đáp án, làm theo.
- Chưa có → hỏi tôi bằng AskUserQuestion, KHÔNG tự giả định. Chờ đáp án rồi mới tiếp.

BƯỚC 3 — ĐÁNH GIÁ RỦI RO THEO CLAUDE.md §0.1
Trước khi viết dòng code nào, liệt kê: việc làm của phase này có ảnh hưởng logic giao dịch
không? luồng dữ liệu không? có side effect ngoài phạm vi không? Nếu có bất kỳ "có" nào →
trình bày và chờ tôi cho phép. Nếu phase yêu cầu chạm vào hành vi hiện có (kể cả một dòng),
đó là "có".

ĐẶC THÙ PHASE 2:
- Đường LƯU/ĐỌC có 6 điểm phải chạm (bảng "6 điểm" trong phase-2): CTraderFixConfig, SansJsonHelper,
  ConfigService.SaveByMachineHostNameAsync (:171, tham số mới CÓ default), RuntimeConfigState
  (method riêng UpdateCTraderFix — KHÔNG nhét vào Update(...)), ConfigViewModel (:393/:449/:518/:534),
  ConfigWindow.xaml. Thiếu một là giá trị bốc hơi sau restart.
- HWND B là BLOCKER, không phải UX: DashboardViewModel.cs:3807 (HasManualTradeHwndConfig) và
  RunHwndHealthCheck → SkipIfHwndInvalid. Cả hai phải nhận cờ requiresExchangeBHwnd từ platform_b.
- Ba quy tắc bảo vệ mật khẩu: SansJsonHelper.Redact cho MỌI chỗ log sans_json; ToString() che;
  không nối CurrentCTraderFixConfig vào RuntimeSummary.
- ĐÃ QUYẾT câu 4: từ chối Save khi (platform_b đổi) && (cũ hoặc mới là ctrader) && (còn slot).
  Đổi mt4↔mt5 giữ nguyên hành vi. Kiểm ở ConfigViewModel, KHÔNG sửa coordinator.
- sans_json cũ (không có ctraderFix) phải parse được; BuildSans không ghi khối khi Empty.
- Nhãn cảnh báo "Tài khoản LIVE" cạnh ô SenderCompID khi giá trị không bắt đầu bằng `demo.` — CHỈ cảnh
  báo, KHÔNG tham gia CanSave (cùng login FxPro có tài khoản live 8220816/8225904).
- Tooltip ô Password: "= mật khẩu đăng nhập tài khoản cTrader" (panel FxPro ghi *a/c password*).
- Smoke G1/G2 (HWND B trống không khoá app) làm tại chỗ với 2 MT5 — không ghi CHƯA KIỂM.
- ĐÃ QUYẾT câu 2: UI 2 MODE cho khối Sàn B. MT4/MT5 → bọc NGUYÊN các control Map Name B/CHART HWND B/TRADE HWND B
  hiện có vào panel ẩn khi cTrader, KHÔNG sửa bên trong. cTrader → ẩn panel đó, hiện form cTrader. Khối Sàn A
  không đổi một dòng. Mode suy từ PlatformB, không lưu cờ riêng. Không tách ManualHwndColumns (profile A+B, Rule G).
  Mốc UI mode MT5 đã chụp 2026-09-17 và mô tả trong phase-2-config.md "Mốc UI trước Phase 2" — nghiệm thu so
  với bảng đó; khác biệt duy nhất được phép là thêm radio "cTrader".
- ĐÃ QUYẾT câu 3: CTraderFixConfig.ChannelMapName = "CTRADER_B"; RuntimeConfigState.CurrentMapName2 trả hằng này
  khi platform_b == ctrader; mapNames[1] đã lưu KHÔNG bị ghi đè; form hiện read-only "Kênh B: CTRADER_B".
  CanSaveCommand mode cTrader không đòi MapName2.
- ĐÃ QUYẾT: chuyển mode KHÔNG xoá dữ liệu mode đang ẩn; Save ghi cả mapNames[1] lẫn khối ctraderFix.
- Nguyên tắc gom tiền thật: không dispatch lệnh ở phase này (mục "Lệnh chân A dispatch" → Phase 7 Bước B).

BƯỚC 4 — THỰC HIỆN
Làm đúng phần "Việc làm" của phase 2. Không hơn. Không refactor "tiện tay". Không dọn code
xung quanh. Mỗi file chạm vào phải nằm trong danh sách của phase hoặc được tôi duyệt ở bước 3.
Giữ nguyên chữ ký public đang có; thêm tham số thì phải có default.

Form cTrader (mode cTrader của khối Sàn B) soi gương panel FIX API của cTrader (khối QUOTE, khối TRADE,
phần chung) theo bảng ánh xạ và mục "UI 2 mode cho Sàn B" trong phase-2. PasswordBox: không bind hai chiều; load không đổ mật khẩu ngược vào ô; Save với
ô trống = giữ mật khẩu cũ.

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 2: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần demo cTrader / giờ thị trường mở / soak nhiều ngày mà chưa có → ghi "CHƯA KIỂM — cần
  {demo|giờ mở|soak N ngày}", KHÔNG đánh ✅. (Windows đã có sẵn — không dùng lý do này.)
- Báo cáo trung thực. Không nói "xong" khi còn mục ❌ hoặc CHƯA KIỂM.

BƯỚC 6 — BÀN GIAO
- KHÔNG commit. Đề xuất commit message theo CLAUDE.md §7 (format "<phase>: <summary>").
- Cập nhật bảng tiến độ ở docs/plans/ctrader-fix/README.md §7 (trạng thái phase 2).
- Liệt kê những gì "Cổng sang Phase 3" còn thiếu.

QUY TẮC BẤT BIẾN
- Không log, không echo, không đưa vào output: mật khẩu FIX, tag 554, chuỗi sans_json thô.
- Không đổi business logic. Nếu thấy cần → bảng 4 cột (vấn đề/giải pháp/ảnh hưởng/rủi ro) và
  chờ tôi duyệt.
- Không dùng subagent trừ khi tôi yêu cầu.
- Playwright MCP với cTrader Web CHỈ ĐỌC: không bấm New order/Close; không chép snapshot
  .playwright-mcp/ vào repo hay memo — chỉ chép số liệu.
```

---

## Phase 3 — Lõi FIX offline

File plan: [phase-3-fix-core-offline.md](phase-3-fix-core-offline.md)

```text
Bạn đang thực hiện PHASE 3 của kế hoạch tích hợp cTrader FIX API cho sàn B.

ĐỌC TRƯỚC, THEO THỨ TỰ, KHÔNG BỎ QUA:
1. CLAUDE.md (toàn bộ — đặc biệt §0.1 Risk Assessment, §0.2 Không thay đổi logic, §2 Rule A–G)
2. docs/plans/ctrader-fix/README.md (bảng rủi ro R1–R11, quyết định thiết kế §4, Phụ lục A/B)
3. docs/plans/ctrader-fix/phase-2-config.md (phase TRƯỚC — để rà soát)
4. docs/plans/ctrader-fix/phase-3-fix-core-offline.md (phase NÀY — để thực hiện)

BƯỚC 1 — RÀ SOÁT PHASE 2 (bắt buộc, có cổng dừng)
Kiểm tra bằng CODE, TEST và LOG THẬT, không kiểm tra bằng cách đọc lại tài liệu:
- Mở phần "Nghiệm thu" của phase 2. Với TỪNG mục checklist: tìm bằng chứng nó đã pass
  (test tồn tại và xanh / dòng log / hành vi quan sát được). Ghi ✅ kèm bằng chứng, hoặc ❌.
- Chạy: DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj
  So với baseline Windows ghi ở docs/plans/ctrader-fix/phase-0-memo.md §2 — so CẢ con số LẪN danh sách
  tên test fail. Tăng = vấn đề; một fail mới thay một fail cũ cũng = vấn đề.
- Đọc lại diff của phase 2 (git log / git diff) với con mắt của CLAUDE.md §0.2: có dòng nào
  đổi hành vi ngoài phạm vi phase đó không? Có chạm signal engine / coordinator / router /
  Rule A–G không?
- Đối chiếu phần "Rủi ro liên quan" của phase 2: mỗi R# được nhắc có thực sự được xử lý
  trong code không, hay chỉ được nhắc trong tài liệu?

Riêng Phase 2: round-trip một sans_json THẬT (che mật khẩu trong output); grep toàn repo không còn
chỗ nào log sans_json thô; IsCompleteFor được gọi đúng ở DashboardViewModel.cs:3807; ba ca test
"chặn đổi platform khi còn slot" xanh.

Nếu phát hiện BẤT KỲ vấn đề nào, trình bày theo đúng bảng này rồi DỪNG, chờ tôi duyệt:

| # | Vấn đề (file:dòng, bằng chứng) | Giải pháp đề xuất | Ảnh hưởng (phạm vi, phase nào bị lùi) | Rủi ro (nếu sửa / nếu không sửa) |
|---|---|---|---|---|

Không sửa gì ở bước này. Không sang bước 2 khi bảng trên còn dòng chưa được tôi duyệt.
Nếu không có vấn đề: ghi rõ "Phase 2: không phát hiện vấn đề" kèm danh sách bằng chứng.

BƯỚC 2 — CHỐT TRƯỚC KHI CODE
Mở phần "Chốt trước khi code" của phase 3. Với từng câu:
- Đã có đáp án ghi "ĐÃ QUYẾT" → nhắc lại đáp án, làm theo.
- Chưa có → hỏi tôi bằng AskUserQuestion, KHÔNG tự giả định. Chờ đáp án rồi mới tiếp.

BƯỚC 3 — ĐÁNH GIÁ RỦI RO THEO CLAUDE.md §0.1
Trước khi viết dòng code nào, liệt kê: việc làm của phase này có ảnh hưởng logic giao dịch
không? luồng dữ liệu không? có side effect ngoài phạm vi không? Nếu có bất kỳ "có" nào →
trình bày và chờ tôi cho phép. Nếu phase yêu cầu chạm vào hành vi hiện có (kể cả một dòng),
đó là "có".

ĐẶC THÙ PHASE 3 — dead code, KHÔNG wire DI:
- Layering theo kết luận Phase 0: QuickFIXn đã restore trên WINDOWS (memo). Nếu còn dev trên macOS thì
  kiểm thêm darwin, nhưng không chặn phase. Mặc định đặt ở Infrastructure/CTrader.
- Đọc trước Common/QuickFixNApp.cs và ConsoleSample/Program.cs của SDK Spotware (README Phụ lục B) —
  bản clone đã có ở C:\tmp\ctrader-spike nếu Phase 0 chưa xoá; không thì clone lại ngoài repo.
- Pitfall #0: QuickFIX/n KHÔNG tự gắn 553/554 vào Logon — transport phải set trong ToAdmin.
- Tầng: Application ZERO using QuickFix; Infrastructure/CTrader được dùng typed message
  (QuickFix.FIX44.*); chỉ QuickFixCTraderTransport chạm SocketInitiator/Session/IApplication.
- HAI SocketInitiator (QUOTE, TRADE) — topology FixClient.cs của Spotware.
- Tag 1007/1008 đọc bằng số thô (lớp Tags gọi nhầm là SideReasonCd/SideTrdSubTyp).
- PositionReport 728=2 → PositionsSynced=true với danh sách rỗng.
- CTraderPositionCache.Version là content-version, chỉ tăng khi tập record đổi (R3).
- CTraderTicketCodec namespace 0x4000_0000_0000_0000 (R4).

BƯỚC 4 — THỰC HIỆN
Làm đúng phần "Việc làm" của phase 3. Không hơn. Không refactor "tiện tay". Không dọn code
xung quanh. Mỗi file chạm vào phải nằm trong danh sách của phase hoặc được tôi duyệt ở bước 3.
Giữ nguyên chữ ký public đang có; thêm tham số thì phải có default.

TEST: CTraderSessionHealth đủ 16 tổ hợp cờ; quote book (delete-then-new, out-of-order, rỗng);
position cache (fill trước pending, ER trùng, 150=0 rồi 150=F, 728=2, 727 batch); codec round-trip;
ToAdmin gắn 553/554 và che 554 trong log. Gate: grep "using QuickFix" trong Application = 0.

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 3: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần demo cTrader / giờ thị trường mở / soak nhiều ngày mà chưa có → ghi "CHƯA KIỂM — cần
  {demo|giờ mở|soak N ngày}", KHÔNG đánh ✅. (Windows đã có sẵn — không dùng lý do này.)
- Báo cáo trung thực. Không nói "xong" khi còn mục ❌ hoặc CHƯA KIỂM.

BƯỚC 6 — BÀN GIAO
- KHÔNG commit. Đề xuất commit message theo CLAUDE.md §7 (format "<phase>: <summary>").
- Cập nhật bảng tiến độ ở docs/plans/ctrader-fix/README.md §7 (trạng thái phase 3).
- Liệt kê những gì "Cổng sang Phase 4" còn thiếu.

QUY TẮC BẤT BIẾN
- Không log, không echo, không đưa vào output: mật khẩu FIX, tag 554, chuỗi sans_json thô.
- Không đổi business logic. Nếu thấy cần → bảng 4 cột (vấn đề/giải pháp/ảnh hưởng/rủi ro) và
  chờ tôi duyệt.
- Không dùng subagent trừ khi tôi yêu cầu.
- Playwright MCP với cTrader Web CHỈ ĐỌC: không bấm New order/Close; không chép snapshot
  .playwright-mcp/ vào repo hay memo — chỉ chép số liệu.
```

---

## Phase 4 — Luồng GIÁ live, read-only

File plan: [phase-4-quote-feed.md](phase-4-quote-feed.md)

```text
Bạn đang thực hiện PHASE 4 của kế hoạch tích hợp cTrader FIX API cho sàn B.

ĐỌC TRƯỚC, THEO THỨ TỰ, KHÔNG BỎ QUA:
1. CLAUDE.md (toàn bộ — đặc biệt §0.1 Risk Assessment, §0.2 Không thay đổi logic, §2 Rule A–G)
2. docs/plans/ctrader-fix/README.md (bảng rủi ro R1–R11, quyết định thiết kế §4, Phụ lục A/B)
3. docs/plans/ctrader-fix/phase-3-fix-core-offline.md (phase TRƯỚC — để rà soát)
4. docs/plans/ctrader-fix/phase-4-quote-feed.md (phase NÀY — để thực hiện)

BƯỚC 1 — RÀ SOÁT PHASE 3 (bắt buộc, có cổng dừng)
Kiểm tra bằng CODE, TEST và LOG THẬT, không kiểm tra bằng cách đọc lại tài liệu:
- Mở phần "Nghiệm thu" của phase 3. Với TỪNG mục checklist: tìm bằng chứng nó đã pass
  (test tồn tại và xanh / dòng log / hành vi quan sát được). Ghi ✅ kèm bằng chứng, hoặc ❌.
- Chạy: DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj
  So với baseline Windows ghi ở docs/plans/ctrader-fix/phase-0-memo.md §2 — so CẢ con số LẪN danh sách
  tên test fail. Tăng = vấn đề; một fail mới thay một fail cũ cũng = vấn đề.
- Đọc lại diff của phase 3 (git log / git diff) với con mắt của CLAUDE.md §0.2: có dòng nào
  đổi hành vi ngoài phạm vi phase đó không? Có chạm signal engine / coordinator / router /
  Rule A–G không?
- Đối chiếu phần "Rủi ro liên quan" của phase 3: mỗi R# được nhắc có thực sự được xử lý
  trong code không, hay chỉ được nhắc trong tài liệu?

Riêng Phase 3: grep "using QuickFix" trong TradeDesktop.Application = 0; grep
SocketInitiator|SessionSettings|IApplication chỉ xuất hiện trong QuickFixCTraderTransport.cs; test
CTraderSessionHealth đủ 16 tổ hợp; test ToAdmin có; không có đăng ký DI nào cho các class CTrader.

Nếu phát hiện BẤT KỲ vấn đề nào, trình bày theo đúng bảng này rồi DỪNG, chờ tôi duyệt:

| # | Vấn đề (file:dòng, bằng chứng) | Giải pháp đề xuất | Ảnh hưởng (phạm vi, phase nào bị lùi) | Rủi ro (nếu sửa / nếu không sửa) |
|---|---|---|---|---|

Không sửa gì ở bước này. Không sang bước 2 khi bảng trên còn dòng chưa được tôi duyệt.
Nếu không có vấn đề: ghi rõ "Phase 3: không phát hiện vấn đề" kèm danh sách bằng chứng.

BƯỚC 2 — CHỐT TRƯỚC KHI CODE
Mở phần "Chốt trước khi code" của phase 4. Với từng câu:
- Đã có đáp án ghi "ĐÃ QUYẾT" → nhắc lại đáp án, làm theo.
- Chưa có → hỏi tôi bằng AskUserQuestion, KHÔNG tự giả định. Chờ đáp án rồi mới tiếp.

BƯỚC 3 — ĐÁNH GIÁ RỦI RO THEO CLAUDE.md §0.1
Trước khi viết dòng code nào, liệt kê: việc làm của phase này có ảnh hưởng logic giao dịch
không? luồng dữ liệu không? có side effect ngoài phạm vi không? Nếu có bất kỳ "có" nào →
trình bày và chờ tôi cho phép. Nếu phase yêu cầu chạm vào hành vi hiện có (kể cả một dòng),
đó là "có".

ĐẶC THÙ PHASE 4 — PHASE LIVE ĐẦU TIÊN, chỉ QUOTE, không đặt lệnh:
- G3: nguồn giá resolve lại MỖI TICK trong PollLoopAsync (theo platform_a/platform_b), đúng khuôn
  RefreshMapReaders. Chọn một lần lúc Start là SAI.
- G4: platform_b != ctrader → StartAsync là no-op; đổi khỏi ctrader → logout + giải phóng.
- MmfExchangeQuoteSource phải là code cũ DI CHUYỂN NGUYÊN VĂN — diff SharedMemoryMarketDataReader
  phải chứng minh không sửa một dòng logic. Vòng 50 ms và SnapshotReceived vô điều kiện không đổi.
- Kênh SecurityList theo kết quả Phase 0 câu 10; nếu QUOTE không trả lời → mở TRADE initiator CHỈ để
  gửi SecurityList, log rõ.
- Mất session → XOÁ book (R9). Lệch digits → fail closed (R6).
- FileLogPath của QuickFIX/n KHÔNG bật.
BƯỚC 3 bắt buộc trình bày bảng rủi ro và chờ tôi duyệt trước khi chạm SharedMemoryMarketDataReader.

BƯỚC 4 — THỰC HIỆN
Làm đúng phần "Việc làm" của phase 4. Không hơn. Không refactor "tiện tay". Không dọn code
xung quanh. Mỗi file chạm vào phải nằm trong danh sách của phase hoặc được tôi duyệt ở bước 3.
Giữ nguyên chữ ký public đang có; thêm tham số thì phải có default.

Nghiệm thu chạy tại chỗ (Windows + 2 MT5 + demo cTrader đều có). Chỉ ghi "CHƯA KIỂM" cho mục soak
nhiều ngày chưa đủ thời gian. Đặc biệt ghi lại tần suất skip LATENCY chân B trong soak — đó là dữ liệu
quyết R8 ở Phase 8.
Đối chiếu bằng cTrader Web (Playwright CHỈ ĐỌC): Bid/Ask XAUUSD trên web và giá B trong app cùng độ lớn,
cùng 2 chữ số, spread cùng cỡ — cross-check R6, KHÔNG so từng tick. Soak: quan sát riêng giờ nghỉ hằng
ngày 03:59:45 → 05:00 (UTC+7) ngay ngày đầu — QUOTE có logout không, book có bị xoá, IsConnected B
chuyển thế nào.

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 4: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần demo cTrader / giờ thị trường mở / soak nhiều ngày mà chưa có → ghi "CHƯA KIỂM — cần
  {demo|giờ mở|soak N ngày}", KHÔNG đánh ✅. (Windows đã có sẵn — không dùng lý do này.)
- Báo cáo trung thực. Không nói "xong" khi còn mục ❌ hoặc CHƯA KIỂM.

BƯỚC 6 — BÀN GIAO
- KHÔNG commit. Đề xuất commit message theo CLAUDE.md §7 (format "<phase>: <summary>").
- Cập nhật bảng tiến độ ở docs/plans/ctrader-fix/README.md §7 (trạng thái phase 4).
- Liệt kê những gì "Cổng sang Phase 5" còn thiếu.

QUY TẮC BẤT BIẾN
- Không log, không echo, không đưa vào output: mật khẩu FIX, tag 554, chuỗi sans_json thô.
- Không đổi business logic. Nếu thấy cần → bảng 4 cột (vấn đề/giải pháp/ảnh hưởng/rủi ro) và
  chờ tôi duyệt.
- Không dùng subagent trừ khi tôi yêu cầu.
- Playwright MCP với cTrader Web CHỈ ĐỌC: không bấm New order/Close; không chép snapshot
  .playwright-mcp/ vào repo hay memo — chỉ chép số liệu.
```

---

## Phase 5 — Luồng LỆNH ĐANG MỞ live, read-only

File plan: [phase-5-open-positions.md](phase-5-open-positions.md)

```text
Bạn đang thực hiện PHASE 5 của kế hoạch tích hợp cTrader FIX API cho sàn B.

ĐỌC TRƯỚC, THEO THỨ TỰ, KHÔNG BỎ QUA:
1. CLAUDE.md (toàn bộ — đặc biệt §0.1 Risk Assessment, §0.2 Không thay đổi logic, §2 Rule A–G)
2. docs/plans/ctrader-fix/README.md (bảng rủi ro R1–R11, quyết định thiết kế §4, Phụ lục A/B)
3. docs/plans/ctrader-fix/phase-4-quote-feed.md (phase TRƯỚC — để rà soát)
4. docs/plans/ctrader-fix/phase-5-open-positions.md (phase NÀY — để thực hiện)

BƯỚC 1 — RÀ SOÁT PHASE 4 (bắt buộc, có cổng dừng)
Kiểm tra bằng CODE, TEST và LOG THẬT, không kiểm tra bằng cách đọc lại tài liệu:
- Mở phần "Nghiệm thu" của phase 4. Với TỪNG mục checklist: tìm bằng chứng nó đã pass
  (test tồn tại và xanh / dòng log / hành vi quan sát được). Ghi ✅ kèm bằng chứng, hoặc ❌.
- Chạy: DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj
  So với baseline Windows ghi ở docs/plans/ctrader-fix/phase-0-memo.md §2 — so CẢ con số LẪN danh sách
  tên test fail. Tăng = vấn đề; một fail mới thay một fail cũ cũng = vấn đề.
- Đọc lại diff của phase 4 (git log / git diff) với con mắt của CLAUDE.md §0.2: có dòng nào
  đổi hành vi ngoài phạm vi phase đó không? Có chạm signal engine / coordinator / router /
  Rule A–G không?
- Đối chiếu phần "Rủi ro liên quan" của phase 4: mỗi R# được nhắc có thực sự được xử lý
  trong code không, hay chỉ được nhắc trong tài liệu?

Riêng Phase 4: đòi bằng chứng soak (log nhiều ngày, qua cuối tuần); số liệu skip LATENCY chân B; diff
SharedMemoryMarketDataReader xác nhận đường MMF không đổi; ma trận 6 ô có kết quả; kill mạng →
IsConnected=false < 1 s có log.

Nếu phát hiện BẤT KỲ vấn đề nào, trình bày theo đúng bảng này rồi DỪNG, chờ tôi duyệt:

| # | Vấn đề (file:dòng, bằng chứng) | Giải pháp đề xuất | Ảnh hưởng (phạm vi, phase nào bị lùi) | Rủi ro (nếu sửa / nếu không sửa) |
|---|---|---|---|---|

Không sửa gì ở bước này. Không sang bước 2 khi bảng trên còn dòng chưa được tôi duyệt.
Nếu không có vấn đề: ghi rõ "Phase 4: không phát hiện vấn đề" kèm danh sách bằng chứng.

BƯỚC 2 — CHỐT TRƯỚC KHI CODE
Mở phần "Chốt trước khi code" của phase 5. Với từng câu:
- Đã có đáp án ghi "ĐÃ QUYẾT" → nhắc lại đáp án, làm theo.
- Chưa có → hỏi tôi bằng AskUserQuestion, KHÔNG tự giả định. Chờ đáp án rồi mới tiếp.

BƯỚC 3 — ĐÁNH GIÁ RỦI RO THEO CLAUDE.md §0.1
Trước khi viết dòng code nào, liệt kê: việc làm của phase này có ảnh hưởng logic giao dịch
không? luồng dữ liệu không? có side effect ngoài phạm vi không? Nếu có bất kỳ "có" nào →
trình bày và chờ tôi cho phép. Nếu phase yêu cầu chạm vào hành vi hiện có (kể cả một dòng),
đó là "có".

ĐẶC THÙ PHASE 5 — R2 là invariant cứng nhất của cả dự án:
- CTraderAwareTradesReader trả MapNotFound cho tới TradeLoggedOn && SymbolResolved && PositionsSynced,
  và quay lại MapNotFound NGAY khi đứt. Trả IsMapAvailable=true, Count=0 khi chưa sync = đóng nhầm
  chân A đang sống.
- 728=2 vẫn là synced. Timestamp = content-version (R3). Ticket = Encode(positionId) (R4).
- OpenEaTimeLocal = Environment.TickCount64 lúc parse, không phải 0. Connected không bao giờ -1.
- Không sửa GetLivePairTradeState, TryDetectAndHandleExternalPartialClose, watchdog — chúng phải chạy
  nguyên xi qua decorator.

BƯỚC 4 — THỰC HIỆN
Làm đúng phần "Việc làm" của phase 5. Không hơn. Không refactor "tiện tay". Không dọn code
xung quanh. Mỗi file chạm vào phải nằm trong danh sách của phase hoặc được tôi duyệt ở bước 3.
Giữ nguyên chữ ký public đang có; thêm tham số thì phải có default.

Rollback = revert đúng một dòng đăng ký DI. Nghiệm thu quan trọng nhất: restart app khi đang có
position B → GetLivePairTradeState = MapUnavailableOrParseError, KHÔNG OnlyAOpen, log không có
external-partial-close.
TÁI CẤU TRÚC 2026-09-16: nghiệm thu phase này CHỈ Lớp 1 (tài khoản live 8220816 TRỐNG, không chạm
tiền): 728=2 path, cửa sổ chưa sync = MapUnavailableOrParseError, kill socket TRADE, R3 với danh sách
rỗng, reconciliation 60 s. Lớp 2 (cần position thật: R2 khi restart có position, R4 so Position ID web,
R3 khi mở position) đã chuyển sang PHASE 7 BƯỚC B — KHÔNG mở position ở phase này. Ghi "→ Phase 7 B"
cho các mục Lớp 2, không đánh CHƯA KIỂM.

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 5: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần demo cTrader / giờ thị trường mở / soak nhiều ngày mà chưa có → ghi "CHƯA KIỂM — cần
  {demo|giờ mở|soak N ngày}", KHÔNG đánh ✅. (Windows đã có sẵn — không dùng lý do này.)
- Báo cáo trung thực. Không nói "xong" khi còn mục ❌ hoặc CHƯA KIỂM.

BƯỚC 6 — BÀN GIAO
- KHÔNG commit. Đề xuất commit message theo CLAUDE.md §7 (format "<phase>: <summary>").
- Cập nhật bảng tiến độ ở docs/plans/ctrader-fix/README.md §7 (trạng thái phase 5).
- Liệt kê những gì "Cổng sang Phase 6" còn thiếu.

QUY TẮC BẤT BIẾN
- Không log, không echo, không đưa vào output: mật khẩu FIX, tag 554, chuỗi sans_json thô.
- Không đổi business logic. Nếu thấy cần → bảng 4 cột (vấn đề/giải pháp/ảnh hưởng/rủi ro) và
  chờ tôi duyệt.
- Không dùng subagent trừ khi tôi yêu cầu.
- Playwright MCP với cTrader Web CHỈ ĐỌC: không bấm New order/Close; không chép snapshot
  .playwright-mcp/ vào repo hay memo — chỉ chép số liệu.
```

---

## Phase 6 — Luồng LỊCH SỬ live, read-only

File plan: [phase-6-history.md](phase-6-history.md)

```text
Bạn đang thực hiện PHASE 6 của kế hoạch tích hợp cTrader FIX API cho sàn B.

ĐỌC TRƯỚC, THEO THỨ TỰ, KHÔNG BỎ QUA:
1. CLAUDE.md (toàn bộ — đặc biệt §0.1 Risk Assessment, §0.2 Không thay đổi logic, §2 Rule A–G)
2. docs/plans/ctrader-fix/README.md (bảng rủi ro R1–R11, quyết định thiết kế §4, Phụ lục A/B)
3. docs/plans/ctrader-fix/phase-5-open-positions.md (phase TRƯỚC — để rà soát)
4. docs/plans/ctrader-fix/phase-6-history.md (phase NÀY — để thực hiện)

BƯỚC 1 — RÀ SOÁT PHASE 5 (bắt buộc, có cổng dừng)
Kiểm tra bằng CODE, TEST và LOG THẬT, không kiểm tra bằng cách đọc lại tài liệu:
- Mở phần "Nghiệm thu" của phase 5. Với TỪNG mục checklist: tìm bằng chứng nó đã pass
  (test tồn tại và xanh / dòng log / hành vi quan sát được). Ghi ✅ kèm bằng chứng, hoặc ❌.
- Chạy: DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj
  So với baseline Windows ghi ở docs/plans/ctrader-fix/phase-0-memo.md §2 — so CẢ con số LẪN danh sách
  tên test fail. Tăng = vấn đề; một fail mới thay một fail cũ cũng = vấn đề.
- Đọc lại diff của phase 5 (git log / git diff) với con mắt của CLAUDE.md §0.2: có dòng nào
  đổi hành vi ngoài phạm vi phase đó không? Có chạm signal engine / coordinator / router /
  Rule A–G không?
- Đối chiếu phần "Rủi ro liên quan" của phase 5: mỗi R# được nhắc có thực sự được xử lý
  trong code không, hay chỉ được nhắc trong tài liệu?

Riêng Phase 5: test R2 trực diện có log (restart khi có position → MapUnavailableOrParseError); R3: để
yên 5 phút không rebuild, nút Đóng per-pair ăn một lần bấm; R4: ticket ngoài dải MT, decode đúng; soak
≥ 2 ngày.

Nếu phát hiện BẤT KỲ vấn đề nào, trình bày theo đúng bảng này rồi DỪNG, chờ tôi duyệt:

| # | Vấn đề (file:dòng, bằng chứng) | Giải pháp đề xuất | Ảnh hưởng (phạm vi, phase nào bị lùi) | Rủi ro (nếu sửa / nếu không sửa) |
|---|---|---|---|---|

Không sửa gì ở bước này. Không sang bước 2 khi bảng trên còn dòng chưa được tôi duyệt.
Nếu không có vấn đề: ghi rõ "Phase 5: không phát hiện vấn đề" kèm danh sách bằng chứng.

BƯỚC 2 — CHỐT TRƯỚC KHI CODE
Mở phần "Chốt trước khi code" của phase 6. Với từng câu:
- Đã có đáp án ghi "ĐÃ QUYẾT" → nhắc lại đáp án, làm theo.
- Chưa có → hỏi tôi bằng AskUserQuestion, KHÔNG tự giả định. Chờ đáp án rồi mới tiếp.

BƯỚC 3 — ĐÁNH GIÁ RỦI RO THEO CLAUDE.md §0.1
Trước khi viết dòng code nào, liệt kê: việc làm của phase này có ảnh hưởng logic giao dịch
không? luồng dữ liệu không? có side effect ngoài phạm vi không? Nếu có bất kỳ "có" nào →
trình bày và chờ tôi cho phép. Nếu phase yêu cầu chạm vào hành vi hiện có (kể cả một dòng),
đó là "có".

ĐẶC THÙ PHASE 6:
- History decorator cùng khuôn Phase 5; content-version RIÊNG cho history (ShouldApplyHistoryResult
  theo dõi độc lập).
- Nhận diện order đóng = "position biến mất khỏi cache" chéo kiểm AP — KHÔNG suy theo chiều lệnh.
- CloseEaTimeLocal stamp thật. Profit/Commission là số tính lại — ghi rõ, không trình bày như số broker.
- Chỉ ghi history khi position đóng hẳn (LeavesQty=0), trừ khi Phase 0 câu 5 cho thấy phải khác.
- TÁI CẤU TRÚC 2026-09-16: nghiệm thu CHỈ Lớp 1 (tài khoản trống): history map MapNotFound → available
  Count=0, version history độc lập trades, unit test projector với message đóng hộp. Lớp 2 (đóng tay →
  record history, closeExecutionMs, chuỗi 3 lần) → PHASE 7 BƯỚC B. KHÔNG mở/đóng position ở phase này.

BƯỚC 4 — THỰC HIỆN
Làm đúng phần "Việc làm" của phase 6. Không hơn. Không refactor "tiện tay". Không dọn code
xung quanh. Mỗi file chạm vào phải nằm trong danh sách của phase hoặc được tôi duyệt ở bước 3.
Giữ nguyên chữ ký public đang có; thêm tham số thì phải có default.

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 6: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần demo cTrader / giờ thị trường mở / soak nhiều ngày mà chưa có → ghi "CHƯA KIỂM — cần
  {demo|giờ mở|soak N ngày}", KHÔNG đánh ✅. (Windows đã có sẵn — không dùng lý do này.)
- Báo cáo trung thực. Không nói "xong" khi còn mục ❌ hoặc CHƯA KIỂM.

BƯỚC 6 — BÀN GIAO
- KHÔNG commit. Đề xuất commit message theo CLAUDE.md §7 (format "<phase>: <summary>").
- Cập nhật bảng tiến độ ở docs/plans/ctrader-fix/README.md §7 (trạng thái phase 6).
- Liệt kê những gì "Cổng sang Phase 7" còn thiếu.

QUY TẮC BẤT BIẾN
- Không log, không echo, không đưa vào output: mật khẩu FIX, tag 554, chuỗi sans_json thô.
- Không đổi business logic. Nếu thấy cần → bảng 4 cột (vấn đề/giải pháp/ảnh hưởng/rủi ro) và
  chờ tôi duyệt.
- Không dùng subagent trừ khi tôi yêu cầu.
- Playwright MCP với cTrader Web CHỈ ĐỌC: không bấm New order/Close; không chép snapshot
  .playwright-mcp/ vào repo hay memo — chỉ chép số liệu.
```

---

## Phase 7 — Thực thi lệnh

File plan: [phase-7-execution.md](phase-7-execution.md)

```text
Bạn đang thực hiện PHASE 7 của kế hoạch tích hợp cTrader FIX API cho sàn B.

ĐỌC TRƯỚC, THEO THỨ TỰ, KHÔNG BỎ QUA:
1. CLAUDE.md (toàn bộ — đặc biệt §0.1 Risk Assessment, §0.2 Không thay đổi logic, §2 Rule A–G)
2. docs/plans/ctrader-fix/README.md (bảng rủi ro R1–R11, quyết định thiết kế §4, Phụ lục A/B)
3. docs/plans/ctrader-fix/phase-6-history.md (phase TRƯỚC — để rà soát)
4. docs/plans/ctrader-fix/phase-7-execution.md (phase NÀY — để thực hiện)

BƯỚC 1 — RÀ SOÁT PHASE 6 (bắt buộc, có cổng dừng)
Kiểm tra bằng CODE, TEST và LOG THẬT, không kiểm tra bằng cách đọc lại tài liệu:
- Mở phần "Nghiệm thu" của phase 6. Với TỪNG mục checklist: tìm bằng chứng nó đã pass
  (test tồn tại và xanh / dòng log / hành vi quan sát được). Ghi ✅ kèm bằng chứng, hoặc ❌.
- Chạy: DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj
  So với baseline Windows ghi ở docs/plans/ctrader-fix/phase-0-memo.md §2 — so CẢ con số LẪN danh sách
  tên test fail. Tăng = vấn đề; một fail mới thay một fail cũ cũng = vấn đề.
- Đọc lại diff của phase 6 (git log / git diff) với con mắt của CLAUDE.md §0.2: có dòng nào
  đổi hành vi ngoài phạm vi phase đó không? Có chạm signal engine / coordinator / router /
  Rule A–G không?
- Đối chiếu phần "Rủi ro liên quan" của phase 6: mỗi R# được nhắc có thực sự được xử lý
  trong code không, hay chỉ được nhắc trong tài liệu?

PHASE 7 RÀ SOÁT CẢ 4, 5, 6 (cổng chặt nhất): toàn bộ checklist LỚP 1 ba phase pass và đã soak trên tài
khoản trống; danh sách Lớp 2 của Phase 5/6 đã có trong checklist Bước B; C:\tmp\ctrader-spike còn build
được và Config-dev.LIVE-TRADE.cfg còn; CurrentOpenPendingTimeMs >= 2000 trong DB; max_total_opens = 1
trong DB; contractSizeB = 100; live 8220816 đã nạp >= $30 (kiểm balance trên cTrader Web qua Playwright);
terminal MT chân A dùng cho nghiệm thu là demo hay live — TÔI xác nhận bằng chữ.

Nếu phát hiện BẤT KỲ vấn đề nào, trình bày theo đúng bảng này rồi DỪNG, chờ tôi duyệt:

| # | Vấn đề (file:dòng, bằng chứng) | Giải pháp đề xuất | Ảnh hưởng (phạm vi, phase nào bị lùi) | Rủi ro (nếu sửa / nếu không sửa) |
|---|---|---|---|---|

Không sửa gì ở bước này. Không sang bước 2 khi bảng trên còn dòng chưa được tôi duyệt.
Nếu không có vấn đề: ghi rõ "Phase 6: không phát hiện vấn đề" kèm danh sách bằng chứng.

BƯỚC 2 — CHỐT TRƯỚC KHI CODE
Mở phần "Chốt trước khi code" của phase 7. Với từng câu:
- Đã có đáp án ghi "ĐÃ QUYẾT" → nhắc lại đáp án, làm theo.
- Chưa có → hỏi tôi bằng AskUserQuestion, KHÔNG tự giả định. Chờ đáp án rồi mới tiếp.

BƯỚC 3 — ĐÁNH GIÁ RỦI RO THEO CLAUDE.md §0.1
Trước khi viết dòng code nào, liệt kê: việc làm của phase này có ảnh hưởng logic giao dịch
không? luồng dữ liệu không? có side effect ngoài phạm vi không? Nếu có bất kỳ "có" nào →
trình bày và chờ tôi cho phép. Nếu phase yêu cầu chạm vào hành vi hiện có (kể cả một dòng),
đó là "có".

ĐẶC THÙ PHASE 7 — PHIÊN TIỀN THẬT DUY NHẤT, ba bước bắt buộc theo thứ tự A → B → C trong MỘT ngày:
- BƯỚC A (spike 721, ConsoleSample, TÔI gõ): câu 4 → CÂU 1 → 5 → 6 → 7|spike-pos-final theo bảng trong
  phase-7 "Bước A". Sau câu 4 và câu 1 bạn snapshot tab Positions web (Playwright chỉ đọc) đối chiếu
  Position ID = 721, Quantity 0.01. Câu 1 KHÔNG 728=2 → DỪNG PHASE, ghi memo, đề xuất đổi chiều đóng
  executor sang Open API; KHÔNG chạm App.xaml.cs.
- BƯỚC B (Phase 5/6 Lớp 2, executor vẫn Null, TÔI mở/đóng tay trên web): checklist "Bước B" trong
  phase-7. Restart app khi có position là mục quan trọng nhất (R2).
- BƯỚC C (executor thật) chỉ sau A GO + B pass, và chỉ khi tôi trả lời "duyệt Phase 7" bằng chữ.
- CTraderTradeExecutor < 150 dòng, KHÔNG logic; comment Rule-E passivity ở đầu file (nguyên văn trong
  phase-7). Open: NewOrderSingle market, KHÔNG 721, chờ terminal report với timeout < OpenPendingTimeoutMs.
  Close: ngược chiều + 721 = TryDecode(Ticket). Đọc CẢ 150 lẫn 39.
- OpenPairAsync/ClosePairAsync trả Success=false "không hỗ trợ pair-level dispatch" (B2).
- GIỮ NGUYÊN TryRefreshCloseRows — không bypass cho leg cTrader (Rule F).
- Xác nhận OPEN vẫn qua polling trades map — KHÔNG short-circuit bằng ExecutionReport.
BƯỚC 3: trình bày bảng rủi ro đầy đủ. KHÔNG thay NullCTraderTradeExecutor trong App.xaml.cs cho tới khi
tôi trả lời rõ ràng bằng chữ "duyệt Phase 7".

BƯỚC 4 — THỰC HIỆN
Làm đúng phần "Việc làm" của phase 7. Không hơn. Không refactor "tiện tay". Không dọn code
xung quanh. Mỗi file chạm vào phải nằm trong danh sách của phase hoặc được tôi duyệt ở bước 3.
Giữ nguyên chữ ký public đang có; thêm tham số thì phải có default.

Nghiệm thu chỉ trên demo, max_total_opens = 1, một phiên đầy đủ, TRONG giờ XAUUSD mở (05:00 → 03:59
UTC+7): pair mở bởi AUTO SIGNAL, đóng bởi close signal tự nhiên; verify không mồ côi trên CẢ HAI nền
tảng — chân A qua terminal MT5/MMF, chân B qua tab Positions trên cTrader Web (Playwright CHỈ ĐỌC) = 0;
test rollback partial open cả hai chiều; nút Đóng per-pair; restart khi có pair mở; ma trận 6 ô chạy
tại chỗ với 2 MT5 (4 ô MT-MT là hồi quy). Chỉ sau khi pass hết mới nâng max_total_opens.

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 7: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần demo cTrader / giờ thị trường mở / soak nhiều ngày mà chưa có → ghi "CHƯA KIỂM — cần
  {demo|giờ mở|soak N ngày}", KHÔNG đánh ✅. (Windows đã có sẵn — không dùng lý do này.)
- Báo cáo trung thực. Không nói "xong" khi còn mục ❌ hoặc CHƯA KIỂM.

BƯỚC 6 — BÀN GIAO
- KHÔNG commit. Đề xuất commit message theo CLAUDE.md §7 (format "<phase>: <summary>").
- Cập nhật bảng tiến độ ở docs/plans/ctrader-fix/README.md §7 (trạng thái phase 7).
- Liệt kê những gì "Cổng sang Phase 8" còn thiếu.

QUY TẮC BẤT BIẾN
- Không log, không echo, không đưa vào output: mật khẩu FIX, tag 554, chuỗi sans_json thô.
- Không đổi business logic. Nếu thấy cần → bảng 4 cột (vấn đề/giải pháp/ảnh hưởng/rủi ro) và
  chờ tôi duyệt.
- Không dùng subagent trừ khi tôi yêu cầu.
- Playwright MCP với cTrader Web CHỈ ĐỌC: không bấm New order/Close; không chép snapshot
  .playwright-mcp/ vào repo hay memo — chỉ chép số liệu.
```

---

## Phase 8 — Hardening + tài liệu

File plan: [phase-8-hardening.md](phase-8-hardening.md)

```text
Bạn đang thực hiện PHASE 8 của kế hoạch tích hợp cTrader FIX API cho sàn B.

ĐỌC TRƯỚC, THEO THỨ TỰ, KHÔNG BỎ QUA:
1. CLAUDE.md (toàn bộ — đặc biệt §0.1 Risk Assessment, §0.2 Không thay đổi logic, §2 Rule A–G)
2. docs/plans/ctrader-fix/README.md (bảng rủi ro R1–R11, quyết định thiết kế §4, Phụ lục A/B)
3. docs/plans/ctrader-fix/phase-7-execution.md (phase TRƯỚC — để rà soát)
4. docs/plans/ctrader-fix/phase-8-hardening.md (phase NÀY — để thực hiện)

BƯỚC 1 — RÀ SOÁT PHASE 7 (bắt buộc, có cổng dừng)
Kiểm tra bằng CODE, TEST và LOG THẬT, không kiểm tra bằng cách đọc lại tài liệu:
- Mở phần "Nghiệm thu" của phase 7. Với TỪNG mục checklist: tìm bằng chứng nó đã pass
  (test tồn tại và xanh / dòng log / hành vi quan sát được). Ghi ✅ kèm bằng chứng, hoặc ❌.
- Chạy: DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj
  So với baseline Windows ghi ở docs/plans/ctrader-fix/phase-0-memo.md §2 — so CẢ con số LẪN danh sách
  tên test fail. Tăng = vấn đề; một fail mới thay một fail cũ cũng = vấn đề.
- Đọc lại diff của phase 7 (git log / git diff) với con mắt của CLAUDE.md §0.2: có dòng nào
  đổi hành vi ngoài phạm vi phase đó không? Có chạm signal engine / coordinator / router /
  Rule A–G không?
- Đối chiếu phần "Rủi ro liên quan" của phase 7: mỗi R# được nhắc có thực sự được xử lý
  trong code không, hay chỉ được nhắc trong tài liệu?

Riêng Phase 7: log một phiên đầy đủ mở→đóng bởi auto signal ở max_total_opens=1; test rollback partial
open cả hai chiều có log; ma trận 6 ô; số liệu hedge ratio thực tế đã ghi.

Nếu phát hiện BẤT KỲ vấn đề nào, trình bày theo đúng bảng này rồi DỪNG, chờ tôi duyệt:

| # | Vấn đề (file:dòng, bằng chứng) | Giải pháp đề xuất | Ảnh hưởng (phạm vi, phase nào bị lùi) | Rủi ro (nếu sửa / nếu không sửa) |
|---|---|---|---|---|

Không sửa gì ở bước này. Không sang bước 2 khi bảng trên còn dòng chưa được tôi duyệt.
Nếu không có vấn đề: ghi rõ "Phase 7: không phát hiện vấn đề" kèm danh sách bằng chứng.

BƯỚC 2 — CHỐT TRƯỚC KHI CODE
Mở phần "Chốt trước khi code" của phase 8. Với từng câu:
- Đã có đáp án ghi "ĐÃ QUYẾT" → nhắc lại đáp án, làm theo.
- Chưa có → hỏi tôi bằng AskUserQuestion, KHÔNG tự giả định. Chờ đáp án rồi mới tiếp.

BƯỚC 3 — ĐÁNH GIÁ RỦI RO THEO CLAUDE.md §0.1
Trước khi viết dòng code nào, liệt kê: việc làm của phase này có ảnh hưởng logic giao dịch
không? luồng dữ liệu không? có side effect ngoài phạm vi không? Nếu có bất kỳ "có" nào →
trình bày và chờ tôi cho phép. Nếu phase yêu cầu chạm vào hành vi hiện có (kể cả một dòng),
đó là "có".

ĐẶC THÙ PHASE 8 — không thêm khả năng, chỉ cảnh báo + tài liệu:
- Wire HedgeVolumeConsistencyChecker (contractSizeB từ config, không hardcode 100) và các Telegram
  event có debounce.
- README §7.1/§7.2/§8/§13 và CLAUDE.md §5/§9 theo ĐÚNG khối text trong phase-8-hardening.md.
- R8: quyết bằng số liệu thật từ Phase 4/7. Nếu cần confirm_latency_ms_b riêng → mở issue riêng,
  KHÔNG code trong phase này.

BƯỚC 4 — THỰC HIỆN
Làm đúng phần "Việc làm" của phase 8. Không hơn. Không refactor "tiện tay". Không dọn code
xung quanh. Mỗi file chạm vào phải nằm trong danh sách của phase hoặc được tôi duyệt ở bước 3.
Giữ nguyên chữ ký public đang có; thêm tham số thì phải có default.

Cập nhật bảng tiến độ docs/plans/ctrader-fix/README.md §7: toàn bộ phase hoàn thành. Liệt kê rõ danh
sách "Sau Phase 8" (để ngoài phạm vi) trong báo cáo cuối.

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 8: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần demo cTrader / giờ thị trường mở / soak nhiều ngày mà chưa có → ghi "CHƯA KIỂM — cần
  {demo|giờ mở|soak N ngày}", KHÔNG đánh ✅. (Windows đã có sẵn — không dùng lý do này.)
- Báo cáo trung thực. Không nói "xong" khi còn mục ❌ hoặc CHƯA KIỂM.

BƯỚC 6 — BÀN GIAO
- KHÔNG commit. Đề xuất commit message theo CLAUDE.md §7 (format "<phase>: <summary>").
- Cập nhật bảng tiến độ ở docs/plans/ctrader-fix/README.md §7 (trạng thái phase 8).
- Đây là phase cuối — liệt kê danh sách "Sau Phase 8" (ngoài phạm vi) thay cho cổng sang phase sau.

QUY TẮC BẤT BIẾN
- Không log, không echo, không đưa vào output: mật khẩu FIX, tag 554, chuỗi sans_json thô.
- Không đổi business logic. Nếu thấy cần → bảng 4 cột (vấn đề/giải pháp/ảnh hưởng/rủi ro) và
  chờ tôi duyệt.
- Không dùng subagent trừ khi tôi yêu cầu.
- Playwright MCP với cTrader Web CHỈ ĐỌC: không bấm New order/Close; không chép snapshot
  .playwright-mcp/ vào repo hay memo — chỉ chép số liệu.
```

---

## Phụ lục — memo Phase 0

Memo thật đã tồn tại tại [phase-0-memo.md](phase-0-memo.md) với cấu trúc §1 Chuẩn bị · §2 Baseline ·
§3 Kiểm offline · §4 Điều tra 11 fail · §5 Spike 10 câu · §6 Còn phải làm · §7 Kết luận. **Điền tiếp
vào file đó**, không tạo memo mới theo mẫu cũ.
