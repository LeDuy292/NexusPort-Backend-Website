# NXP-114 Smart Route — Gate In → giao nhận container → Gate Out

## Phạm vi và cách chạy

Nhánh `feature/NXP-114-smart-route` của backend, web và app dùng develop mới nhất.
Thay đổi mới nằm trong Node TypeScript, frontend, Expo app và migration SQL; không sửa C#.

Áp dụng mọi migration còn thiếu theo thứ tự filename trước khi chạy Node:

```powershell
node --env-file=node-core/.env database/scripts/apply-smart-yard-routes.cjs
node --env-file=node-core/.env database/scripts/apply-smart-yard-routes.cjs --apply
npm --prefix node-core run build
npm --prefix node-core test -- --runInBand
```

## Dữ liệu và luồng

- Chỉ giao dịch `gate_transactions.gate_in/completed` xác nhận xe đã vào bãi. Gate Officer dùng chức năng Gate hiện tại. Trạng thái transport trip hoặc nút Driver không tạo xác nhận cổng.
- Driver identity được resolve từ `drivers.user_id` của tài khoản JWT; hỗ trợ profile legacy có cùng ID và chưa liên kết. Driver không thể chọn ID tài xế khác.
- Booking/container/stay được nối bằng `booking_containers`; không phụ thuộc `gate_transaction_containers`, vì luồng duyệt Gate hiện tại không ghi bảng đó.
- Operation có `booking_id`/`container_visit_id` được ưu tiên và phải khớp đúng lượt bãi. Với operation legacy chưa liên kết, chỉ dùng khi đúng Driver/container, được tạo sau Gate In và booking hiện hành không mơ hồ. Operation phân công trước Gate In cần liên kết booking tường minh; API gán destination nhận thêm `bookingId` để lưu liên kết đã kiểm tra. Không lấy reservation của operation khác. Pickup không có operation có thể dùng vị trí hiện tại của đúng lượt bãi đang active.
- Đích Operation `ToLocation` được đối chiếu Slot hoặc area trên map. Một Slot không tồn tại không được biến thành một area giả.
- Driver xem đường, báo đã đến điểm giao nhận. Progress gắn transaction Gate In + container + destination key. Đổi điểm đến sẽ bỏ trạng thái đã đến cũ và tính đường từ vị trí cuối Driver đã báo.
- Sau yard event `Completed` của operation đúng Driver/container, xảy ra sau Gate In, Driver mới được xác nhận container (`OK`, `DAMAGED`, `ISSUE`). Node ghi cùng `container_confirmation_audits` với luồng hiện có, không tạo một hệ trạng thái riêng. Booking chỉ completed khi mọi container đã xác nhận.
- Sau khi booking hoàn tất, route chuyển tới GATE-03. Driver chỉ thấy chờ xác nhận cổng. Gate Officer xác nhận Node `POST /api/v1/gate/complete-out`; giao dịch thực tế đổi journey thành `exited`, trigger DB đóng đúng Pickup visit/phát hành EIR. Xe Dropoff rỗng ra cổng giữ nguyên lượt lưu bãi của container.
- Dispatcher xem mọi xe đang trong bãi và xe đã ra trong 24 giờ, gồm booking, biển số, Driver, operation, điểm đến, thời điểm các bước và lý do không có tuyến.

## API Node Core (port 4000)

Base `/api/v1` (alias `/api`). Web dùng proxy `/node-api`.

| Method | Path | Quyền / ý nghĩa |
| --- | --- | --- |
| GET | `/drivers/me/routes` | Driver: tất cả lượt đã Gate In của mình |
| GET | `/drivers/me/routes/:containerId?bookingId=...&currentNodeId=...` | Driver: chọn đúng booking; tính lại từ waypoint đã báo |
| POST | `/drivers/me/routes/:containerId/progress` | Driver: `{bookingId,action:"arrive",currentNodeId?}` |
| POST | `/drivers/me/routes/:containerId/confirm-container` | Driver: `{bookingId,condition:"OK",notes?}` |
| GET | `/dispatcher/smart-routes` | Dispatcher/Administrator: `{routes,generatedAt}` |
| PUT | `/dispatcher/smart-routes/conditions` | Dispatcher/Administrator: `{targetType:"edge"|"node",targetId,restricted,congestion,validUntil?}` |
| GET | `/gate/locations` | Đọc cổng thật `{id,code,name,gateType,isActive}` |
| POST | `/gate/complete-out` | Gate Officer/Gate/Administrator: `{bookingId,gateId}` xác nhận xe đã ra; Driver không được ghi |

DTO giữ các trường route cũ, thêm `operation`, `target`, `navigation`, `journey`, Driver/biển số/booking.
`journey.phase`: `to_yard`, `at_yard`, `ready_for_gate_out`, `exited`.
`navigation` có cả map graph/layout, tuyến, hướng dẫn, khoảng cách/thời gian ước tính và revision.
Mỗi refresh đọc lại đích, map và điều kiện. HTTP 200 vẫn trả điểm đến khi `unavailable`/`pending_destination`.
Thiếu Gate In hoặc ngoài phạm vi Driver: 404. Token/role sai: 401/403. Payload sai: 422. Xung đột trạng thái: 409.
API điều hướng đặt `Cache-Control: no-store`.

## Bản đồ và điều kiện đường

Migration 017 tạo map sơ đồ theo reference: Gate01/02 ở bắc, đường vòng một chiều, điểm giao nhận A-S1/A-S2 phía đông, Gate03 phía nam. Block/Slot/occupancy lấy từ Yard Location thật. Đây là sơ đồ nội bộ; tọa độ không phải GPS và thời gian/khoảng cách là ước tính theo cấu hình. Slot map biểu diễn điểm tiếp cận giao nhận, không dẫn xe chạy xuyên qua chồng container.

`ConfiguredYardMapProvider` là boundary thay được bằng nguồn Smart Yard/IoT:

1. `SMART_ROUTE_MAP_PATH` nếu cấu hình: đọc snapshot JSON ngoài mỗi request.
2. Active `smart_yard_maps` và overlay `smart_yard_conditions`, occupancy hiện tại từ DB.
3. Snapshot `driver_route_instructions.map_data.smartYardMap` tương thích khi không có active map.

Đường một chiều mặc định; chỉ cạnh có `bidirectional` mới đi hai chiều. Node/edge restricted bị loại; congestion [0,1] tăng chi phí `travelSeconds * (1 + 4*congestion)`. Không có đường thay thế hợp lệ thì báo dừng nơi an toàn/liên hệ điều phối; không vẽ đường xuyên vùng cấm.
Condition dispatcher mặc định hết hạn sau 30 phút, có thể đặt `validUntil`. Provider bỏ observation hết hạn. File map ngoài là nguồn ưu tiên và UI không ghi đè nó. Chưa kết nối một thiết bị IoT bên ngoài; adapter/schema đã sẵn để nhận observations cùng contract.

Web/app refresh mỗi 15 giây và khi trở lại màn hình. Sự kiện yard hiện có có thể đánh thức refresh; polling đảm bảo cập nhật cả khi Gate không phát socket.

## Kiểm tra

Node unit/API tests bao phủ Dijkstra, một chiều, restriction/congestion/no-route, map hỏng/hết hạn, auth linked account, arrival, thay đổi destination, confirmation và exit. Integration phải dùng database local cô lập; không tạo fixture trên DB cấu hình remote. Chạy thêm web build/lint, app TypeScript và auth/bookings tests.
