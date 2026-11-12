# Tạo và lưu nháp đăng ký

Tại `/services`, chọn giao hàng nhập hoặc hạ hàng xuất. Có thể lưu nháp và mở lại trong công ty trước khi xác nhận.

## Thông tin chính

- Nguồn: Booking xuất hoặc DO/eDO, Bill, tàu/chuyến và hạn lệnh nhập.
- OTP email, container, dịch vụ chính/kèm, thời gian sử dụng.
- Xác nhận thông quan, xe/tài xế hoặc sà lan, liên hệ và thông tin nhận hóa đơn.
- Tối đa 50 container, 12 dịch vụ/container; hỗ trợ sao chép dịch vụ.

## Quy tắc

- Xuất chỉ chọn container có hàng đã công bố trong Booking của công ty.
- Nhập chỉ chọn lượt dỡ tàu đã xác minh, active, đã bắt đầu, chưa kết thúc/hết hạn và có quyền công ty.
- Đổi nguồn/container phải xác nhận thông quan lại; đổi nguồn phải xác thực email lại.
- Cập nhật cần `expectedVersion` hiện tại; xung đột trả 409.
- Bản nháp chưa giữ container, chốt phí hay tạo thanh toán. Sau xác nhận, nội dung bị khóa.
- Mẫu xem thử trên UI không cho lưu; để chạy thật dùng [mẫu DB](eport-import-demo.md).

## API

Prefix `/api/v1/ServiceDrafts`: `GET /sources`, `GET /containers`, `GET /`, `GET /{id}`, `PUT /{id}`. Các API nguồn nhận `flow`, xuất thêm `sourceId` khi chọn container.

Migration chính: 036 cùng prerequisite. Kiểm tra tại `csharp-core/tests/ServiceDraftChecks`, chỉ dùng DB cô lập.

Xem [quy trình](eport-delivery-workflow.md) và [xác nhận đăng ký](eport-service-confirmation.md).
