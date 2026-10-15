# NXP-114 — Smart Route

## Phạm vi

BE chỉ thay đổi trong `node-core`, FE thay đổi các màn hình Driver và thêm service Driver trong repo frontend, theo phạm vi đã được cho phép. Không migration, không ghi DB, không chỉnh `csharp-core`, Gate, Yard hay Dispatcher. Giữ các endpoint và trường dữ liệu route cũ.

## Luồng dữ liệu

1. Driver đăng nhập bằng JWT hiện có và gọi `GET /api/v1/drivers/me/routes`.
2. Chỉ lấy container thuộc đúng Driver, đã Gate In (`allowed`/`completed`), chưa rời cảng/kết thúc. Cổng xuất phát lấy từ giao dịch Gate In thực tế, không dùng cổng ghi trong hướng dẫn cũ.
3. Đọc Operation đang hoạt động trong `yard_tasks` của đúng Driver/container; ưu tiên Operation phân công gần nhất. `ToLocation` là nguồn điểm đích. Chuỗi `Block-Bay-Row-Tier` được đối chiếu với `yard_blocks`/`yard_slots`, chấp nhận số có zero đầu. Khu vực xử lý được ánh xạ bằng `node.location`.
4. Nếu Operation chưa có `ToLocation`, đọc reservation gắn đúng Operation (`NXP-059 operation:<id>`). Khi không có Operation đang hoạt động, giữ tương thích với reservation/position hiện có. Không lấy reservation của Operation khác để thay thế một điểm đích Operation không hợp lệ.
5. Thuật toán Dijkstra tìm tuyến có thời gian ước tính thấp nhất, tuân thủ đường một chiều, loại node/edge bị hạn chế. Chi phí cạnh = `travelSeconds * (1 + 4 * congestion)`, với congestion trong [0,1]. `rerouted` cho biết tuyến khác với tuyến tối ưu khi bỏ qua hạn chế/ùn tắc.
6. FE tự đọc lại mỗi 15 giây và khi kết nối/trở lại cửa sổ. Destination/conditions thay đổi sẽ cập nhật revision và thông báo. Driver có thể xác nhận waypoint hiện tại; không mô phỏng GPS hay tự xác nhận đến bãi.

## API tương thích

- `GET /api/v1/drivers/me/routes`: danh sách container, điểm đến và khả năng tạo tuyến.
- `GET /api/v1/drivers/me/routes/:containerId?currentNodeId=<map-node-id>`: tính lại tuyến. Bỏ `currentNodeId` để bắt đầu tại cổng Gate In. Các endpoint cũng có tại prefix `/api` hiện có.
- Các trường cũ `containerId`, `containerNumber`, `bookingId`, `gateTransactionId`, `gateStatus`, `destination`, `route` được giữ nguyên. `route.directions` là hướng dẫn vị trí cũ; FE Smart Route chỉ hiển thị tuyến khi `navigation.status === 'available'`.
- Bổ sung `operation`, `target` (`slot` hoặc `area`) và `navigation`.
- `navigation` gồm status, reasonCode, message, revision, generatedAt, refreshAfterSeconds, mapVersion, origin, destinationNode, waypoints, segments, directions, distanceMeters, estimatedSeconds, rerouted.
- Không có tuyến: HTTP 200, vẫn trả điểm đến, `navigation.status = unavailable`; UI hiển thị lý do. Chưa gán điểm đến: list trả `pending_destination`, endpoint chi tiết giữ HTTP 409 tương thích. Không có Gate In của Driver/container: HTTP 404. JWT sai/thiếu: 401; sai role: 403; tham số sai: 422.
- `Cache-Control: no-store` trên API điều hướng.

## Bản đồ đường nội bộ

Dữ liệu hiện tại có bảng route instruction và JSONB `map_data` nhưng chưa có graph đường nội bộ. Cần bản đồ được xác nhận bởi đội vận hành trước khi dùng dẫn đường thật. Không suy diễn đường xe chạy từ tọa độ Block/Slot, không tự bật bản đồ ví dụ.

Hai nguồn được hỗ trợ:

1. **Snapshot dùng chung cho Node**: đặt `SMART_ROUTE_MAP_PATH` trong môi trường chạy Node, trỏ đến file JSON bản đồ. Nên dùng đường dẫn tuyệt đối. File được đọc lại ở mỗi lần tính tuyến. Đổi file theo cách ghi file tạm rồi rename để tránh đọc dở dang. Đây là cấu hình ở Node, không phải schema DB.
2. **Snapshot instruction hiện có**: nếu không đặt đường dẫn trên, đọc `driver_route_instructions.map_data.smartYardMap`. Instruction vẫn phải gắn đúng booking/Driver/container và block theo điều kiện SQL hiện có. Không có endpoint ghi instruction mới trong feature này.

Xem `config/smart-yard-map.example.json` để biết định dạng. File này chỉ là dữ liệu minh họa, không phải sơ đồ thật. Cần thay gateCode, blockCode, location, topology, chiều đường và chiều dài bằng dữ liệu bãi thực tế.

### Hợp đồng snapshot

- `version`: chuỗi phiên bản bắt buộc. `updatedAt`, `validUntil`: ISO UTC tùy chọn. Snapshot hết `validUntil` bị từ chối; bên cung cấp realtime cần đặt TTL thích hợp.
- `nodes`: tối đa 2000, id duy nhất, label, tọa độ sơ đồ hữu hạn x/y. Tùy chọn gateCode, blockCode, slotId (UUID), location và restricted.
- `edges`: tối đa 10000, id duy nhất, from/to phải tồn tại; distanceMeters và travelSeconds dương. Mặc định một chiều. `bidirectional: true` cho phép hai chiều. restricted mặc định false, congestion mặc định 0.
- Điểm đầu khớp gateCode; điểm đích ưu tiên slotId, tiếp theo location; với slot có thể dừng tại node lối vào Block duy nhất (`blockCode`, không có slotId/location), rồi hướng dẫn đến Bay/Row và xử lý container ở Tier theo Yard. Operation chỉ gán mã Block cũng được ánh xạ vào lối vào Block. Nếu Operation dùng định dạng Block-Bay-Row-Tier nhưng Slot không tồn tại trong Yard, trả `YARD_LOCATION_INVALID`.
- Khi cấu hình nhiều node cho cùng gate/block, cần gán điểm đến cụ thể bằng slotId/location để tránh mơ hồ. Endpoint không chọn ngẫu nhiên một lối vào.
- `instruction` là hướng dẫn cạnh theo chiều from → to. Ở chiều ngược của cạnh bidirectional, hệ thống dùng hướng dẫn sinh từ waypoint để tránh đảo sai chỉ dẫn rẽ.

## Điểm mở rộng Smart Yard / IoT

`YardMapProvider.load()` cung cấp một snapshot topology + hạn chế + congestion. Có thể thay provider bằng nguồn Smart Yard hoặc bộ tổng hợp IoT mà không đổi API Driver hay schema PostgreSQL. Nguồn file cấu hình ưu tiên hơn snapshot cũ trong instruction. Chưa triển khai feed IoT/Smart Yard bên ngoài; cần thống nhất dữ liệu và quyền sở hữu với module đó trước khi tích hợp.

## Các trạng thái cần thông báo

`YARD_MAP_NOT_CONFIGURED`, `YARD_MAP_INVALID`, `YARD_MAP_EXPIRED`, `ORIGIN_NOT_MAPPED`, `CURRENT_POSITION_NOT_MAPPED`, `DESTINATION_NOT_MAPPED`, `DESTINATION_RESTRICTED`, `NO_SAFE_ROUTE`, `YARD_LOCATION_NOT_ASSIGNED`, `YARD_LOCATION_INVALID`. FE hiển thị thông báo tiếng Việt và nút thử lại; khi mất kết nối hoặc lỗi API sẽ ẩn tuyến chưa được xác nhận lại.

## Liên quan module ngoài phạm vi

Luồng `src/modules/trips/trips.service.js::confirmGateIn` thuộc backend JavaScript cũ chỉ cập nhật `transport_trips`/history; chưa tạo `gate_transactions` và `gate_transaction_containers`. Việc nhấn nút Gate In của luồng đó không tự tạo dữ liệu Gate In hợp lệ cho API Driver Route hiện có. Feature này không sửa module Trip, không xem trạng thái chuyến là giao dịch cổng, không tạo dữ liệu cổng thay module Gate. Cần phối hợp chủ module nếu muốn hợp nhất hai luồng; hiện dùng Gate In thật từ module Gate để nhận route. Màn Smart Route vẫn truy cập độc lập khi API thông tin chuyến cũ lỗi.

## Kiểm tra

Chạy từ node-core: `npm run build`, `npm test -- --runInBand`. Kiểm tra có trọng tâm: tuyến ngắn nhất, một chiều/hai chiều, vùng hạn chế, ùn tắc, hết tuyến, đích khu vực, bảo trì, vị trí Driver, revision khi đích thay đổi, nguồn snapshot hỏng/hết hạn và trường route cũ.

Chạy `npm run build` tại repo frontend. Smoke test với tài khoản Driver đã Gate In và bản đồ bãi được xác nhận: vào trang chủ Driver → Smart Route; đổi Operation destination từ chức năng điều phối hiện có; cập nhật congestion/restricted trong snapshot; xác nhận UI đổi tuyến ở lần refresh tiếp theo. Không cần chạy migration hoặc seed cho feature này.
