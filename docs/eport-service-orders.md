# Quản lý đơn dịch vụ

Trang `/service-orders`: danh sách, tìm/lọc, chi tiết, lịch sử, thanh toán và phiếu đăng ký. Backend C#; migration `20261109_040_eport_service_order_management.sql`.

## Phân quyền

- Khách: chỉ xem đơn công ty mình, sửa nháp và hủy khi còn được phép.
- Administrator/Admin/Dispatcher: xem đơn đã đăng ký, tiếp nhận, duyệt/từ chối theo trạng thái.
- Đơn nhập cần đối chiếu DO/eDO và quyền nhận hàng, lưu căn cứ trước khi duyệt.

| Trạng thái | Thao tác chính |
| --- | --- |
| DRAFT | Sửa, kiểm tra, xác nhận hoặc hủy |
| CONFIRMED | Có thể thanh toán; cảng có thể tiếp nhận |
| PENDING_APPROVAL | Chờ cảng tiếp nhận và duyệt trước thanh toán |
| RECEIVED | Khách không tự hủy; cảng duyệt/từ chối nếu còn hợp lệ |
| APPROVED | Có thể thanh toán |
| REJECTED / CANCELLED | Đóng đơn, không mở lại hoặc sửa nội dung |

## Quy tắc và API

- Hủy/từ chối phải có lý do; đang thanh toán hoặc đã trả tiền thì chặn hủy/từ chối.
- Đơn 0 đồng dùng `NOT_REQUIRED`, không tạo giao dịch.
- Giá/nội dung đã xác nhận và lịch sử được giữ nguyên.
- Prefix `/api/v1/ServiceOrders`: `GET /`, `GET /{id}`, `GET /{id}/history`, `POST /{id}/actions`.
- Action gồm `CANCEL`, `RECEIVE`, `APPROVE`, `REJECT`; gửi `expectedVersion`, UUID `requestId` và lý do khi cần.
- Duyệt nhập thêm `sourceDocumentsChecked:true` và `sourceEvidenceReference`. Phiên bản workflow độc lập phiên bản nháp.

Xem [PayOS](eport-service-payments.md) và [phiếu](eport-service-receipts.md).
