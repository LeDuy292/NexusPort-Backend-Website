# Danh mục dịch vụ và biểu phí

Backend C# tại `csharp-core/src/Modules/ServiceCatalog`; migration `20261108_035_eport_service_catalog.sql`.

## Chức năng

- `/services`: khách tra cứu dịch vụ, điều kiện, biểu phí và dự tính chi phí.
- `/admin/service-catalog`: Administrator khai báo/sửa dịch vụ, thêm/ngừng giá và xem lịch sử.
- Dịch vụ: hạ xuất, giao nhập, cắm điện, đảo chuyển, gia hạn lưu bãi và cân; kiểm tra trạng thái cung cấp trước sử dụng.

## Quy tắc giá

- Giá VND theo kích thước 20/40/45 feet, nhóm DRY/REEFER/DG/OOG và ngày hiệu lực.
- Ưu tiên đúng kích thước + nhóm → đúng kích thước → đúng nhóm → giá chung.
- Không cho giá active cùng phạm vi chồng ngày. Thêm phiên bản giá mới, giữ lịch sử giá cũ.
- Thiếu giá không được hiểu là giá 0. Giá trị dự tính chưa gồm thuế.
- Danh mục dùng chung; chỉ admin được ghi, tài khoản/quyền được kiểm tra trong DB.

## API

Prefix `/api/v1/ServiceCatalog`: `GET /`, `POST /`, `PUT /{id}`, `POST /{id}/tariffs`, `POST /{id}/tariffs/{tariffId}/retire`, `POST /{id}/quote`, `GET /{id}/history`.

Kiểm tra danh mục: `csharp-core/tests/ServiceCatalogChecks`, chỉ chạy với PostgreSQL fixture riêng. Xem [tạo đăng ký](eport-service-drafts.md).
