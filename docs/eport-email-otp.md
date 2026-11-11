# Xác thực email OTP

- API dùng `EmailSettings`: SMTP host/port, tài khoản, App Password, người gửi và SSL.
- Người nhận là `users.email` của tài khoản đăng nhập; không phải email người gửi.
- Gmail dùng App Password. Không lưu mật khẩu vào Git hoặc gửi qua chat.
- Mã gồm 6 số, hiệu lực 5 phút, tối đa 5 lần nhập. Gửi cách nhau 60 giây, tối đa 10 lần/giờ.
- Xác thực nguồn có hiệu lực 30 phút; đổi Booking/DO/eDO cần xác thực lại.
- Mở lại màn hình sẽ dùng yêu cầu OTP còn hiệu lực, không tự gửi mã mới.

## Kiểm tra gửi mã

Cấu hình `EmailSettings` trên API, khởi động lại backend rồi đăng nhập và gửi OTP trong màn hình đăng ký dịch vụ. Kiểm tra Inbox/Spam của email tài khoản. Có thể dùng biến môi trường `EmailSettings__SmtpPass` để cấu hình mật khẩu cho tiến trình API.

## Lỗi thường gặp

- **429:** đang trong thời gian chờ hoặc vượt giới hạn gửi.
- **422:** mã sai, hết hạn hoặc bị khóa; nhập mã mới nhất trong email.
- **503:** thiếu cấu hình, gửi SMTP thất bại hoặc quá thời gian chờ. Kiểm tra App Password, kết nối và Spam.

API không trả/ghi log mã OTP; SMTP lỗi không được báo gửi thành công.
