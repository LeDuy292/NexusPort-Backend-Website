# Gửi đăng ký dịch vụ

Mở hồ sơ nháp → kiểm tra thông tin → tích xác nhận → **Gửi đăng ký**.

- Backend kiểm tra quyền công ty, nguồn, container, dịch vụ, thời gian, OTP và xác nhận thông quan.
- Phía khách không tính giá, không hiển thị bảng phí và không cần có biểu phí để gửi.
- Hồ sơ chuyển `PENDING_APPROVAL`, `pricingStatus=AWAITING`, chưa có số tiền. Nội dung gửi được khóa.
- Phần Dispatcher của thành viên khác xác nhận/tính tiền và trả kết quả. Không triển khai tính phí hoặc màn hình Dispatcher ở phần này.
- Nhận kết quả duyệt hợp lệ và số tiền: mới được thanh toán PayOS. Trả từ chối: xem lý do, không thanh toán.
- Thông báo thanh toán và biên lai sau PayOS được gửi qua email, có trạng thái chờ/gửi lỗi để theo dõi.

API khách: `POST /api/v1/ServiceDrafts/{id}/validate`, `POST /{id}/confirm`, `GET /{id}/registration`. Token kiểm tra giữ tương thích trường `quoteToken`, không còn chốt giá ở phía khách.

Cần migration 042 sau các migration dịch vụ hiện có. Kiểm tra luồng hiện tại tại `csharp-core/tests/ServiceDispatcherContractChecks`; chỉ dùng DB fixture.

Xem [hợp đồng Dispatcher](eport-dispatcher-integration.md).
