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

- **Phase 0 phải kết luận GO.** Nếu câu hỏi R1 (đóng bằng tag 721) thất bại thì hướng tiếp cận đổi và
  công sức phase này có thể bỏ đi.
- Con số baseline test từ Phase 0 làm gate.

---

## Chốt trước khi code

| # | Câu hỏi | Đề xuất |
|---|---|---|
| 1 | `platform_a = ctrader` bị chặn ở đâu — chỉ ẩn UI, hay reject cứng ở tầng save config? | **Reject cứng ở `ConfigService`**. Ẩn UI thôi là không đủ: config có thể được sửa trực tiếp trong Supabase. |
| 2 | Khi `platform_b = ctrader` mà chưa có executor thật, lệnh B nên fail thế nào? | `Success=false` + `Detail = "cTrader chưa được kích hoạt"`. **Không throw** — throw sẽ đi qua đường exception chưa được kiểm chứng của router. |

---

## Việc làm

### R7 — sửa cả **3** bản sao `NormalizePlatform` trong cùng một commit

Ba bản hiện giống hệt nhau và đều fallback unknown → `"mt5"`:

| File | Dòng |
|---|---|
| `TradeDesktop.Application/Services/ConfigService.cs` | `:25` |
| `TradeDesktop.Application/Services/ConfigService.cs` | `:488` |
| `TradeDesktop.App/State/RuntimeConfigState.cs` | `:460` |

```csharp
// hiện tại — cả 3 chỗ
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

**R7** (chủ đạo — 3 bản sao normalize) · §4.1 (không đụng `TryRefreshCloseRows`)

---

## Nghiệm thu

### Unit test (macOS)

- [ ] **Test parity**: hai `NormalizePlatform` (`ConfigService` và `RuntimeConfigState`) nhận **cùng
      tập** `{mt4, mt5, ctrader}` và cùng fallback unknown → `mt5`.
      Test này tồn tại để bắt đúng R7 — đặt tên rõ, ví dụ `NormalizePlatform_BothCopiesAcceptSameSet`.
- [ ] Test: `SaveByMachineHostNameAsync` với `platformA = "ctrader"` trả `Failed`.
- [ ] Test: `platformB = "ctrader"` được chấp nhận và round-trip nguyên vẹn.
- [ ] Test: `"CTRADER"`, `" ctrader "` đều normalize về `"ctrader"`.

### Ma trận platform

Xem [README — Ma trận platform được hỗ trợ](README.md). Phase này test được **toàn bộ** ở tầng config
bằng unit test:

| `platform_a` | `platform_b` | Kỳ vọng | ☐ |
|---|---|---|---|
| mt4 | mt4 | Save OK, resolve ra `(Mt4, Mt4)` | ☐ |
| mt4 | mt5 | Save OK, resolve ra `(Mt4, Mt5)` | ☐ |
| mt5 | mt4 | Save OK, resolve ra `(Mt5, Mt4)` | ☐ |
| mt5 | mt5 | Save OK, resolve ra `(Mt5, Mt5)` | ☐ |
| mt4 | ctrader | Save OK, resolve ra `(Mt4, CTrader)` | ☐ |
| mt5 | ctrader | Save OK, resolve ra `(Mt5, CTrader)` | ☐ |
| ctrader | bất kỳ | **Save bị reject** | ☐ |
| (bypass config) A = CTrader tại dispatch | | **`ValidatePlatformOrThrow` throw** (B1) | ☐ |

### Smoke test (Windows)

- [ ] App chạy y hệt như trước với `mt4`/`mt5` — không có log mới, không có hành vi khác.
- [ ] Chạy thử **cả 4 tổ hợp MT-MT** (mt4/mt4, mt4/mt5, mt5/mt4, mt5/mt5) — không hồi quy.
- [ ] Đặt `platform_b = ctrader` thủ công trong Supabase → app **không crash**, khởi động bình thường.
- [ ] Trigger một auto open → chân B fail với `"cTrader chưa được kích hoạt"`, chân A rollback đúng
      qua `CloseOpenedLegByTimeoutAsync`.
- [ ] Không có exception chưa bắt nào trong log.

### Gate chung

- [ ] `DOTNET_ROLL_FORWARD=Major dotnet test TradeDesktop.Tests/TradeDesktop.Tests.csproj` không tăng
      so với baseline Phase 0.
- [ ] Build sạch, không warning mới.

---

## Rollback

Revert một commit. **Không có state nào bị ghi khác đi** — chưa có field DB mới, chưa có file mới nào
được ghi ra đĩa.

Nếu `platform_b` đã bị đặt thành `ctrader` trong Supabase thì đổi lại về `mt5` trước khi revert (sau
khi revert, `ctrader` sẽ lại fallback về `mt5` — vẫn chạy được, nhưng nên dọn cho sạch).

---

## Cổng sang Phase 2

- [ ] Toàn bộ checklist nghiệm thu pass.
- [ ] Đã chốt các câu hỏi còn mở ở "Chốt trước khi code" của [Phase 2](phase-2-config.md): câu 2
      (layout section cTrader) và câu 3 (`map_name_2` khi B là cTrader). Câu 1 (password → ô masked,
      lưu `sans_json`) và câu 4 (từ chối Save khi còn slot) **đã quyết**.
