# Phase 2 — Cấu hình, UI và lưu token

> Người dùng chọn được PrimeXBT trong cửa sổ Config, nhập thông số, đăng nhập một lần; token được lưu an toàn trên máy.
> **Chưa mở socket giao dịch.**

[← Phase 1](phase-1-platform-enum.md) · [Index](README.md) · Phase sau: [Phase 3](phase-3-protocol-core-offline.md)

---

## Mục tiêu

- Block `primexbt` trong `sans_json` (giống `ctraderFix`) + cột latency B riêng.
- Lưu/đọc token cục bộ bằng DPAPI, có trạng thái hiển thị (đã đăng nhập / hết hạn lúc …).
- Cửa sổ đăng nhập lấy token mà không bắt user copy tay.

## Cổng vào (recheck)

| # | Kiểm tra |
|---|---|
| 2.R1 | Bước chung README §8 (1–5) |
| 2.R2 | Toàn bộ nghiệm thu Phase 1 (1-A1…1-A4) chạy lại PASS |
| 2.R3 | Spike `refresh` (Phase 0 bước 0.5) vẫn thành công — nếu FAIL thì **dừng**, chiến lược token phải xem lại trước |

## Chốt trước khi code

| # | Câu hỏi | Đề xuất |
|---|---|---|
| 1 | Trường nào vào `sans_json.primexbt`? | `accountId` (vd `D…`/`L…`), `symbol` (`XAU/USD`), `volumeBOz` (ounce, > 0, bội `orderStep`), `contractSizeB` (mặc định 100), `volumeALots` (khai báo, như cTrader), `env` (`demo`/`live`, chỉ để hiển thị/kiểm tra khớp `metrics.demo`) |
| 2 | Latency B | ✅ **Chốt 2026-10-09: lưu trong `sans_json.primexbt.confirmLatencyB`** (nullable, không clamp; null ⇒ dùng `confirm_latency` chung). **Không** migration DB, **không** tái dùng cột ctrader. Gợi ý giá trị đầu từ memo Phase 0: ≥ 1500 ms (tick interval max ~1 s) |
| 3 | Token lưu đâu? | `%LOCALAPPDATA%\TradeDesktop\primexbt\{hostname}.bin`, DPAPI CurrentUser. **Không lên Supabase** (D5). Log chỉ in `exp` + 6 ký tự cuối `sid` |
| 4 | Đăng nhập thế nào? | ✅ **Chốt 2026-10-09: WebView2 trong app** (chủ dự án duyệt thêm NuGet `Microsoft.Web.WebView2`). User đăng nhập trên trang thật; app đọc `localStorage["prm-token"]` qua `ExecuteScriptAsync` và cookie httpOnly `fws_token`, `refresh_token` (domain `.api.primexbt.com`) qua `CoreWebView2.CookieManager` |
| 5 | `auth-guard` | ✅ Phase 0 Q1: **server không kiểm tra** (bỏ hoặc random đều kết nối được) ⇒ không lưu, không gửi. Bắt buộc chỉ: JWT + cookie `fws_token` (WS) / `refresh_token` (refresh) |

## Việc làm

| File | Việc |
|---|---|
| `Application/Models/PrimeXbtConfig.cs` (mới) | Record + `Normalize()`, `GetMissingRequiredFields()` |
| `Application/Helpers/SansJsonHelper.cs` | Đọc/ghi block `primexbt`; giữ nguyên block `ctraderFix` (round-trip không mất key) |
| `ConfigLoadResult`, `ConfigService` | Đưa `PrimeXbtConfig` (kể cả `confirmLatencyB`, nullable, **không clamp**) từ `sans_json` ra runtime — **không** thêm cột DB |
| `RuntimeConfigState.cs` | `CurrentPrimeXbtConfig`, `UpdatePrimeXbt(...)`; `CurrentMapName2` → `PRIMEXBT_B` khi B = primexbt; `CurrentConfirmLatencyMsBEffective` dùng cột mới khi B = primexbt. **Tham số mới dùng sentinel** (bẫy `Update` — CLAUDE.md §5) |
| `Infrastructure/PrimeXbt/PrimeXbtTokenStore.cs` (mới) | DPAPI load/save/clear, `ExpiresAtUtc`, không bao giờ log token |
| `ConfigViewModel.cs`, `ConfigWindow.xaml` | Radio "PrimeXBT", panel: account, symbol, volume (oz) + hiển thị lot tương đương, latency, nút **Đăng nhập** / **Đăng xuất**, trạng thái token. `IsPlatformBMtMode=false` khi primexbt; HWND B không bắt buộc |
| `App/Views/PrimeXbtLoginWindow` (mới, nếu chọn WebView2) | Mở `https://primexbt.com/my/id/sign-in`, chờ `prm-token`, lưu qua TokenStore, đóng |

## Test mới

- `SansJsonPrimeXbtTests`: round-trip, giữ `ctraderFix` song song, thiếu block → null.
- `PrimeXbtConfigTests`: volume không bội step, `contractSizeB <= 0`, `accountId` rỗng.
- `PrimeXbtConfirmLatencyBTests`: chỉ có hiệu lực khi B = primexbt; null → mặc định; **không ảnh hưởng ctrader**.
- `RuntimeConfigState` overload ngắn không reset field PrimeXBT (lớp lỗi sentinel).
- `PrimeXbtTokenStoreTests`: round-trip, file hỏng → coi như chưa đăng nhập, không throw.

## Rủi ro liên quan

P2, D5, bẫy sentinel `RuntimeConfigState.Update`.

## Nghiệm thu

| # | Tiêu chí | Kết quả |
|---|---|---|
| 2-A1 | Lưu/đọc config PrimeXBT qua Supabase, mở lại cửa sổ Config vẫn đúng | |
| 2-A2 | Mở cửa sổ Config **không** làm reset latency/ cấu hình B đang chạy | |
| 2-A3 | Đăng nhập demo → token lưu, hiển thị `exp`; Đăng xuất → xoá file | |
| 2-A4 | grep log phiên: không có chuỗi `eyJ`, không có cookie | |
| 2-A5 | Ma trận cũ smoke sạch; config ctrader round-trip không đổi | |
| 2-A6 | Test fail = baseline, cùng tên; không warning mới | ✅ 1137 / 11, warning 3 (2026-10-09) |

Kết quả 2026-10-09 (agent tự chạy qua UI Automation): 2-A1 ✅ (P2-3/P2-4) · 2-A2 ✅ (P2-4) · 2-A3 ✅ (P2-2 sau khi sửa
cookie path, P2-5) · 2-A4 ✅ (0 chuỗi token trong log/DB; file DPAPI không đọc được JWT) · 2-A5 ✅ (P2-6) · 2-A6 ✅.

**Bẫy cho Phase 4:** cookie `api.primexbt.com` có **path** riêng — `fws_token` chỉ gửi tới `/v2/fws`, `refresh_token` chỉ
tới `/v2/auth/refresh`. `CookieContainer` của transport phải thêm cookie với path phù hợp (hoặc `/`) — spike thêm với
path `/` và chạy được.

## Hướng dẫn smoke cho chủ dự án (laptop demo)

> Tắt app `TradeMulti.exe` trước, build lại Release (`dotnet build -c Release` ở thư mục repo), mở lại
> `bin\Release\net8.0-windows\TradeMulti.exe` (thư mục vẫn còn `mt5engine_capi.dll`).

| Bước | Làm | Kỳ vọng |
|---|---|---|
| P2-1 | Config → chọn radio **PrimeXBT** | Ẩn Map Name 2 / HWND B; hiện panel PrimeXBT (Kênh B = `PRIMEXBT_B`, symbol `XAU/USD`, contract 100 điền sẵn) |
| P2-2 | Bấm **Đăng nhập** → đăng nhập tài khoản PrimeXBT trong cửa sổ WebView2 | Cửa sổ tự đóng; trạng thái "✔ Đã đăng nhập — JWT hết hạn …" (2-A3) |
| P2-3 | Nhập Account ID `D1282507`, Volume B (oz) `1`, Volume A `0.01`, Latency B `2000` → **Save** | Save OK, không đòi HWND B. DB: `platform_b='primexbt'`, `sans->'primexbt'` có đủ trường, `sans->'ctraderFix'` (nếu có) giữ nguyên, **không có token** (2-A1, 2-A4) |
| P2-4 | Đóng/mở lại Config | Giá trị PrimeXBT còn nguyên; mở Config không làm đổi runtime (2-A2) |
| P2-5 | Bấm **Đăng xuất** | Trạng thái "Chưa đăng nhập" (file token bị xoá) |
| P2-6 | Chọn lại **MT5**, Save, Reconnect, Start 1' | Mọi thứ như S1 Phase 1; khối primexbt vẫn còn trong sans (2-A5) |

Query kiểm DB: `select platform_b, sans->'primexbt' as px, sans ? 'ctraderFix' as has_ctrader from public.configs where hostname = 'laptop-eoj2n95d';`

## Rollback

Revert commit; cột DB mới nullable nên để lại vô hại. Xoá file token cục bộ.

## Cổng ra → Phase 3

2-A1…2-A6 ✅.

## Nhật ký recheck

| Ngày | Commit | Test (pass/fail) | Kết luận |
|---|---|---|---|
| | | | |
