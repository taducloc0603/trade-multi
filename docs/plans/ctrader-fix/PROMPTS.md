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

---

## Mẫu bảng báo cáo vấn đề (Bước 1)

| # | Vấn đề (file:dòng, bằng chứng) | Giải pháp đề xuất | Ảnh hưởng (phạm vi, phase nào bị lùi) | Rủi ro (nếu sửa / nếu không sửa) |
|---|---|---|---|---|
| 1 | … | … | … | … |

---

## Phase 0 — Spike khử rủi ro

File plan: [phase-0-spike.md](phase-0-spike.md)

```text
Bạn đang thực hiện PHASE 0 của kế hoạch tích hợp cTrader FIX API cho sàn B.

ĐỌC TRƯỚC, THEO THỨ TỰ, KHÔNG BỎ QUA:
1. CLAUDE.md (toàn bộ — đặc biệt §0.1 Risk Assessment, §0.2 Không thay đổi logic, §2 Rule A–G)
2. docs/plans/ctrader-fix/README.md (bảng rủi ro R1–R11, quyết định thiết kế §4, Phụ lục A/B)
3. docs/plans/ctrader-fix/phase-0-spike.md (phase NÀY — để thực hiện)

BƯỚC 1 — CHUẨN BỊ (Phase 0 không có phase trước)

LƯU Ý PHASE 0: không có phase trước. Thay toàn bộ bước rà soát bằng:
- Xác nhận `git status` sạch và đang ở branch mới tách từ `dev-5-gap-on-dinh`.
- Đo baseline test: chạy lệnh dotnet test ở trên, ghi CHÍNH XÁC số pass/fail vào memo.
- Xác nhận KHÔNG có nhánh logic nào rẽ theo TradeSharedRecord.Sl/Tp hay HistorySharedRecord.Commission
  (grep + đọc call site), và đọc CurrentOpenPendingTimeMs thực tế trong DB.

Nếu phát hiện BẤT KỲ vấn đề nào, trình bày theo đúng bảng này rồi DỪNG, chờ tôi duyệt:

| # | Vấn đề (file:dòng, bằng chứng) | Giải pháp đề xuất | Ảnh hưởng (phạm vi, phase nào bị lùi) | Rủi ro (nếu sửa / nếu không sửa) |
|---|---|---|---|---|

Không sửa gì ở bước này. Không sang bước 2 khi bảng trên còn dòng chưa được tôi duyệt.
Nếu không có vấn đề: ghi rõ "Chuẩn bị Phase 0: OK" kèm baseline.

BƯỚC 2 — CHỐT TRƯỚC KHI CODE
Mở phần "Chốt trước khi code" của phase 0. Với từng câu:
- Đã có đáp án ghi "ĐÃ QUYẾT" → nhắc lại đáp án, làm theo.
- Chưa có → hỏi tôi bằng AskUserQuestion, KHÔNG tự giả định. Chờ đáp án rồi mới tiếp.

BƯỚC 3 — ĐÁNH GIÁ RỦI RO THEO CLAUDE.md §0.1
Trước khi viết dòng code nào, liệt kê: việc làm của phase này có ảnh hưởng logic giao dịch
không? luồng dữ liệu không? có side effect ngoài phạm vi không? Nếu có bất kỳ "có" nào →
trình bày và chờ tôi cho phép. Nếu phase yêu cầu chạm vào hành vi hiện có (kể cả một dòng),
đó là "có".

ĐẶC THÙ PHASE 0 — spike bằng ConsoleSample của Spotware, ZERO code production:
- Clone https://github.com/spotware/quickfixnsamples.net vào /tmp/ctrader-spike (NGOÀI repo).
- Tạo hai file Config-dev.cfg (TRADE và QUOTE) theo bảng trong phase-0-spike.md. Tôi sẽ cung cấp
  mật khẩu qua kênh riêng — KHÔNG hỏi lại trong chat, KHÔNG in file cfg ra output.
- Thứ tự bắt buộc: câu 9 và 8 (logon) → câu 3 (SecurityList) → câu 4 (mở position) → CÂU 1 (đóng bằng
  721). Nếu câu 1 KHÔNG trả 728=2 → DỪNG TOÀN BỘ, không làm câu còn lại, báo cáo ngay.
- Chỉ làm câu 2, 5, 6, 7, 10 sau khi câu 1 GO.

BƯỚC 4 — THỰC HIỆN
Làm đúng phần "Việc làm" của phase 0. Không hơn. Không refactor "tiện tay". Không dọn code
xung quanh. Mỗi file chạm vào phải nằm trong danh sách của phase hoặc được tôi duyệt ở bước 3.
Giữ nguyên chữ ký public đang có; thêm tham số thì phải có default.

DELIVERABLE: docs/plans/ctrader-fix/phase-0-memo.md gồm: bảng 10 câu × (kết quả | log raw đã che
554 | kết luận); baseline test; kết luận layering (QuickFIXn có restore trên darwin không); ghi nhận
QuickFixNApp.ToAdmin tự gắn 553/554. Cuối phiên: chạy 7|spike-pos-final và xác nhận 728=2; xoá
Config-dev.cfg, store/, log/.

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 0: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần Windows / demo cTrader mà môi trường hiện tại không có → ghi "CHƯA KIỂM — cần
  {Windows|demo}", KHÔNG đánh ✅.
- Báo cáo trung thực. Không nói "xong" khi còn mục ❌ hoặc CHƯA KIỂM.

BƯỚC 6 — BÀN GIAO
- KHÔNG commit. Đề xuất commit message theo CLAUDE.md §7 (format "<phase>: <summary>").
- Cập nhật bảng tiến độ ở docs/plans/ctrader-fix/README.md §7 (trạng thái phase 0).
- Liệt kê những gì "Cổng sang Phase 1" còn thiếu.

QUY TẮC BẤT BIẾN
- Không log, không echo, không đưa vào output: mật khẩu FIX, tag 554, chuỗi sans_json thô.
- Không đổi business logic. Nếu thấy cần → bảng 4 cột (vấn đề/giải pháp/ảnh hưởng/rủi ro) và
  chờ tôi duyệt.
- Không dùng subagent trừ khi tôi yêu cầu.
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
  So với baseline ghi ở docs/plans/ctrader-fix/phase-0-memo.md. Tăng = vấn đề.
- Đọc lại diff của phase 0 (git log / git diff) với con mắt của CLAUDE.md §0.2: có dòng nào
  đổi hành vi ngoài phạm vi phase đó không? Có chạm signal engine / coordinator / router /
  Rule A–G không?
- Đối chiếu phần "Rủi ro liên quan" của phase 0: mỗi R# được nhắc có thực sự được xử lý
  trong code không, hay chỉ được nhắc trong tài liệu?

Riêng Phase 0 (spike, không có code): kiểm memo phase-0-memo.md có đủ 10 câu kèm log raw; câu 1 kết
luận GO; baseline có con số; kết luận layering có mặt; Config-dev.cfg/store/log đã xoá khỏi máy.

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
ma trận 8 dòng ở phần Nghiệm thu. Smoke Windows nếu có máy: cả 4 tổ hợp MT-MT không hồi quy.

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 1: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần Windows / demo cTrader mà môi trường hiện tại không có → ghi "CHƯA KIỂM — cần
  {Windows|demo}", KHÔNG đánh ✅.
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
  So với baseline ghi ở docs/plans/ctrader-fix/phase-0-memo.md. Tăng = vấn đề.
- Đọc lại diff của phase 1 (git log / git diff) với con mắt của CLAUDE.md §0.2: có dòng nào
  đổi hành vi ngoài phạm vi phase đó không? Có chạm signal engine / coordinator / router /
  Rule A–G không?
- Đối chiếu phần "Rủi ro liên quan" của phase 1: mỗi R# được nhắc có thực sự được xử lý
  trong code không, hay chỉ được nhắc trong tài liệu?

Riêng Phase 1: grep chắc cả 3 chỗ normalize đều nhận "ctrader"; NullCTraderTradeExecutor đăng ký ở
App.xaml.cs; ValidatePlatformOrThrow có assert chân A; test parity xanh.

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

BƯỚC 4 — THỰC HIỆN
Làm đúng phần "Việc làm" của phase 2. Không hơn. Không refactor "tiện tay". Không dọn code
xung quanh. Mỗi file chạm vào phải nằm trong danh sách của phase hoặc được tôi duyệt ở bước 3.
Giữ nguyên chữ ký public đang có; thêm tham số thì phải có default.

Layout ConfigWindow soi gương panel FIX API của cTrader (khối QUOTE, khối TRADE, phần chung) theo bảng
ánh xạ trong phase-2. PasswordBox: không bind hai chiều; load không đổ mật khẩu ngược vào ô; Save với
ô trống = giữ mật khẩu cũ.

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 2: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần Windows / demo cTrader mà môi trường hiện tại không có → ghi "CHƯA KIỂM — cần
  {Windows|demo}", KHÔNG đánh ✅.
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
  So với baseline ghi ở docs/plans/ctrader-fix/phase-0-memo.md. Tăng = vấn đề.
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
- Đọc trước Common/QuickFixNApp.cs và ConsoleSample/Program.cs của SDK Spotware (README Phụ lục B).
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
  Mục nào cần Windows / demo cTrader mà môi trường hiện tại không có → ghi "CHƯA KIỂM — cần
  {Windows|demo}", KHÔNG đánh ✅.
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
  So với baseline ghi ở docs/plans/ctrader-fix/phase-0-memo.md. Tăng = vấn đề.
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

Nghiệm thu cần Windows + demo: ghi "CHƯA KIỂM" cho các mục đó nếu không có. Đặc biệt ghi lại tần
suất skip LATENCY chân B trong soak — đó là dữ liệu quyết R8 ở Phase 8.

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 4: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần Windows / demo cTrader mà môi trường hiện tại không có → ghi "CHƯA KIỂM — cần
  {Windows|demo}", KHÔNG đánh ✅.
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
  So với baseline ghi ở docs/plans/ctrader-fix/phase-0-memo.md. Tăng = vấn đề.
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

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 5: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần Windows / demo cTrader mà môi trường hiện tại không có → ghi "CHƯA KIỂM — cần
  {Windows|demo}", KHÔNG đánh ✅.
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
  So với baseline ghi ở docs/plans/ctrader-fix/phase-0-memo.md. Tăng = vấn đề.
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

BƯỚC 4 — THỰC HIỆN
Làm đúng phần "Việc làm" của phase 6. Không hơn. Không refactor "tiện tay". Không dọn code
xung quanh. Mỗi file chạm vào phải nằm trong danh sách của phase hoặc được tôi duyệt ở bước 3.
Giữ nguyên chữ ký public đang có; thêm tham số thì phải có default.

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 6: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần Windows / demo cTrader mà môi trường hiện tại không có → ghi "CHƯA KIỂM — cần
  {Windows|demo}", KHÔNG đánh ✅.
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
  So với baseline ghi ở docs/plans/ctrader-fix/phase-0-memo.md. Tăng = vấn đề.
- Đọc lại diff của phase 6 (git log / git diff) với con mắt của CLAUDE.md §0.2: có dòng nào
  đổi hành vi ngoài phạm vi phase đó không? Có chạm signal engine / coordinator / router /
  Rule A–G không?
- Đối chiếu phần "Rủi ro liên quan" của phase 6: mỗi R# được nhắc có thực sự được xử lý
  trong code không, hay chỉ được nhắc trong tài liệu?

PHASE 7 RÀ SOÁT CẢ 4, 5, 6 (cổng chặt nhất): toàn bộ checklist ba phase pass và đã soak; memo Phase 0
câu 1 và câu 4 — nếu cũ hơn 2 tuần, CHẠY LẠI bằng ConsoleSample và đính log mới; CurrentOpenPendingTimeMs
>= 2000 trong DB; max_total_opens = 1 trong DB; contractSizeB trong config = giá trị Phase 0 xác minh.

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

ĐẶC THÙ PHASE 7 — PHASE DUY NHẤT ĐẶT LỆNH THẬT:
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

Nghiệm thu chỉ trên demo, max_total_opens = 1, một phiên đầy đủ: pair mở bởi AUTO SIGNAL, đóng bởi
close signal tự nhiên, verify không mồ côi trên CẢ HAI nền tảng; test rollback partial open cả hai
chiều; nút Đóng per-pair; restart khi có pair mở; ma trận 6 ô (4 ô MT-MT là hồi quy). Chỉ sau khi pass
hết mới nâng max_total_opens.

BƯỚC 5 — NGHIỆM THU
- Chạy test suite, so baseline. Build sạch, không warning mới.
- Điền phần "Nghiệm thu" của phase 7: từng mục ✅ kèm bằng chứng, hoặc ❌ kèm lý do.
  Mục nào cần Windows / demo cTrader mà môi trường hiện tại không có → ghi "CHƯA KIỂM — cần
  {Windows|demo}", KHÔNG đánh ✅.
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
  So với baseline ghi ở docs/plans/ctrader-fix/phase-0-memo.md. Tăng = vấn đề.
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
  Mục nào cần Windows / demo cTrader mà môi trường hiện tại không có → ghi "CHƯA KIỂM — cần
  {Windows|demo}", KHÔNG đánh ✅.
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
```

---

## Phụ lục — mẫu memo Phase 0 (`phase-0-memo.md`)

```markdown
# Phase 0 memo — go/no-go

Ngày: YYYY-MM-DD · Branch: … · Người chạy: …

## Baseline test
`DOTNET_ROLL_FORWARD=Major dotnet test …` → pass: N, fail: M (liệt kê tên test fail)

## Layering
QuickFIXn.Core / QuickFIXn.FIX4.4 restore trên darwin/net8.0: OK | FAIL (lý do)
→ Phase 3 đặt ở: Infrastructure/CTrader | project TradeDesktop.CTrader

## Kết quả 10 câu (log raw đã che 554)

| Câu | Kết quả | Log raw (rút gọn) | Kết luận |
|---|---|---|---|
| 1 | GO / NO-GO | `8=FIX.4.4|35=8|150=F|721=…` … `35=AP|728=2` | … |
| … | | | |

## Kết luận
GO → sang Phase 1 | NO-GO → dừng, lý do
Đã dọn: Config-dev.cfg ☐ · store/ ☐ · log/ ☐ · 7|spike-pos-final → 728=2 ☐
```
