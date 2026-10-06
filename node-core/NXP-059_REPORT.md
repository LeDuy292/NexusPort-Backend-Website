# NXP-059 - Phân công vị trí và điểm đến cho Driver

- Chỉ thay đổi `node-core`; không sửa `csharp-core`, scaffold hay schema PostgreSQL dùng chung.
- Dispatcher xem danh sách Yard slot qua `GET /api/v1/dispatcher/yard/destinations` và gán slot cho Operation qua `PUT /api/v1/dispatcher/operations/:operationId/destination`.
- Khi gán, API khóa Operation/slot trong transaction, lưu reservation, cập nhật `yard_tasks.ToLocation/BlockCode` và từ chối slot không còn trống.
- Driver đọc destination được gán (Block/Bay/Row/Tier) qua `GET /api/v1/drivers/me/destinations`; không có endpoint Driver để thay đổi destination.
- Liên kết Operation được lưu trong `yard_slot_reservations.reason` theo dạng `NXP-059 operation:<operationId>` và có thể kiểm tra qua API GET destination của Operation.
