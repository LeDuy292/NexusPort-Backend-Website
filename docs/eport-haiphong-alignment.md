# Tham khảo ePort Hải Phòng

Nguồn: [Hướng dẫn khách hàng ePort 01/2022](https://haiphongport.com.vn/FileUpload/Documents/EPort/1.%20Huong%20dan%20su%20dung%20ePort%20cho%20KH_Full%2020012022.pdf). Chỉ tham khảo bố cục và quy trình, không áp dụng biểu phí của cảng này cho NexusPort.

## Nội dung đã áp dụng

- Đăng ký giao hàng nhập bằng DO/eDO, Bill, tàu/chuyến; hạ hàng xuất theo Booking.
- OTP email, chọn nhiều container, dịch vụ kèm, sao chép dịch vụ và khai báo xe/sà lan.
- Container lạnh nhập bắt buộc cắm điện; khai báo hạn sử dụng để tính phí.
- Kiểm tra phí, lưu giá khi xác nhận, theo dõi xử lý và thanh toán PayOS.
- In/tải PDF phiếu đăng ký toàn đơn hoặc từng container.

## Theo yêu cầu của dự án

- Chỉ có PayOS và không có dịch vụ container rỗng.
- Thông quan dùng nút xác nhận của khách hàng, chưa kết nối VASSCM.
- DO/eDO cần cảng đối chiếu trước duyệt; chưa xác minh tự động với hãng tàu.

## Phần còn cần tích hợp

API eDO hãng tàu, VASSCM, hóa đơn điện tử và kết quả tác nghiệp/EIR từ module phụ trách. Phiếu đăng ký và thanh toán không tự cấp quyền giao nhận hoặc mở cổng.

Xem [quy trình hiện tại](eport-delivery-workflow.md).
