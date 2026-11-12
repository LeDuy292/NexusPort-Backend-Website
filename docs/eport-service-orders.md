# Theo dõi đơn dịch vụ

Trang `/service-orders`: khách tra cứu đơn công ty mình, xem trạng thái, kết quả trả về, thanh toán và email chứng từ.

| Trạng thái | Ý nghĩa |
| --- | --- |
| DRAFT | Có thể sửa và gửi đăng ký |
| PENDING_APPROVAL / RECEIVED | Đang chờ Dispatcher, chưa được thanh toán |
| APPROVED + PUBLISHED | Đã nhận kết quả và số tiền, có thể thanh toán |
| REJECTED / CANCELLED | Hồ sơ đã đóng; xem lý do trong lịch sử |

- Khách không tính phí hoặc tự đặt số tiền; giao diện đăng ký không có bảng phí dự tính.
- Tài khoản công ty chỉ xem đơn của mình. Phía Dispatcher dùng quyền riêng để đọc hồ sơ đã gửi và trả kết quả.
- Khi nhận số tiền hợp lệ, hệ thống gửi thông báo email. Khi PayOS xác minh trả tiền, gửi biên lai/biểu mẫu.
- Đang thanh toán hoặc đã trả tiền thì chặn hủy; số tiền 0 đã được phía xử lý xác nhận thì không tạo giao dịch.
- Đơn cũ giữ lịch sử/giá cũ, không tự chuyển đổi sang luồng mới.

API: `GET /api/v1/ServiceOrders`, `GET /{id}`, `GET /{id}/history`. Điểm nhận kết quả: `POST /{id}/billing-result`, khách không có quyền gọi.

Xem [hợp đồng kết nối](eport-dispatcher-integration.md) và [PayOS](eport-service-payments.md).
