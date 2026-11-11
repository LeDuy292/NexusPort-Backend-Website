# Quy trình đăng ký dịch vụ

Backend C# `ServiceCatalog`, frontend React tại `/services`. Phạm vi: hàng nhập, hạ hàng xuất và dịch vụ kèm; không có container rỗng.

## Hai luồng chính

- **Nhập:** DO/eDO, Bill, tàu/chuyến, hạn lệnh → OTP email → chọn container → dịch vụ kèm → xác nhận thông quan → khai báo xe/sà lan → tính phí → đồng ý bảng phí và xác nhận đăng ký.
- **Xuất:** Booking thuộc công ty → OTP email → container đã công bố, VGM/seal và khai báo hàng → dịch vụ kèm → xác nhận thông quan → khai báo giao nhận → tính phí và xác nhận đăng ký.
- Đơn cần duyệt: nhân viên cảng tiếp nhận, duyệt/từ chối; sau duyệt khách thanh toán PayOS và in/tải phiếu.

## Quy tắc cần nhớ

- Chỉ chọn container đúng luồng, đúng công ty và còn đủ điều kiện.
- Bấm **Xác nhận thông quan** để tiếp tục. Đây là xác nhận của khách; đổi chứng từ/container phải xác nhận lại.
- Container lạnh hàng nhập bắt buộc có cắm điện và thời hạn sử dụng.
- Giờ dự kiến phải ở tương lai, trong hạn lệnh và cửa sổ tiếp nhận của Booking.
- Đơn đã xác nhận khóa nội dung và giá; thông tin thiếu vẫn lưu nháp được.
- Chưa tích ô đồng ý bảng phí thì nút xác nhận còn khóa.

## Triển khai

- Áp dụng các migration cần thiết theo thứ tự trong `database/migrations`; chức năng dịch vụ dùng 035, 036, 037, 040 và 041.
- Cấu hình SMTP và PayOS, build rồi khởi động lại API/frontend.
- Chưa kết nối VASSCM, API eDO hãng tàu hoặc nhà cung cấp hóa đơn điện tử. Đăng ký/thanh toán không tự cấp quyền qua cổng.

Chi tiết: [OTP](eport-email-otp.md), [quản lý đơn](eport-service-orders.md), [PayOS](eport-service-payments.md), [phiếu](eport-service-receipts.md).
