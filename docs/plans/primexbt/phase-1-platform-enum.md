# Phase 1 — Nhận diện platform `primexbt`, chưa có kết nối

> **Không có một dòng code mạng nào.** App biết `primexbt` là giá trị hợp lệ cho `platform_b`, mọi lệnh chân B
> thất bại **to và rõ** qua `NullPrimeXbtTradeExecutor`. MT4/MT5/cTrader chạy y hệt trước.

[← Phase 0](phase-0-spike.md) · [Index](README.md) · Phase sau: [Phase 2](phase-2-config-auth.md)

---

## Mục tiêu

Mở đường cho các phase sau mà không thay đổi hành vi của ba platform hiện có. Đây cũng là **kill switch**: DI về
Null executor là tắt PrimeXBT ngay.

## Phụ thuộc

- Memo Phase 0 kết luận **GO-W**.
- Baseline test/warning trong memo Phase 0.

## Cổng vào (recheck)

| # | Kiểm tra |
|---|---|
| 1.R1 | Bước chung 1–5 ở [README §8](README.md#8-giao-thức-recheck--áp-dụng-cho-mọi-phase); số test fail = baseline Phase 0, **cùng tên** |
| 1.R2 | `scan/primexbt` spike vẫn chạy `watch` 5 phút được (giao thức chưa đổi kể từ Phase 0) |
| 1.R3 | `PlatformNormalizationTests`, `PlatformBSwitchGuardTests`, `CTraderPhase2RulesTests` đang PASS (ghi tên) |

## Chốt trước khi code

| # | Câu hỏi | Đề xuất |
|---|---|---|
| 1 | Chuỗi platform | `"primexbt"` (lowercase, không dấu gạch) |
| 2 | Enum | `TradeLegPlatform.PrimeXbt = 3` (giữ `CTrader = 2`) |
| 3 | `platform_a = primexbt` | **Reject cứng** ở `ConfigService.SaveByMachineHostNameAsync` (giống ctrader) |
| 4 | Lệnh B khi chưa có executor thật | `Success=false`, `Detail="PrimeXBT chưa được kích hoạt"`, **không throw** |

## Việc làm

| File | Việc |
|---|---|
| `ConfigService.cs` (2 chỗ), `RuntimeConfigState.cs`, `SupabaseConfigRepository.cs`, `ConfigViewModel.cs` | Thêm `"primexbt"` vào whitelist `NormalizePlatform` — **cả 5 trong cùng một commit** (R7) |
| `TradeDesktop.Application/Services/PrimeXbt/PrimeXbtRoutingRules.cs` (mới) | `IsPrimeXbtPlatform`, `TradeMapName = "PRIMEXBT_B_Trades"`, `HistoryMapName = "PRIMEXBT_B_History"`; **không** sửa `CTraderRoutingRules` |
| `TradeDesktop.App/Services/ITradeExecutionRouter.cs` | `TradeLegPlatform.PrimeXbt = 3` |
| `TradeDesktop.App/Services/TradeExecutionRouter.cs` | ctor `FirstOrDefault(PrimeXbt)`; `ValidatePlatformOrThrow` chặn A = PrimeXbt; `ResolveExecutor` case mới, throw rõ nếu null |
| `TradeDesktop.App/Services/NullPrimeXbtTradeExecutor.cs` (mới) | Luôn `Success=false`, không throw |
| `App.xaml.cs` | Đăng ký `NullPrimeXbtTradeExecutor` là `ITradePlatformExecutor` |
| `DashboardViewModel.ResolveTradeLegPlatform` | `"primexbt" => TradeLegPlatform.PrimeXbt` |
| `ConfigService.SaveByMachineHostNameAsync` | Reject A = primexbt |
| `PlatformBSwitchGuard.Evaluate` | Chặn đổi B khi còn slot nếu old **hoặc** new là `primexbt` (thêm điều kiện, không đổi nhánh ctrader) |

## Test mới / cập nhật

- `PlatformNormalizationTests`: 5 bản sao cùng chấp nhận `primexbt`, cùng fallback unknown → `mt5`.
- `PrimeXbtRoutingRulesTests`: map name, case-insensitive, không nhận `ctrader`.
- `PlatformBSwitchGuardTests`: mt5→primexbt, primexbt→ctrader, primexbt→primexbt khi còn slot.
- `ConfigService` reject A = primexbt.
- Router: `NullPrimeXbtTradeExecutor` trả lỗi rõ, router đi đường partial/rollback hiện có (không exception).

## Rủi ro liên quan

R7 (thiếu một bản `NormalizePlatform` ⇒ âm thầm thành `mt5` và click HWND cũ), P12.

## Nghiệm thu

| # | Tiêu chí | Kết quả |
|---|---|---|
| 1-A1 | Config `platform_b = primexbt` đọc/ghi Supabase giữ nguyên chuỗi (không bị thành `mt5`) | ✅ S2 2026-10-09 |
| 1-A2 | Start với B = primexbt: không crash; giá B Disconnected, không signal, không lệnh (fail-closed) | ✅ S3 (log `20261009_133834`) |
| 1-A3 | Ma trận `mt4|mt5 × mt4|mt5|ctrader` smoke sạch | ✅ mt5/mt5 thật (S1, S4: 2 cặp mở/đóng, 0 ERROR); các ô khác qua unit test |
| 1-A4 | Test: fail = baseline, cùng tên; test mới PASS; không warning mới | ✅ 1086 / 11, warning 3 |

## Hướng dẫn smoke cho chủ dự án

> Chạy trên máy dev `laptop-eoj2n95d` — **cả hai MT5 đều là tài khoản DEMO** ⇒ **bật Auto** để phủ đủ case
> (chủ dự án chốt 2026-10-09). S1 phải có ≥ 1 cặp open→close thật để kiểm regression router/executor Phase 1.
> Mỗi bước ghi ✅/❌ + tên file log vào bảng nghiệm thu ở trên, hoặc báo lại để agent ghi.
> Query Supabase cho từng bước (kể cả tạo dòng `configs` cho laptop nếu chưa có): [sql/phase1-smoke.sql](sql/phase1-smoke.sql).

| Bước | Làm | Kỳ vọng |
|---|---|---|
| S1 | Chạy `bin\Release\net8.0-windows\TradeMulti.exe` (build có Phase 1; thư mục phải có `mt5engine_capi.dll`) với config mt5/mt5, **bật Auto**, Start tới khi có **≥ 1 cặp open → close**, rồi Stop. Cho nhanh: chạy khối **S1-FAST** trong [sql/phase1-smoke.sql](sql/phase1-smoke.sql) → **bấm Reconnect (bắt buộc — app chỉ nạp config lúc mở/Reconnect; xác nhận bằng dòng `[DB][WARN] confirm_gap_pts=-100` trong log)** → Start (mở ~1 s, đóng ~30 s sau); xong chạy **S1-RESTORE** → Reconnect. **✅ 2026-10-09** (ticket 77460990/77460991, 0 ERROR) | App không crash; cặp mở/đóng đủ 2 chân, đúng pair; log `[ROUTER]` không có `Invalid platform` / `Unsupported platform`; không `[ERROR]` mới |
| S2 | (1-A1) Trên Supabase, bảng config của máy này: ghi lại giá trị `platform_b` hiện tại, rồi đổi thành `primexbt`. Mở lại app → mở cửa sổ **Config** → bấm **Save** (không đổi gì) | Sau Save, cột `platform_b` trên Supabase **vẫn là `primexbt`** (không bị thành `mt5`). Cửa sổ Config chưa có radio PrimeXBT — đúng, đó là việc của Phase 2 |
| S3 | (1-A2) Với `platform_b = primexbt`, **Auto vẫn bật**, bấm **Start**, chờ 2 phút | App không crash; giá sàn B **Disconnected** (kênh `PRIMEXBT_B` không có EA nào ghi); **không có signal Open nào** ⇒ không lệnh nào (đây là thiết kế fail-closed). Đường "chân B fail → rollback chân A" với `NullPrimeXbtTradeExecutor` **chưa chạy tới được ở Phase 1** — kiểm ở Phase 4 (4-A7) khi đã có giá PrimeXBT thật |
| S4 | Stop app. Trả `platform_b` trên Supabase về giá trị ghi ở S2. Mở lại app, Start 1 phút | Mọi thứ như S1 |

**Bẫy đã gặp (2026-10-09):** chạy bản `bin\Release\...\TradeMulti.exe` mà thư mục Release **thiếu
`mt5engine_capi.dll`** ⇒ Save Config báo cả 4 HWND "cửa sổ không tồn tại" dù HWND đúng
(`NativeWindowProbe` nuốt `DllNotFoundException` → `false`). DLL không do csproj build/copy (nguồn
`native/mt5engine-capi/`); chép từ `bin\Debug\net8.0-windows\mt5engine_capi.dll` sang thư mục exe đang chạy.

Nếu S3 cần HWND sàn B để Start: dùng nguyên HWND đang có trong config (Phase 1 vẫn coi B như MT ở phần kiểm tra HWND; Phase 2 sẽ bỏ yêu cầu này cho PrimeXBT).

## Rollback

Revert commit Phase 1. DB: đổi `platform_b` về giá trị cũ trước khi chạy bản cũ (bản cũ đọc `primexbt` → `mt5`!).

## Cổng ra → Phase 2

1-A1…1-A4 ✅. Ghi nhật ký.

## Nhật ký recheck

| Ngày | Commit | Test (pass/fail) | Kết luận |
|---|---|---|---|
| | | | |
