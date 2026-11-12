# Gửi đăng ký và nhận kết quả Dispatcher

Phần này chỉ làm phía khách: gửi hồ sơ, nhận số tiền, thanh toán và nhận email. Việc xác nhận/tính tiền và giao diện Dispatcher do thành viên khác triển khai.

## Gửi hồ sơ

- Khách lưu nháp, xác thực email, xác nhận thông quan rồi gửi đăng ký.
- `POST /api/v1/ServiceDrafts/{id}/confirm` tạo đơn `PENDING_APPROVAL`, `pricingStatus=AWAITING`; không tính giá, không yêu cầu có biểu phí.
- Phía Dispatcher đọc `GET /api/v1/ServiceOrders?status=PENDING_APPROVAL` và `GET /api/v1/ServiceOrders/{id}` bằng JWT role Dispatcher. Chi tiết có nguồn, container, dịch vụ và khai báo giao nhận.
- Phân công đề xuất nằm trong `content.delivery.trips[]`: `sourceContainerId`, `appointmentStart/End` (UTC), `truckId`, `driverId`, `vehicle` và `driver` là thông tin do backend lấy từ hồ sơ công ty. Mỗi container một chuyến; Dispatcher kiểm tra lại và giữ lịch trong phần điều phối của mình.

## Nhận kết quả từ phần của Dispatcher

`POST /api/v1/ServiceOrders/{id}/billing-result`, JWT role Dispatcher/Administrator/Admin:

```json
{
  "requestId": "11111111-1111-4111-8111-111111111111",
  "expectedVersion": 3,
  "status": "APPROVED",
  "reference": "PHIEU-PHI-001",
  "total": 345678,
  "currency": "VND",
  "sourceDocumentsChecked": true,
  "sourceEvidenceReference": "DO-DA-DOI-CHIEU"
}
```

- Dùng `version` mới nhất của đơn; mỗi kết quả dùng UUID `requestId` riêng. Retry cùng nội dung không ghi nhận trùng, thay đổi nội dung trả 409.
- `total` do module Dispatcher cung cấp, là số nguyên VND không âm. Phía khách không được gọi endpoint này hoặc tự sửa số tiền.
- Hàng nhập cần xác nhận đã đối chiếu DO/eDO và căn cứ. Nếu từ chối: `status=REJECTED`, thêm `reason`, không cần total.
- Nhận duyệt hợp lệ: đơn `APPROVED`, tiền từ kết quả được khóa; mới được tạo PayOS. Số tiền 0 không tạo giao dịch.
- Hàng đợi gửi thông báo phí tới email tài khoản đăng ký. Sau PayOS xác minh thành công, gửi biên lai kèm biểu mẫu HTML có thể mở/in.

Migration: `20261112_042_eport_dispatcher_billing.sql`. Không chuyển đổi phí/đơn cũ. SMTP dùng `EmailSettings`; chưa kết nối thật với phần Dispatcher cho tới khi bên đó gọi API trả kết quả.
