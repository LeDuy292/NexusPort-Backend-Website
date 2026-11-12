# Quy trình đăng ký dịch vụ

Backend C# `ServiceCatalog`, frontend React tại `/services`. Phạm vi: hàng nhập, hạ hàng xuất và dịch vụ kèm; không có container rỗng.

## Hai luồng chính

- **Nhập:** DO/eDO, Bill, tàu/chuyến, hạn lệnh → OTP email → chọn container → dịch vụ kèm → xác nhận thông quan → khai báo xe/sà lan → gửi Dispatcher.
- **Xuất:** Booking thuộc công ty → OTP email → container đã công bố, VGM/seal và khai báo hàng → dịch vụ kèm → xác nhận thông quan → khai báo giao nhận → gửi Dispatcher.
- Dispatcher do thành viên khác làm xác nhận/tính tiền và trả kết quả. Khách nhận thông báo, thanh toán PayOS rồi nhận biên lai/biểu mẫu qua email.

## Quy tắc cần nhớ

- Chỉ chọn container đúng luồng, đúng công ty và còn đủ điều kiện.
- Bấm **Xác nhận thông quan** để tiếp tục. Đây là xác nhận của khách; đổi chứng từ/container phải xác nhận lại.
- Container lạnh hàng nhập bắt buộc có cắm điện và thời hạn sử dụng.
- Giờ dự kiến phải ở tương lai, trong hạn lệnh và cửa sổ tiếp nhận của Booking.
- Giao nhận bằng xe: mỗi container có giờ bắt đầu/kết thúc và cặp xe–tài xế riêng. Tự gợi ý từ lịch Booking, ưu tiên tài xế phụ trách xe; chặn trùng giờ trong hồ sơ và kiểm tra lại trước khi gửi. Đề xuất chưa giữ lịch vận chuyển; Dispatcher thực hiện phân công chính thức.
- Gửi đăng ký khóa nội dung; số tiền chỉ có sau kết quả Dispatcher. Không tính/hiện phí dự tính phía khách.
- Tích xác nhận thông tin hồ sơ rồi bấm Gửi đăng ký; chưa nhận phí thì chưa được thanh toán.

## Triển khai

- Áp dụng migration theo thứ tự; luồng gửi/nhận Dispatcher cần thêm 042 sau các migration dịch vụ hiện có.
- Cấu hình SMTP và PayOS, build rồi khởi động lại API/frontend.
- Chưa kết nối VASSCM, API eDO hãng tàu hoặc nhà cung cấp hóa đơn điện tử. Đăng ký/thanh toán không tự cấp quyền qua cổng.

Chi tiết: [OTP](eport-email-otp.md), [quản lý đơn](eport-service-orders.md), [PayOS](eport-service-payments.md), [phiếu](eport-service-receipts.md).

Hợp đồng giao cho thành viên làm Dispatcher: [gửi/nhận kết quả](eport-dispatcher-integration.md).
