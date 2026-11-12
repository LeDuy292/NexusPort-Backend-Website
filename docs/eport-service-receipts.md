# Phiếu đăng ký dịch vụ

## Sử dụng

- Mở đơn đã xác nhận → **Phiếu đăng ký**.
- Chọn toàn đơn hoặc từng container; xem A4, in hoặc tải PDF do C# tạo.
- Nếu trạng thái đơn thay đổi khi xuất PDF, cập nhật bản xem trước rồi tải lại.

## Nội dung và quy tắc

- Gồm mã đơn, công ty, hồ sơ nguồn, container, giao nhận, dòng phí và trạng thái xử lý/thanh toán.
- Giá lấy từ snapshot đã xác nhận; giờ hiển thị UTC+7, tiền chưa gồm thuế.
- Phiếu từng container chỉ tính phí của container đó, ghi riêng tổng và trạng thái toàn đơn.
- Đơn hủy/từ chối hiển thị phiếu lịch sử; bản nháp chưa xác nhận không xuất phiếu.
- Khách chỉ xem phiếu của công ty mình; nhân viên cảng xem theo quyền.
- Phiếu không thay thế hóa đơn, EIR hoặc giấy phép qua cổng.

## API

- `GET /api/v1/ServiceOrders/{id}/receipt`
- `GET /api/v1/ServiceOrders/{id}/receipt.pdf?expectedVersion={orderVersion}`
- Thêm `sourceContainerId` để chọn phiếu từng container.

PDF dùng PDFsharp-MigraDoc và font Noto Sans nhúng trong module. Giữ `Assets/Fonts` cùng giấy phép khi đưa code lên Git.
