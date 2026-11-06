# Booking xuất khẩu của Transport Company

Luồng FCL dựa trên tài liệu nghiệp vụ bước 7–13. API mới nằm trong module
Booking C#, tại `/api/v1/Booking/export`. Giao diện khách hàng đọc/ghi nguồn này;
không dùng dữ liệu mẫu hay cập nhật thành công chỉ trong bộ nhớ.

## Phạm vi nhánh Transport Company

Giao diện hãng tàu và module cổng được giữ nguyên theo HEAD. Nhánh này chỉ thay
đổi Booking khách hàng, API Booking liên quan và schema lưu hồ sơ. Các endpoint
duyệt/trao đổi trạng thái trong Booking là hợp đồng tích hợp cho các role khác;
giao diện hãng tàu hiện chưa được nối vào các endpoint này ở nhánh khách hàng.
Phần cổng cần được nhóm phụ trách tích hợp đối chiếu thông quan, thời hạn và
seal quan sát thực tế trước khi vận hành các lịch xuất liên kết. Guard SQL đã
áp dụng vẫn giữ điều kiện này; không bỏ guard để mô phỏng qua cổng thành công.
Các bước hãng tàu/cảng bên dưới mô tả hợp đồng nghiệp vụ, không có nghĩa các
màn hình tương ứng đã được phát triển trong phạm vi Transport Company.

## Phân biệt dữ liệu và quyền

- `export_bookings`: đặt chỗ vận chuyển bằng tàu, thuộc công ty khách hàng và
  tài khoản hãng tàu đã chọn. Hãng tàu duyệt, lập draft và phát hành B/L.
- `bookings`: lịch xe giao/nhận tại cảng; mỗi chuyến giữ nguyên cơ chế chọn xe,
  tài xế rảnh, moóc và khung giờ. `export_gate_links` liên kết chính xác container
  xuất với chuyến xe, không dùng số Booking nhập tay làm bằng chứng.
- `containers` vẫn là nhận dạng vật lý. Hồ sơ hàng nằm tại bản khai riêng của
  công ty, giữ nguyên `data_status = NULL`; không ghi đè trạng thái vật lý khi
  đặt chỗ hoặc đặt lịch xe.
- Transport Company/Carrier Staff được xác định công ty từ `carrier_users` của
  tài khoản đăng nhập. Không nhận quyền sở hữu công ty từ request.
- Trong phiên bản này, mỗi tài khoản thực mang role `Shipping Line` đại diện
  cho một hãng tàu và chỉ xử lý Booking/lịch chuyến của tài khoản đó. Không tự
  chuyển Berth Staff thành hãng tàu hoặc suy đoán tài khoản từ dữ liệu mẫu.

## Luồng sử dụng

1. Transport Company bắt đầu tại một màn hình Tạo Booking: chọn container có
   hàng từ bản khai riêng, chọn hãng tàu/chuyến có thật. Một container là Booking
   riêng; từ 2–50 container cùng loại hàng là một lô. Kích thước có thể khác nhau.
   Lịch chuyến, hãng tàu và các hạn CY/VGM/SI là dữ liệu tham chiếu đã cấu hình;
   không tạo hãng tàu hoặc chuyến giả để làm đầy dropdown.
2. Hệ thống lấy số container, loại, seal, khối lượng hàng và công suất từ nguồn.
   Khách hàng bổ sung VGM thực tế đã xác minh, chỉ dẫn lạnh/nguy hiểm còn thiếu.
   Khối lượng hàng không được tự coi là VGM. Chứng từ chung cần tệp gốc và xác
   nhận phạm vi đủ các container; tải tệp không tự trở thành đã thông quan.
3. Bộ tối ưu đọc đội xe/tài xế của công ty và lịch bận thực tế. Các chuyến trùng
   giờ không dùng chung xe, tài xế hoặc moóc; có thể đề xuất giờ kế tiếp khi thiếu
   phương tiện. Moóc chỉ tự gợi ý từ lịch sử thực còn rảnh, không suy đoán từ
   container. Nếu chưa có nguồn thì người dùng bổ sung biển số thực tế.
4. Khách hàng gửi toàn bộ hồ sơ một lần bằng `POST /submissions`, có `requestId`
   chống tạo trùng. Container được giữ trong hồ sơ Pending để tránh gửi trùng,
   nhưng chưa tạo Booking cảng, lượt bãi, sự kiện qua cổng hoặc giữ chỗ vật lý.
   Hồ sơ chưa duyệt có thể sửa và gửi lại bằng cùng BookingId + ExpectedVersion.
5. Hãng tàu duyệt hoặc trả hồ sơ kèm lý do. Khi duyệt, backend kiểm tra lại lịch
   rảnh, thay phương tiện đã bận bằng phương án rảnh nếu còn, rồi tạo lịch riêng
   hoặc lô ở trạng thái Pending cho cảng xử lý. Tiếp nhận danh sách container,
   outbox/inbox và các lịch xe nằm trong cùng giao dịch; lỗi thì rollback toàn bộ.
   Cảng nhận dữ liệu không đồng nghĩa đã chấp nhận lịch hoặc đã qua cổng.
   Booking Note/VGM đối chiếu nguồn, không tạo tệp giả hoặc bắt tải lại. Hồ sơ
   hàng lạnh/nguy hiểm vẫn bắt buộc. Đây là truyền dữ liệu trong cùng ứng dụng,
   không phải khẳng định đã tích hợp EDI với hệ thống bên ngoài.

Chuyến kết hợp hai chiều dùng một container Giao thuộc hồ sơ xuất và một
container Nhận khác đã có lượt bãi/mã nhận hợp lệ. Hai tác nghiệp cùng một
khung giờ, xe, moóc và tài xế; không phải lô hai chuyến. Phía Nhận cần riêng
B/L, D/O, hải quan và chỉ dẫn hàng đặc biệt theo dữ liệu thật. Hồ sơ lưu
`submission.pickup`; số lượng đặt chỗ tàu chỉ tính container Giao. Khi hãng
tàu duyệt, backend tạo đúng một Booking `IsCombined` với hai nguồn/lượt bãi.
Mã nhận chuyển nhượng chỉ trả cho khách hàng, không trả qua API hãng tàu/cảng.
Luồng này dùng cột JSON đã có, không thêm migration hoặc sửa dữ liệu đã deploy.

6. Cảng đối chiếu chứng từ/kết quả thông quan. Nhân viên nhập seal quan sát
   thực tế; không tự lấy seal khai báo điền vào ô quan sát. Lệnh mở cổng và
   giao dịch Gate In đều kiểm tra điều kiện xuất khẩu. Giao dịch Gate In thật
   mới cập nhật tiến độ xuất; tạo Booking không tạo sự kiện qua cổng.
7. Khách hàng nộp SI độc lập với tiến độ xe, trước SI cut-off. Hãng tàu lập
   snapshot draft; khách hàng kiểm tra và xác nhận đúng phiên bản. SI xác nhận
   bị khóa; hãng tàu có thể yêu cầu sửa kèm lý do. Yêu cầu này cũng là quyền
   cho một lần nộp SI ngoại lệ sau cut-off, có lịch sử người thực hiện.
8. Sau CY cut-off, hãng tàu xem trước danh sách đủ điều kiện/bị loại rồi chốt.
   Loại container làm mất hiệu lực draft cũ của Booking bị ảnh hưởng, tránh
   phát hành B/L sai danh sách hoặc sai khối lượng hàng.
9. Cảng ghi nhận bắt đầu xếp, Loaded và rời cầu. Loaded cần có Gate In và vị trí
   bãi hiện tại được ghi sau Gate In của lượt tương ứng. B/L yêu cầu tàu đã
   rời cầu, toàn bộ container giữ lại đã xếp, draft hiện tại được xác nhận và
   cước trả trước được hãng tàu ghi nhận thanh toán.
10. Manifest lấy snapshot B/L đã phát hành, xuất CSV và lưu lần gửi tới cảng
    đến mô phỏng. Không khẳng định đã gửi tới hải quan/cảng đến thực tế.

## Giới hạn và triển khai

- Bản hiện tại phát hành một B/L cuối cùng cho mỗi Booking. Tách nhiều B/L,
  chuyển một phần container sang chuyến khác, hủy/phát hành lại B/L đã phát
  hành và ngoại lệ nhận container muộn cần quy trình chuyên biệt; không được
  mô phỏng thành công hay tự bỏ qua điều kiện hiện tại.
- Phí cảng của lịch xe và cước hãng tàu là hai trạng thái riêng. Không tích hợp
  thu cước thật, khai hải quan thật hay stowage plan.
- Migration `20261104_031_export_booking_workflow.sql` bổ sung các bảng xuất
  khẩu và cột seal quan sát tại cổng. Không seed tài khoản, lịch chuyến, tạo
  Booking mẫu, backfill hay sửa dữ liệu nghiệp vụ cũ.
- Migration `20261105_032_unified_booking_submission.sql` chỉ bổ sung cột JSON
  `export_bookings.submission` cho hồ sơ, chứng từ và lịch đề xuất. Booking cũ
  giữ NULL; API cũ vẫn phục vụ các hồ sơ đã lưu theo quy trình trước đó.
- Cần tài khoản hãng tàu thực và lịch chuyến thực để bắt đầu đặt chỗ. Khi
  chưa cấu hình, màn hình hiển thị trạng thái trống và hướng dẫn phù hợp.
- Các lịch Giao đã lưu trước đây và nghiệp vụ Nhận giữ nguyên. Lịch Giao mới
  từ tài khoản công ty cần liên kết xuất khẩu hợp lệ; không cho đi vòng qua
  form cũ để bỏ bước hãng tàu duyệt.
- Đổi giờ của lịch xe liên kết cũng kiểm tra lại CY cut-off. Các cập nhật
  Booking hãng tàu dùng `expectedVersion`; xác nhận draft dùng thêm
  `draftVersion`. Sự kiện cảng dùng ID chống xử lý trùng và kiểm tra đúng đích.

## Kiểm chứng

`BookingPlanningChecks` chứa kiểm tra checksum ISO và việc client không thể
gửi cờ xác minh nguồn. Luồng HTTP đầy đủ được kiểm thử với dữ liệu tổng hợp
trên PostgreSQL localhost riêng có marker, gồm đặt chỗ, phân quyền, lô hai
chuyến, Gate In, chốt chuyến, sửa SI có quyền, B/L và manifest. Giao diện khách
hàng được kiểm thử bằng Chrome với API mô phỏng. Các kiểm tra giao diện hãng
tàu/cổng trước khi thu hẹp phạm vi không đại diện cho bản HEAD được khôi phục.
Không chạy test tạo Booking hoặc seed trên database đã deploy.
