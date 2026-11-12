# Nguồn dữ liệu đăng ký dịch vụ

## Hàng xuất

- Chọn Booking đúng công ty, được cảng tiếp nhận và đã công bố container.
- Container có hàng, còn active/chờ hạ; chuyến còn CY cut-off, có VGM và seal hợp lệ.
- Booking giấy phải khớp nguồn có sẵn. Không đoán quyền từ tên chủ hàng hoặc người tạo.

## Hàng nhập

- Lượt `vessel_discharge`, `verified`, active, đã vào bãi, chưa hoàn tất và còn hạn.
- Có declaration hàng đầy và quyền công ty với đúng lượt đó.
- DO/eDO là khai báo của khách; cảng đối chiếu chứng từ trước duyệt.
- Bộ lọc nguồn được kiểm tra lại ở backend khi lưu/xác nhận, không chỉ ẩn trên UI.

## Khi không thấy dữ liệu

1. Kiểm tra tài khoản/công ty và DB backend đang kết nối.
2. Kiểm tra trạng thái Booking/lượt bãi, VGM/seal, hạn và quyền truy cập.
3. Lỗi 503 do thiếu schema: đối chiếu cấu trúc nguồn với migration đang có; không sửa migration đã áp dụng hoặc tự gán công ty cho dữ liệu cũ.

## Dữ liệu Development

- Booking xuất mẫu: `DEMO-EPORT-<công ty>-001/002/003`, mỗi Booking 2 container khô/lạnh/nguy hiểm tùy bộ.
- SQL xuất: `database/samples/20261109_eport_service_sources.sql`; chỉ chạy có chủ đích trên Development sau khi kiểm tra prerequisite.
- Mẫu trong DB dùng được với luồng thật; chế độ xem mẫu UI không cho lưu.
- Hàng nhập xem [dữ liệu mẫu](eport-import-demo.md). Giá DEMO không phải biểu phí cảng chính thức.

Kiểm tra dịch vụ còn giữ trong `ServiceCatalogChecks`, `ServiceDraftChecks`, `ServiceRegistrationChecks`; chỉ chạy fixture trên DB cô lập.
