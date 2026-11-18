# Module Chuẩn Bị Container Trước Khi Giao (Import Preparation TOS)

Nghiệp vụ backend cho TOS kết hợp e-Port: **Chuẩn bị container trước khi xe đến lấy (chiều nhập khẩu)**, đảm bảo container đích ở vị trí thuận tiện nhất để nâng ngay, giảm thiểu tối đa số lần cẩu đảo chuyển container khác.

---

## 1. Công nghệ & Kiến trúc
- **Framework**: ASP.NET Core Web API (.NET 8)
- **Database**: PostgreSQL (Npgsql) qua EF Core với Concurrency Token (`RowVersion`)
- **Xác thực & Phân quyền**: JWT Bearer, RBAC Policy (`Dispatcher`, `YardStaff`, `Administrator`)
- **Validation**: FluentValidation
- **Giám sát nền**: `BackgroundService` (`PreparationMonitoringWorker`)
- **Tích hợp sự kiện**: Outbox Pattern (`PreparationOutboxMessage`) đảm bảo At-Least-Once Delivery
- **Xử lý lỗi**: Chuẩn RFC 7807 `ProblemDetails` bằng tiếng Việt

---

## 2. Thực thể (Entities)

| Thực thể | Mục đích |
|---|---|
| `PreparationTask` | Quản lý lệnh chuẩn bị container theo giờ hẹn, mức ưu tiên, trạng thái vòng đời. |
| `PreparationWorkOrder` | Lệnh làm hàng chi tiết (Rehandle / Stage), thứ tự, vị trí From/To, thiết bị và người thực hiện. |
| `SlotReservation` | Khóa giữ chỗ ô bãi đích độc quyền, chống xung đột hai lệnh cùng đích. |
| `PreparationConfig` | Bảng cấu hình hệ thống: Lead time, Max rehandle, Staging block, Trọng số ưu tiên. |
| `ContainerMovement` | Lịch sử vết di chuyển container trong bãi. |
| `EDoRecord` | Dữ liệu e-D/O, trạng thái giải phóng, hạn lệnh, số chì niêm phong. |
| `ExceptionItem` | Ghi nhận các ngoại lệ bãi (sai vị trí, lệch số chì, vượt ngưỡng đảo, trễ KPI). |
| `PreparationOutboxMessage` | Bảng Outbox lưu trữ sự kiện bền vững cùng transaction với dữ liệu. |

---

## 3. Thuật toán Lập Kế Hoạch (`PreparationPlanner`)

1. **Xác định Stack**: Lấy vị trí container đích $(Bay, Row, Tier)$. Tìm các container nằm **TRÊN** nó ($Tier > targetSlot.Tier$).
2. **Không có container chắn**:
   - Nếu có cấu hình `StagingBlockId`: tạo 1 lệnh `Stage` đưa container ra khu chờ giao.
   - Nếu không: Chuyển thẳng `Planned` với 0 lần đảo chuyển.
3. **Có container chắn**:
   - Xử lý từ tầng cao nhất xuống thấp nhất.
   - **Quy tắc cứng (Hard Constraints)**:
     - Ô bãi phải trống (`Status = "empty"`) và chưa bị giữ chỗ (`SlotReservation`).
     - Tầng $\le 4$.
     - Khác stack với container đích.
     - Container lạnh bắt buộc vào ô có ổ điện (`HasReeferPlug == true`); container thường không được chiếm ô có điện.
   - **Quy tắc mềm (Soft Constraints - Scoring)**:
     - Gần ô cũ (+100 điểm cùng khu, trừ điểm khoảng cách bay/row).
     - Không chắn container khác có lịch xuất trong 24 giờ tới (+50 điểm; -100 điểm nếu xung đột).
     - Ưu tiên tầng thấp hơn (+15 điểm mỗi bậc tầng thấp).
     - Cân bằng tải khu bãi (+ điểm theo tỷ lệ trống của Block).
4. **Vượt ngưỡng `MaxRehandlePerTask`** (mặc định 6):
   - Không tự động lập lệnh, đánh dấu cần điều độ duyệt và tự động tạo `ExceptionItem`.
5. **Lập kế hoạch theo lô (`PlanBatch`)**:
   - Sắp xếp lệnh theo giờ hẹn tăng dần (lệnh hẹn sớm xử lý trước).
   - Tích lũy giữ chỗ xuyên suốt lô để các ô tạm không cản trở lệnh sau.

---

## 4. Vòng đời Thực thi & Ngoại lệ

```
[SlotConfirmed + EDo Valid] ──> [Pending] ──> [Planned] ──> [Assigned] ──> [InProgress] ──> [All Done] ──> [Confirm Ready] ──> [Ready]
                                   │                                            │                                │
                                   ├──> [Needs Approval: Rehandle > 6]          └──> [Failed] ──> [Delayed]     └──> [Seal Mismatch -> Exception]
                                   └──> [EDo Held / Cancelled] ──> [Cancelled]
```

- **Điều độ**:
  - Gán người thực hiện hoặc thiết bị (`POST /api/work-orders/{id}/assign`).
  - Lập lại kế hoạch khi thay đổi bãi (`POST /api/preparation/tasks/{id}/replan`).
  - Hủy lệnh kèm lý do (`POST /api/preparation/tasks/{id}/cancel`).
- **Nhân viên bãi**:
  - Bắt đầu (`POST /api/work-orders/{id}/start`): Kiểm tra container có đúng ô `FromSlot` không. Sai thì từ chối và ghi `ExceptionItem`.
  - Hoàn tất (`POST /api/work-orders/{id}/complete`): Quét số container, kiểm tra ô đích thực tế, ghi `ContainerMovement`, giải phóng `SlotReservation`.
  - Báo lỗi (`POST /api/work-orders/{id}/fail`): Tự động lập lại kế hoạch 1 lần; nếu thất bại lần 2 thì tạo `ExceptionItem`.
- **Xác nhận sẵn sàng giao**:
  - So khớp số container và số chì thực tế với dữ liệu e-D/O.
  - Khớp: Chuyển `Ready`, phát sự kiện `ContainerReadyForPickup`.
  - Không khớp số chì: Từ chối xác nhận, lập `ExceptionItem` nghiêm trọng và báo hãng tàu.

---

## 5. Giám sát Nền (`PreparationMonitoringWorker`)
Chạy định kỳ mỗi `DelayCheckIntervalSeconds`:
- **Cận giờ `StartBy` (15 phút)**: Phát sự kiện `PreparationAtRisk`.
- **Quá `TargetReadyAt`**: Chuyển trạng thái `Delayed`, tăng ưu tiên, phát `PreparationDelayed`. Nếu quá `RescheduleGraceMinutes` (15 phút) -> lập ngoại lệ KPI.
- **Dọn dẹp giữ chỗ**: Tự động giải phóng ô đích bị giữ quá hạn mà lệnh không chạy.
- **Dispatch Outbox**: Phát các sự kiện bền vững ra hệ thống thông báo/Message Bus.

---

## 6. Hướng dẫn Chạy & Kiểm thử

### Áp dụng Database Schema
Chạy file SQL script:
```powershell
# Chạy file script PostgreSQL
psql -h localhost -U postgres -d NexusPort -f "D:\SEP\NexusPort_Website\BE\NexusPort-Backend-Website\csharp-core\src\Infrastructure\Database\Scripts\003_container_preparation.sql"
```

### Chạy Unit Tests
```powershell
dotnet test "D:\SEP\NexusPort_Website\BE\NexusPort-Backend-Website\csharp-core\tests\PreparationPlannerChecks\PreparationPlannerChecks.csproj"
```
Kết quả kiểm thử: **11/11 tests Passed** đầy đủ mọi kịch bản nghiệp vụ:
- 0 container chắn -> 0 lượt đảo chuyển.
- Container chắn -> Chọn ô tối ưu theo quy tắc cứng và mềm.
- Nhiều container chắn -> Đảo theo thứ tự tầng từ trên xuống dưới.
- Bãi hết ô hợp lệ -> Trả lỗi rõ ràng bằng tiếng Việt.
- Container lạnh -> Bắt buộc chọn ô có cắm điện.
- Container khô -> Không được chiếm ô có điện.
- Tránh ô thuộc cùng stack hoặc ô đang bị giữ bởi lệnh khác.
- Vượt quá `MaxRehandlePerTask` -> Gắn cờ cần điều độ duyệt.
- Lập kế hoạch theo lô -> Tối ưu thứ tự theo độ khẩn cấp và không tạo xung đột mới.
