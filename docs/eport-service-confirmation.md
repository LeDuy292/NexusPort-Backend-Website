# Kiểm tra và xác nhận đăng ký

Mở hồ sơ nháp → **Lưu & kiểm tra** → xem điều kiện/phí → tích đồng ý → **Xác nhận đăng ký**.

## Backend kiểm tra

- Tài khoản active, đúng một công ty; Booking/container đúng nguồn và quyền.
- Dịch vụ còn hoạt động, đúng luồng; container, seal/VGM, giờ dự kiến và hạn lệnh hợp lệ.
- OTP nguồn còn hiệu lực và đã bấm xác nhận thông quan cho đúng hồ sơ/container.
- Có giá hợp lệ cho mọi dòng phí; lỗi chặn xác nhận, cảnh báo thể hiện điều kiện cần cảng xử lý.
- DO/eDO nhập chưa xác minh tự động vẫn được xác nhận đăng ký, nhưng cần cảng đối chiếu trước duyệt/thanh toán.

## Tính phí và lưu đơn

- CONTAINER: 1/container; MOVE/WEIGH: số lượt đăng ký.
- HOUR: thời gian sử dụng làm tròn lên 0,01 giờ; DAY: làm tròn lên mỗi 24 giờ.
- Giá theo nguồn container và ngày áp dụng UTC+7; tổng phí VND chưa gồm thuế.
- Xác nhận lưu snapshot bất biến: nguồn, nội dung, số lượng và giá. Biểu phí mới không đổi đơn cũ.
- Trả `CONFIRMED` hoặc `PENDING_APPROVAL`. Thanh toán được theo dõi riêng.
- Giá/nguồn/phiên bản đổi thì kiểm tra lại; retry hợp lệ không tạo thêm đăng ký.

## API

Prefix `/api/v1/ServiceDrafts`: `POST /{id}/validate`, `POST /{id}/confirm`, `GET /{id}/registration`.

Migration chính: 037; quy trình hiện tại còn cần 040 và 041 cùng các prerequisite. Kiểm tra tại `csharp-core/tests/ServiceRegistrationChecks` trên DB cô lập.

Xem [quản lý đơn](eport-service-orders.md).
