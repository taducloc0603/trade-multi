# Fast Leg — kill switch

Tắt sàn C ngay, không cần build lại. Sau khi tắt, engine chạy **y hệt** bản không có Fast Leg
(`snapshot.FastLeg = null`).

## Cách 1 — tắt bằng DB (khuyến nghị)

1. Trên Supabase, sửa đúng dòng của host (thay `<host>`):

   ```sql
   update public.configs
   set fast_leg = jsonb_set(fast_leg, '{enabled}', 'false'::jsonb)
   where lower(hostname) = '<host>';
   ```

   Hoặc xoá hẳn cấu hình: `set fast_leg = null`.
2. Trên app, bấm **Reconnect** (hoặc Stop rồi Start).
3. Kiểm log: dòng `[DB] … | fast_leg=disabled | …` và `[CONFIG] fast_leg=disabled`; dải "Sàn C" biến mất.

## Cách 1b — tắt trong cửa sổ Config

Mở **Config** ⇒ bỏ tick **Bật sàn C (Fast Leg)** ⇒ **Save**. App ghi `enabled=false` vào cột `fast_leg` (các khoá khác lấy theo giá trị đang hiện trên form) và
áp runtime ngay, không cần Reconnect. Nếu thông báo là "Sàn A/B đã lưu, Sàn C lưu thất bại" thì sàn C CHƯA tắt — dùng
Cách 1.

## Cách 2 — chuyển sang chỉ quan sát

Giữ dữ liệu verdict nhưng không chặn lệnh nào:

```sql
update public.configs
set fast_leg = jsonb_set(fast_leg, '{mode}', '"observe"'::jsonb)
where lower(hostname) = '<host>';
```

Rồi bấm **Reconnect**.

## Lưu ý
- Tắt terminal C **không** phải là kill switch: với `failOpen=false`, C chết sẽ **chặn mọi Open** (Close vẫn chạy).
- Bản app cũ (trước Fast Leg) bỏ qua cột `fast_leg`, nên rollback code không cần sửa DB.
- Host live chỉ do chủ dự án thao tác (CLAUDE.md §0.0).
