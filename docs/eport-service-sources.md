# Nguồn Booking và dữ liệu phát triển cho dịch vụ e-Port

## Sửa lỗi nguồn 503

Migration `20261109_038_restore_service_booking_source.sql` bổ sung các cột
`carrier_id`, `line_user_id`, `request_id`, `request_hash` bị thiếu trong bảng
`export_bookings`. Các cột mới cho phép NULL để giữ nguyên bản ghi cũ; không đoán
công ty từ người tạo hoặc tên chủ hàng. Booking cũ chưa xác định quyền vẫn không
được đưa vào danh sách của công ty. Dữ liệu mới có liên kết công ty/hãng tàu rõ ràng.

Không thay hai file appsettings, không sửa migration đã áp dụng và không đổi
module Booking, IoT hoặc trạng thái vật lý của container đang hoạt động.

## Áp dụng và dữ liệu mẫu

Runner đọc đúng cấu hình C# development trong bộ nhớ, đối chiếu với database
phát triển cấu hình trong `.env`; không xuất hoặc ghi lại mật khẩu kết nối.

```powershell
# Chỉ kiểm tra
powershell -NoProfile -ExecutionPolicy Bypass -File database/scripts/apply-eport-service-sources.ps1

# Áp dụng migration và kiểm thử INSERT mẫu bằng transaction ROLLBACK
powershell -NoProfile -ExecutionPolicy Bypass -File database/scripts/apply-eport-service-sources.ps1 -Apply -ValidateSamples

# Thêm dữ liệu mẫu vào database development theo yêu cầu rõ ràng
powershell -NoProfile -ExecutionPolicy Bypass -File database/scripts/apply-eport-service-sources.ps1 -Apply -SeedSamples
```

Runner kiểm tra migration còn thiếu theo thứ tự, chặn môi trường khác development
khi seed và kiểm tra hash của tất cả bản ghi cũ trước/sau. Bản ghi nguồn chỉ thêm
mới; file seed có ID cố định, kiểm tra va chạm và không tự kéo dài thời hạn khi
chạy lại. Có thể kiểm tra chạy lặp bằng `-Apply -ValidateSamples` sau khi đã seed.

Bộ dữ liệu hiện tại gồm 3 chuyến Scheduled, 3 Booking cho mỗi công ty hợp lệ và
2 container có hàng cho mỗi Booking. Với hai công ty hiện có: **6 Booking, 12
container, 12 khai báo hàng và 12 dòng container công bố**. Các tài khoản hãng tàu
đã có phải đang active và gắn hồ sơ `shipping_lines`; không tạo hoặc reset tài khoản.

| Booking | Container |
| --- | --- |
| `DEMO-EPORT-<công ty>-001` | Khô 20 feet và 40 feet |
| `DEMO-EPORT-<công ty>-002` | Lạnh 20 feet và 40 feet, có nhiệt độ/kW |
| `DEMO-EPORT-<công ty>-003` | Hàng nguy hiểm 20 feet và khô 45 feet |

Số container NXPU có check digit ISO 6346; seal/VGM và declaration liên kết đúng
container/công ty. Cửa sổ tiếp nhận bắt đầu từ hôm trước; CY còn 30/45/60 ngày
tính tại lần thêm đầu tiên. Không giả lập đã vào bãi, thông quan hoặc qua cổng.

Các bản giá DEMO chỉ được thêm khi chưa có phạm vi giá phù hợp, không sửa giá
đang tồn tại. Giá tham khảo: hạ xuất 20/40/45 feet là 300.000/500.000/600.000 VND,
điện lạnh 1.000 VND/giờ, đảo chuyển 20.000 VND/lượt, lưu bãi 7.000 VND/ngày.
Đây là **dữ liệu thử nghiệm, không phải biểu phí chính thức của cảng**; số tiền
đăng ký chưa gồm thuế. Ghi chú DEMO và audit được lưu cùng các bản giá này.

## Thử trên UI

Khởi động lại Vite sau khi chuyển Tailwind từ CDN sang build cục bộ, rồi tải lại
`/services`. Đăng nhập tài khoản công ty hiện có như `carrier01` hoặc
`dauboquay`/`dauboquay1`/`dauboquay5`.

Chọn **Tạo đăng ký → Hạ container xuất → Chọn Booking**, tìm `DEMO-EPORT` và chọn
container. Các bản ghi này là nguồn database thật, không cần bấm **Nạp dữ liệu mẫu**
(nút đó chỉ mở bản xem thử UI không cho lưu). Nhập người liên hệ, điện thoại và
thời gian dự kiến trong cửa sổ CY, chọn **Lưu & kiểm tra** để xem phí và xác nhận.
Dịch vụ kèm ở bãi có thể chuyển **Chờ cảng duyệt**; thanh toán được theo dõi riêng.

## Kiểm chứng

`csharp-core/tests/ServiceSourceRepairChecks` tái hiện schema mất liên kết công ty
trên PostgreSQL riêng `127.0.0.1:55446/eport_source_repair_check`, bắt buộc marker
`nexusport.source_repair_test`. Kiểm tra 503 trước sửa, áp dụng 038 hai lần,
giữ nguyên bản ghi cũ, FK/request duy nhất, tenant và luồng draft → fee → confirm.

Seed được kiểm tra trực tiếp với schema development bằng transaction rollback
trước khi chạy commit. Không dùng fixture của checker trên database dự án.

Tailwind 3.4 dùng PostCSS, theme và plugins forms/container queries giữ trong
frontend; không còn phụ thuộc Play CDN. Xem [hướng dẫn Vite của Tailwind v3](https://v3.tailwindcss.com/docs/guides/vite).
Thông báo React DevTools khi chạy development là thông tin của React.
