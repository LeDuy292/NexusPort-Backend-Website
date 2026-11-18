# Thanh toán PayOS

Chỉ thanh toán số tiền do phần Dispatcher trả về sau xác nhận. Phía khách không tính phí; PayOS nhận đúng số tiền VND đã lưu từ kết quả đó.

## Cấu hình

1. Đặt `PAYOS_CLIENT_ID`, `PAYOS_API_KEY`, `PAYOS_CHECKSUM_KEY` của kênh PayOS vào môi trường tiến trình API; không đưa khóa vào Git.
2. Đặt `PAYOS_RETURN_URL`, `PAYOS_CANCEL_URL` về frontend; HTTPS khi triển khai, localhost HTTP chỉ dùng Development.
3. Cấu hình webhook của kênh PayOS trỏ đến `https://<backend>/api/v1/ServiceOrders/payments/payos/webhook`. Backend phải có HTTPS công khai; đổi URL tunnel thì cập nhật webhook.
4. Khởi động lại API để nhận cấu hình. `.env.eport-payments.example` là mẫu tham khảo; API không tự nạp `.env.eport-payments.local`. Nếu dùng `csharp-core/run.ps1`, các biến có thể đặt trong `.env` của backend.

## Sử dụng và xử lý kết quả

- Mở đơn → Thanh toán → tạo liên kết PayOS → thực hiện thanh toán tại cổng.
- Chỉ ghi `PAID` sau webhook có chữ ký hợp lệ hoặc backend đồng bộ xác minh đúng mã/số tiền; redirect frontend không chứng minh đã trả tiền.
- Timeout: giữ yêu cầu hiện tại và đồng bộ, không tạo link mới ngay.
- Yêu cầu đã xác minh hết hạn/hủy và chưa trả tiền mới được thử lại. Hủy link không hủy đơn dịch vụ.
- Callback lặp không ghi nhận trùng; trả thiếu/thừa hoặc tiền về muộn cần đối soát.
- Đơn 0 đồng không tạo thanh toán. Chưa có luồng hoàn tiền.

## API

Prefix `/api/v1/ServiceOrders/{id}`: `GET /payments`, `POST /payments`, `POST /payments/{paymentId}/sync`, `POST /payments/{paymentId}/cancel`.

Thao tác gửi UUID `requestId`, `expectedVersion` của workflow và lý do khi hủy. Khóa và webhook phải được cấu hình trước khi tạo giao dịch thật.

## Mức tiền PayOS trong Development

- Tổng phí đơn (`order.total`) luôn giữ nguyên số Dispatcher trả về và tiếp tục hiển thị trên hồ sơ.
- Để tạo QR nhận đúng 2.000 VND khi phát triển, đặt `ASPNETCORE_ENVIRONMENT=Development` và `NEXUSPORT_EPORT_PAYOS_AMOUNT=2000` trong môi trường chạy API. Bỏ trống biến thứ hai để PayOS nhận đủ tổng phí.
- Production bỏ qua biến ghi đè và luôn dùng toàn bộ số tiền đơn. Webhook có chữ ký hợp lệ chỉ được chấp nhận khi mã đơn, link và số tiền khớp chính xác lần thanh toán đã lưu.
- Biên lai ghi riêng tổng phí đơn và số tiền giao dịch PayOS đã nhận. Nội dung chuyển khoản là `DV` cùng 7 chữ số mã giao dịch (9 ký tự ASCII).
- QR đã tạo trước khi bật cấu hình 2.000 VND giữ nguyên số tiền cũ; hãy hủy yêu cầu đang chờ rồi tạo yêu cầu mới.
