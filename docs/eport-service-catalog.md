# Người 3 — danh mục dịch vụ và biểu phí

Backend của phần này dùng C#/.NET 8 trong `csharp-core/src/Modules/ServiceCatalog`. Không có mô-đun Node cho danh mục dịch vụ.

Phần 2 đăng ký và lưu nháp theo container được mô tả riêng tại
[eport-service-drafts.md](eport-service-drafts.md).

## Phạm vi

- Danh mục, mô tả, điều kiện tham khảo, luồng hàng, đơn vị tính và trạng thái cung cấp.
- Biểu phí VND theo kích thước, nhóm container, ngày bắt đầu/kết thúc và ghi chú.
- Dự tính theo số đơn vị, lịch sử thay đổi, chống ghi đè phiên bản và chồng ngày.
- Chưa tạo lệnh dịch vụ, giữ container, đặt lịch xe, thu tiền, phát hành hóa đơn hoặc xác nhận tác nghiệp. Những chức năng đó thuộc các phần tiếp theo.

## UI và phân quyền

- `/services`: Transport Company, Carrier Staff và Administrator. Tra cứu, lọc, đọc điều kiện/biểu phí và dự tính.
- `/admin/service-catalog`: Administrator. Thêm/chỉnh sửa dịch vụ, thêm biểu phí, ngừng áp dụng giá, xem lịch sử.
- UI có trạng thái tải, lỗi, trống, chưa có giá; hỗ trợ desktop/mobile và bàn phím. Menu mobile chỉ áp dụng cho hai trang này.
- API kiểm tra JWT, role và tài khoản còn active/role còn phù hợp trong DB. Danh mục là cấu hình dùng chung; chỉ admin ghi được.

## API C#

Prefix `/api/v1/ServiceCatalog`:

| HTTP | Đường dẫn | Quyền |
| --- | --- | --- |
| GET | `/` | Khách hàng/admin |
| POST | `/` | Admin, tạo dịch vụ |
| PUT | `/{id}` | Admin, cập nhật với `expectedVersion` |
| POST | `/{id}/tariffs` | Admin, thêm bản giá |
| POST | `/{id}/tariffs/{tariffId}/retire` | Admin, ngừng áp dụng, giữ lịch sử |
| POST | `/{id}/quote` | Khách hàng/admin, chỉ dự tính |
| GET | `/{id}/history` | Admin |

Ngày hiệu lực có dạng `yyyy-MM-dd`, bao gồm cả ngày kết thúc. Ngày hiện tại lấy theo UTC+7. Giá là số nguyên VND không âm; 0 phải được khai báo rõ. Thiếu giá trả `UNPRICED` với total null; tạm ngừng trả `SUSPENDED`.

Giá phù hợp được ưu tiên: đúng kích thước và nhóm → đúng kích thước → đúng nhóm → giá chung. Không cho hai giá active cùng cặp kích thước/nhóm chồng ngày. Row lock trên dịch vụ bảo vệ khi thêm giá đồng thời qua API.

`quote` nhận `{size,equipment,quantity}`. Size: 20FT/40FT/45FT. Nhóm: DRY/REEFER/DG/OOG. Đơn vị HOUR cho phép hai chữ số thập phân; các đơn vị khác là số nguyên. Tổng dự tính chưa bao gồm thuế và các khoản khác; không thay thế kiểm tra điều kiện đặt lệnh.

Điều kiện trong danh mục là mô tả để người dùng tra cứu. Phần tạo lệnh sau này phải đối chiếu nguồn Booking/e-D/O, quyền công ty, trạng thái bãi và hold phía server.

## Database

Migration additive `20261108_035_eport_service_catalog.sql` tạo ba bảng riêng `eport_services`, `eport_tariffs`, `eport_catalog_audit`. Không đổi bảng nghiệp vụ cũ.

Sáu dịch vụ tham chiếu: hạ xuất, lấy nhập, điện lạnh, đảo chuyển, gia hạn, cân. Cân tạm ngừng mặc định vì là tùy chọn. Không seed giá, đơn hàng, khách hàng hoặc dữ liệu thanh toán. Giá cần được Administrator khai báo theo quyết định của nhóm/cảng.

Các bản giá không sửa số tiền tại chỗ: thêm bản mới, ngừng bản cũ khi cần. Lịch sử và audit được giữ. Đơn vị dịch vụ không đổi sau khi đã có giá. Lỗi phiên bản và chồng ngày trả HTTP 409.

Triển khai cần apply migration 035 và khởi động lại C# để nạp project/controller mới. Vite đã proxy `/api/v1` về C#; không thay proxy, appsettings hay các API khác.

## Kiểm chứng

- Build API và frontend.
- `csharp-core/tests/ServiceCatalogChecks` kiểm tra thật trên PostgreSQL riêng: migration idempotent, quyền admin/khách, validation, giá theo phạm vi, ngày hiệu lực, xung đột phiên bản, ngừng giá idempotent, giá 0 và thêm giá đồng thời.
- Checker từ chối mọi DB ngoài `127.0.0.1:55443/eport_catalog_check`, user `catalog_check`, và phải có custom setting `nexusport.catalog_test` trùng `CATALOG_TEST_MARKER`.
- Truyền connection qua `CATALOG_TEST_CONNECTION`, marker qua `CATALOG_TEST_MARKER`, chạy `dotnet run --project csharp-core/tests/ServiceCatalogChecks` trên cluster mới riêng. Không dùng DB chia sẻ. `--serve` phục vụ controller C# riêng trên 5508 để browser QA; secret trong checker chỉ dành cho fixture cục bộ.
- Browser QA cục bộ kiểm tra role, tìm/lọc, dự tính, lỗi giá chồng ngày, lưu giá, lịch sử và menu/mobile 390px. Giá trong ảnh QA là fixture tại DB riêng, không phải biểu phí vận hành.
