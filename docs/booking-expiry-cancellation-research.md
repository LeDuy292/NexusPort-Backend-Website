# Booking quá hẹn và hủy chuyến — nghiên cứu, chưa triển khai

## Hành vi hiện tại trên NXP-114

Ngày hẹn booking, hạn phản hồi lệnh gọi và hạn vào cổng là ba mốc khác nhau.

- `dispatch.policy.js` chỉ giới hạn thời điểm sớm nhất là 12 giờ trước hẹn.
  Không có điều kiện chặn `appointmentEnd` đã qua cho arrival/call; đến muộn
  được đánh dấu nhưng không tự hủy booking. Bảng điều độ có bộ lọc theo ngày,
  nên booking cũ có thể không nằm trong danh sách ngày hiện tại.
- Lệnh gọi cho tài xế có hạn phản hồi 10 phút. Sau khi nhận lệnh, quyền vào
  cổng có hạn 15 phút. Hết hạn thì worker trả về chờ điều độ gọi lại, không hủy
  booking nghiệp vụ.
- `GateEntryAccess.Check` yêu cầu booking Approved/Ready, đúng xe/tài xế/biển
  số, đúng booking và lệnh active còn hạn. `BookingTimeWindowRule` dùng hạn
  điều độ cho Gate In; không dùng lại ngày hẹn như điều kiện chặn vào cổng.
- Vì vậy booking ngày cũ còn Approved/Ready có thể được gọi lại và vào bằng
  một quyền mới; quyền cũ hết hạn không cho vào. Pending chưa được duyệt,
  Canceled/Rejected/Expired và booking đã vào/hoàn tất không được Gate In lại.
- `CancelAsync` hiện chặn CheckedIn/Completed và booking đã hủy. Đổi lịch qua
  `UpdateAsync` hiện chỉ hỗ trợ Pending; chưa có luồng đổi lịch đã duyệt riêng.
- Trigger 015 thu hồi điều phối khi booking đổi lịch, đổi tài xế hoặc rời
  Approved/Ready. Trigger 018 thu hồi quyền vào cổng tương ứng.

Nguồn đọc: `src/modules/booking-dispatch/dispatch.policy.js`,
`dispatch.service.js`, `dispatch.realtime.js`,
`csharp-core/src/Modules/Gate/Application/Services/GateEntryAccess.cs`,
`csharp-core/src/Modules/Gate/Domain/Rules/Concrete/BookingTimeWindowRule.cs`,
`csharp-core/src/Modules/Booking/Application/Services/BookingService.cs`,
migrations 009/010/015/018. Không thay đổi các file nghiệp vụ này trong đợt sửa
bản đồ.

## Luồng đề xuất để chốt sau

| Trường hợp | Đề xuất |
| --- | --- |
| Quá giờ hẹn, chưa Gate In và chưa đến khu chờ | Đánh dấu quá hẹn/no-show sau thời gian ân hạn cấu hình; yêu cầu chọn đổi lịch hoặc hủy |
| Đã đến, sẵn sàng hoặc đang được điều độ giữ chờ | Giữ trạng thái chờ và ghi lý do chậm; điều độ xem xét gia hạn/đổi lịch, không tự coi là bỏ chuyến |
| Còn nhu cầu thực hiện, chưa Gate In | Yêu cầu lịch mới → điều độ duyệt lại → thu hồi quyền/lệnh cũ → tài xế nhận lệnh mới |
| Không tiếp tục, chưa Gate In | Yêu cầu hủy có lý do → người có quyền xác nhận → Canceled, thu hồi điều phối và quyền vào cổng |
| Đã Gate In | Dùng quy trình dừng/đổi xử lý trong cảng; không hủy booking thông thường |
| Đã Gate Out | Giữ lịch sử hoàn thành; sai sót xử lý bằng nghiệp vụ điều chỉnh có nhật ký |

Không đồng nhất quá hẹn với hủy: quá hẹn mô tả thời gian; hủy là quyết định
kết thúc chuyến. Thời gian ân hạn và quyền phê duyệt chưa được chốt hay áp dụng.

## Những điều cần bảo đảm khi triển khai

1. Driver chỉ gửi yêu cầu cho booking được giao; Carrier chỉ thao tác booking
   thuộc công ty; Dispatcher/Admin quyết định theo quyền được quy định.
   Endpoint cancel hiện kế thừa role Driver ở controller nhưng service chỉ
   kiểm tra tenant cho Carrier. Cần bổ sung kiểm tra quyền sở hữu/phạm vi trước
   khi cho Driver sử dụng quy trình hủy.
2. Xác nhận hủy và Gate In dùng cùng khóa booking, kiểm tra lại giao dịch cổng
   trong transaction, tránh hủy và cho xe vào đồng thời. Retry cùng yêu cầu
   không sinh thêm lần hủy/thông báo hay hoàn tài nguyên.
3. Thu hồi lệnh gọi, quyền vào cổng và phân bổ tương lai của đúng booking;
   lưu lý do, người yêu cầu, người duyệt, thời điểm và phiên bản điều phối.
   Thông báo cho tài xế, công ty, điều độ và màn hình cổng sau commit.
4. Hủy Pickup trước khi vào không đưa container ra khỏi bãi và không kết thúc
   lượt lưu bãi. Hủy Dropoff chưa thực hiện phải kiểm tra các Pickup phụ thuộc
   của lượt dự kiến; không xóa master container, bản khai hay lịch sử EIR.
5. Sau Gate In, nếu buộc dừng chuyến: điều độ và Yard xác định xe/container đang
   ở đâu, thu hồi công việc chưa thực hiện, xử lý vật lý và chứng từ cần thiết,
   rồi ghi nhận Gate Out phù hợp. Không sửa ngược giao dịch đã xảy ra.

Trên app có thể đặt yêu cầu hủy/đổi lịch trong chi tiết booking và bước Sẵn
sàng. Sau Gate In chỉ hiển thị yêu cầu hỗ trợ/sự cố. Timeline cần ghi rõ quá
hẹn, chờ duyệt đổi lịch, đang yêu cầu hủy và đã hủy, thay vì giả hoàn thành.

## Phạm vi đợt này

Chỉ sửa cách đọc timestamp của bản đồ và thêm kiểm tra hồi quy cho bản đồ.
Chưa thêm trạng thái booking, API hủy/đổi lịch, worker no-show hay thao tác
cập nhật booking test. Chẩn đoán database chỉ chạy SELECT trong transaction
READ ONLY.
