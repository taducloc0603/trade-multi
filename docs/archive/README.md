# docs/archive — tài liệu lịch sử

Tài liệu ở đây mô tả công việc **đã hoàn thành và đã lên production**. Giữ lại để tra cứu *vì sao*
một cơ chế được thiết kế như hiện tại, **không** phải để thực thi lại.

> **Muốn biết hành vi hiện hành, đọc `README.md` và `CLAUDE.md` ở gốc repo** — hai file đó mới là
> nguồn sự thật. Tài liệu trong thư mục này có thể mô tả trạng thái trung gian đã bị thay thế.

## Nội dung

| File | Mô tả | Bằng chứng đã hoàn thành |
|---|---|---|
| `PLAN-GAP-STABILITY-OPEN-CLOSE.md` | Kế hoạch triển khai Gap Stability cho Open và Normal Close | 12 cột `open_gap_*` / `close_gap_*` tồn tại thật trong bảng `configs`; code có `GapStabilityConfig`, `GapStabilityElements`; ship qua `b717c35`, `caeafef` |
| `PLAN-GAP-STABILITY-DIAGNOSTICS-TRIAL.md` | Kế hoạch diagnostics tạm thời phục vụ hiệu chỉnh 12 tham số | Toolkit đã ship: `tools/analyze-gap-stability-logs.sh` |
| `GAP-STABILITY-CONFIG-MIGRATION.sql` | Migration tạo 12 cột Gap Stability | Đã áp dụng — kiểm bằng `select *` trên `configs`, các cột đều có |

## Những thứ liên quan **không** nằm ở đây

| File | Ở đâu | Vì sao giữ ngoài archive |
|---|---|---|
| `docs/GAP-STABILITY-TRIAL-RUNBOOK.md` | `docs/` | **Quy trình vận hành còn dùng được** — hướng dẫn cô lập tham số, thu thập dữ liệu, đọc báo cáo khi cần hiệu chỉnh lại 12 tham số |
| `docs/CAC-TRUONG-HOP-GAP-VA-KET-QUA.docx` | `docs/` | **Đặc tả nghiệp vụ** mà `PLAN-GAP-STABILITY-OPEN-CLOSE.md` tham chiếu — mô tả hành vi đang chạy, không phải kế hoạch |

## Lưu ý về đường dẫn cũ bên trong các file này

Các file trên có nhắc đường dẫn kiểu `docs/GAP-STABILITY-CONFIG-MIGRATION.sql` hoặc
`docs/PLAN-GAP-STABILITY-OPEN-CLOSE.md` — tức **đường dẫn trước khi chuyển vào archive**.

Những dòng đó nằm trong mục *"Nhật ký quyết định"* / *"File đã thay đổi"*, là **bản ghi lịch sử về
việc gì đã xảy ra tại thời điểm đó**. Cố ý **không sửa** để không làm sai lệch bản ghi. Đường dẫn
đúng hiện tại là `docs/archive/<tên file>`.
