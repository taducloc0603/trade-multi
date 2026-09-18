# Phase 1 — Nhận diện platform `ctrader`, chưa có cTrader

> **Không có một dòng code FIX nào.** Phase này chỉ dạy app biết `ctrader` là một giá trị hợp lệ cho
> `platform_b`, và fail **to và rõ** khi ai đó thử dùng.

[← Phase 0](phase-0-spike.md) · [Index](README.md) · Phase sau: [Phase 2](phase-2-config.md)

---

## Mục tiêu

Mở đường cho các phase sau mà **không thay đổi hành vi MT4/MT5 dù chỉ một chút**.

Kết thúc phase này: chọn được `platform_b = ctrader`; mọi lệnh ở chân B thất bại với thông báo rõ
ràng; hai chân MT4/MT5 chạy y hệt như trước.

---

## Phụ thuộc phase trước

- **Phase 0 đạt GO-READ** ([phase-0-spike.md § Cổng sang Phase 1](phase-0-spike.md)): kết nối SSL,
  SecurityList, market data, reconnect đã chứng minh trên live 8220816. R1 (đóng bằng tag 721) là cổng
  của **Phase 7 Bước A**; nếu R1 fail thì chỉ đổi chiều đóng của executor — Phase 1 **không** bị ảnh hưởng.
- Con số baseline test từ Phase 0 làm gate.

---

## Chốt trước khi code

| # | Câu hỏi | Đề xuất |
|---|---|---|
| 1 | `platform_a = ctrader` bị chặn ở đâu — chỉ ẩn UI, hay reject cứng ở tầng save config? | **Reject cứng ở `ConfigService`**. Ẩn UI thôi là không đủ: config có thể được sửa trực tiếp trong Supabase. |
| 2 | Khi `platform_b = ctrader` mà chưa có executor thật, lệnh B nên fail thế nào? | `Success=false` + `Detail = "cTrader chưa được kích hoạt"`. **Không throw** — throw sẽ đi qua đường exception chưa được kiểm chứng của router. |

---

## Việc làm

### R7 — sửa cả **5** bản sao `NormalizePlatform` trong cùng một commit

Năm bản giống hệt nhau và đều fallback unknown → `"mt5"`. **Plan gốc chỉ liệt kê 3** — hai bản cuối
phát hiện khi làm Phase 1 (2026-09-17), chủ dự án duyệt đưa vào phạm vi:

| File | Dòng |
|---|---|
| `TradeDesktop.Application/Services/ConfigService.cs` | `:25` |
| `TradeDesktop.Application/Services/ConfigService.cs` | `:488` |
| `TradeDesktop.App/State/RuntimeConfigState.cs` | `:460` |
| `TradeDesktop.Infrastructure/Supabase/SupabaseConfigRepository.cs` | `:255` — dùng khi **đọc** row (`:46-47`) **và ghi** PATCH (`:648-649`). Thiếu bản này: `ctrader` bị đọc thành `mt5` và Save ghi đè `mt5` xuống DB |
| `TradeDesktop.App/ViewModels/ConfigViewModel.cs` | `:709` — setter `PlatformA`/`PlatformB`. Thiếu bản này: bấm Save trong cửa sổ Config là ghi `mt5` xuống DB và đẩy `mt5` vào runtime |

```csharp
// hiện tại — cả 5 chỗ
return normalized is "mt4" or "mt5" ? normalized : "mt5";
// sau khi sửa
return normalized is "mt4" or "mt5" or "ctrader" ? normalized : "mt5";
```

> **Đây là rủi ro R7.** Thiếu một chỗ thì `platform_b` âm thầm thành `mt5` và app **click vào HWND
> chart MT5 cũ hoặc sai symbol**. Fallback sai kiểu im lặng là loại nguy hiểm nhất — không có log,
> không có exception, chỉ có lệnh vào sai chỗ.

### Enum + router

| File | Việc |
|---|---|
| `TradeDesktop.App/Services/ITradeExecutionRouter.cs` | Thêm `TradeLegPlatform.CTrader = 2` |
| `TradeDesktop.App/Services/TradeExecutionRouter.cs` (~:29) | ctor: thêm `_ctraderExecutor = executors.FirstOrDefault(x => x.Platform == TradeLegPlatform.CTrader);` — **giữ nguyên 2 lời gọi `.First()` cho MT4/MT5** (cả hai luôn được đăng ký; sửa là ngoài phạm vi §0.2) |
| `TradeDesktop.App/Services/TradeExecutionRouter.cs` (~:781) | `ValidatePlatformOrThrow`: `if (platform is Mt4 or Mt5 or CTrader) return;` |
| `TradeDesktop.App/Services/TradeExecutionRouter.cs` (~:869) | `ResolveExecutor`: thêm case `CTrader`, throw thông báo rõ nếu `_ctraderExecutor` null |
| `TradeDesktop.App/ViewModels/DashboardViewModel.cs` (~:3224) | `ResolveTradeLegPlatform`: thêm `"ctrader" => TradeLegPlatform.CTrader` |

### Guard config

`TradeDesktop.Application/Services/ConfigService.cs` — trong `SaveByMachineHostNameAsync` (~:185),
sau khi normalize:

```csharp
if (normalizedPlatformA == "ctrader")
    return ConfigSaveResult.Failed("cTrader chỉ được dùng cho sàn B.");
```

### B1 — assert chân A không bao giờ là cTrader, ngay tại dispatch

Guard ở `ConfigService` là lớp bảo vệ **duy nhất** nếu chỉ làm theo mô tả trên. Nó nằm xa điểm dispatch
và **không bảo vệ được** trường hợp `platform_a` bị sửa thẳng trong Supabase (bỏ qua app).

`ValidatePlatformOrThrow` vốn đã nhận `exchange` làm tham số — tận dụng:

```csharp
private static void ValidatePlatformOrThrow(TradeLegPlatform platform, string exchange)
{
    if (exchange == "A" && platform == TradeLegPlatform.CTrader)
        throw new InvalidOperationException("cTrader chỉ được dùng cho sàn B.");

    if (platform is TradeLegPlatform.Mt4 or TradeLegPlatform.Mt5 or TradeLegPlatform.CTrader)
        return;

    throw new InvalidOperationException($"Invalid platform for exchange {exchange}: {platform}");
}
```

Rẻ, fail closed, đúng chỗ.

### Null executor

`TradeDesktop.App/Services/NullCTraderTradeExecutor.cs` (mới) — implement `ITradePlatformExecutor`,
`Platform => TradeLegPlatform.CTrader`, mọi method trả
`Success=false, Detail="cTrader chưa được kích hoạt"`.

Đăng ký ở `TradeDesktop.App/App.xaml.cs` (~:50) cạnh hai executor MT:

```csharp
services.AddSingleton<ITradePlatformExecutor, NullCTraderTradeExecutor>();
```

---

## Rủi ro liên quan

**R7** (chủ đạo — 5 bản sao normalize) · §4.1 (không đụng `TryRefreshCloseRows`)

---

## Nghiệm thu

### Unit test — kết quả 2026-09-17 (`TradeDesktop.Tests/Config/PlatformNormalizationTests.cs`, 22 test xanh)

> Test project **không reference App** → không gọi được bản ở `RuntimeConfigState`/`ConfigViewModel`.
> Parity khoá qua **API public** cho 3 bản Application/Infrastructure; 2 bản App kiểm bằng grep.

- [x] **Parity** `NormalizePlatform_AllTestableCopiesAcceptSameSet` (9 ca: `mt4`, `mt5`, `ctrader`, `CTRADER`,
      ` ctrader `, ` MT4 `, `xyz`, `""`, `null`) — cùng input qua **Save** (`ConfigService:28`), **Load**
      (`ConfigService:496`) và **Supabase đọc row** (`SupabaseConfigRepository:255`) cho cùng output.
- [x] Grep 2 bản App: không còn `is "mt4" or "mt5"` nào thiếu `or "ctrader"` (5/5 có).
- [x] `Save_PlatformACTrader_IsRejectedAndNothingWritten` — `Failed("cTrader chỉ được dùng cho sàn B.")`, repository
      **không** được gọi; kèm biến thể `CTRADER` / ` ctrader `.
- [x] `platformB = ctrader` round-trip: Save (ma trận 6 ô), Load, và `SupabaseRepository_WritePath_KeepsCTraderInPatchPayload`
      (PATCH body `platform_b = "ctrader"` từ input ` CTrader `).
- [x] `"CTRADER"`, `" ctrader "` → `"ctrader"` (trong 9 ca parity).

### Ma trận platform

Xem [README — Ma trận platform được hỗ trợ](README.md). Phase này test được **toàn bộ** ở tầng config
bằng unit test:

| `platform_a` | `platform_b` | Kỳ vọng | ☐ |
|---|---|---|---|
| mt4 | mt4 | Save OK, resolve ra `(Mt4, Mt4)` | ✅ Save test · resolve = review code |
| mt4 | mt5 | Save OK, resolve ra `(Mt4, Mt5)` | ✅ Save test · resolve = review code |
| mt5 | mt4 | Save OK, resolve ra `(Mt5, Mt4)` | ✅ Save + Load test · resolve = review code |
| mt5 | mt5 | Save OK, resolve ra `(Mt5, Mt5)` | ✅ Save test · resolve = review code |
| mt4 | ctrader | Save OK, resolve ra `(Mt4, CTrader)` | ✅ Save + Load test · resolve = review code |
| mt5 | ctrader | Save OK, resolve ra `(Mt5, CTrader)` | ✅ Save + Load test · resolve = review code |
| ctrader | bất kỳ | **Save bị reject** | ✅ test |
| (bypass config) A = CTrader tại dispatch | | **`ValidatePlatformOrThrow` throw** (B1) | ⚠️ chỉ review code (`TradeExecutionRouter.cs` `ValidatePlatformOrThrow`) — App không được test reference |

"resolve" = `DashboardViewModel.ResolveTradeLegPlatform` (thêm `"ctrader" => CTrader`) nằm ở App → không unit
test được; App build 0 error / 0 warning.

### Smoke test (Windows)

- [x] App chạy với `mt5`/`mt5`, không crash — chủ dự án chạy 2026-09-17. Bằng chứng: `%LOCALAPPDATA%\TradeDesktop\logs\startup.log`
      có 3 lần `OnStartup begin → Host started successfully → MainWindow shown` (13:49, 14:05, 14:21), 0 dòng error/exception.
      Có `mt5engine_capi.dll` (build tại máy từ `native/mt5engine-capi` bằng cờ CI) và EA DataExporter A/B đang ghi 6 MMF.
- [x] **4 tổ hợp MT-MT**: đổi `platform_a/b` trong Config + Save không lỗi — theo quan sát của chủ dự án; DB cuối phiên
      đọc lại `A=mt5 B=mt5` (đã trả về cấu hình gốc).
- [x] `platform_b = ctrader` trong Supabase → app mở lại **không crash**; Save trong Config → DB vẫn `ctrader` — theo quan
      sát của chủ dự án (Claude không đọc DB đúng thời điểm đó). Đã trả `platform_b` về `mt5`.
- [x] Không có exception trong `startup.log`. Log phiên `Desktop\trade-log\` **không được tạo** vì chỉ mở khi bấm Start
      (không bấm theo phạm vi smoke) — nên không có log `[VM]` Save để đối chiếu thêm.
- ⚠️ Phát hiện phụ (không do Phase 1): hostname máy dev trong `configs` có ký tự xuống dòng cuối → Load qua `ilike` vẫn
  đọc được nhưng Save (`eq`) báo "không có bản ghi nào được cập nhật". Chủ dự án đã sửa dữ liệu. Bất đối xứng Load `ilike`
  / Save `eq` trong `SupabaseConfigRepository` nên ghi thành pitfall CLAUDE.md khi commit gộp.
- ↪ **Dời sang Phase 7 Bước B** (quyết 2026-09-17 — đặt lệnh thật trên MT5 chân A): trigger auto open với
  `NullCTraderTradeExecutor` → chân B fail `"cTrader chưa được kích hoạt"`, chân A rollback qua `CloseOpenedLegByTimeoutAsync`.

### Gate chung

- [x] `dotnet test`: **675 tổng, 664 pass, 11 fail** — 11 fail **trùng từng tên** baseline memo §2.2b; +22 test mới xanh.
- [x] `dotnet build TradeDesktop.App`: **0 error, 0 warning**.

---

## Rollback

Revert một commit. **Không có state nào bị ghi khác đi** — chưa có field DB mới, chưa có file mới nào
được ghi ra đĩa.

Nếu `platform_b` đã bị đặt thành `ctrader` trong Supabase thì đổi lại về `mt5` trước khi revert (sau
khi revert, `ctrader` sẽ lại fallback về `mt5` — vẫn chạy được, nhưng nên dọn cho sạch).

---

## Cổng sang Phase 2

- [x] Toàn bộ checklist nghiệm thu pass (B1 chỉ review code; smoke đặt lệnh dời Phase 7 Bước B).
- [x] Đã chốt cả 4 câu "Chốt trước khi code" của [Phase 2](phase-2-config.md) — câu 2 (UI 2 mode cho Sàn B) và
      câu 3 (`map_name_2` = hằng `CTRADER_B`, giữ dữ liệu mode ẩn) quyết 2026-09-17; câu 1 và 4 đã quyết trước đó.
