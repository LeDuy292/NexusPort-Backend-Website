# Kiểm tra hợp đồng gửi/nhận Dispatcher

Kiểm tra hàng nhập/xuất: gửi không tính phí, quyền trả kết quả, khóa số tiền,
PayOS dùng đúng số tiền, email retry và chống ghi nhận lặp. SMTP/PayOS giả lập.
Kiểm tra thêm lịch từng container: gợi ý cặp xe–tài xế, trùng giờ, quyền công ty,
snapshot hồ sơ và xe vừa bận trước khi gửi. Đội xe dùng adapter giả lập; không tạo Booking thật.

Chỉ chạy trên PostgreSQL fixture riêng `127.0.0.1:55449/flow_filter_check`, user
`filter_check`. Đặt `FLOW_FILTER_TEST_CONNECTION` và `FLOW_FILTER_TEST_MARKER`
khớp custom setting `nexusport.filter_test` của cluster. Không dùng DB dự án.

`dotnet run --project csharp-core/tests/ServiceDispatcherContractChecks`
