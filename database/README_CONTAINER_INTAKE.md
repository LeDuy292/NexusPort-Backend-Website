# NXP-038 Container intake/import

## Phạm vi giai đoạn 1

- Một `containers` record là Container Master, định danh duy nhất bằng `container_no`.
- Một `container_visits` record là một lượt dự kiến hoặc thực tế của Container tại cảng.
- Hai nguồn nhập dùng chung validation/service:
  - `port_vessel`: dữ liệu từ cảng hoặc danh sách tàu, được đánh dấu `verified`.
  - `transport_company`: dữ liệu do công ty vận chuyển khai báo, được đánh dấu `pending_verification`.
- File import và kết quả từng dòng được lưu tại `container_import_batches` và `container_import_rows`.
- Giai đoạn này không phát hành hoặc xử lý EIR.

## Chạy migration

Áp dụng sau schema NexusPort gốc:

```text
database/migrations/20261002_002_container_intake_import.sql
```

Migration chỉ thêm bảng/cột/index và có thể chạy lại an toàn.

## API Node

- `POST /api/v1/containers/intake/port/manual`
- `POST /api/v1/containers/intake/port/import`
- `POST /api/v1/containers/intake/transport/manual`
- `POST /api/v1/containers/intake/transport/import`
- `GET /api/v1/containers/intake/imports`

Import dùng `multipart/form-data`, field file là `file`, tối đa 5 MB và 1.000 dòng.
File mẫu nằm tại `public/templates/NXP-038_Container_Import_Template.xlsx` của frontend.
