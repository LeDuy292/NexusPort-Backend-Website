# Cải tổ cơ sở dữ liệu NexusPort

## Hiện trạng Railway

Kiểm kê ngày 20/11/2026 ghi nhận 148 bảng trong `public`, 3 bảng ở schema sao lưu và 1 view. Có 63 bảng rỗng. Trong đó 31 bảng chưa thấy tên được nhắc trực tiếp trong mã chạy đã quét. Đây là danh sách để điều tra: truy vấn động, chức năng dùng theo mùa, công việc nền và tích hợp ngoài có thể không xuất hiện trong kết quả. Chỉ số đọc/ghi của PostgreSQL cũng có thể bị đặt lại.

`public.containers` có 160 dòng và được tham chiếu bởi `booking_containers`. Bảng phân biệt hoa thường `public."Containers"` có 0 dòng, không có khóa ngoại trỏ đến và không có view phụ thuộc. Ánh xạ C# của Yard và một script SQL cũ vẫn dùng tên viết hoa. Đây là cặp trùng đã có bằng chứng đủ để chuẩn bị chuyển đổi đầu tiên.

## Thiết kế đích

| Nhóm | Dữ liệu nguồn và quan hệ dự kiến |
| --- | --- |
| Tài khoản và đội xe | `users`, `carriers`, `carrier_users`, `drivers`, `trucks`. Quan hệ truy cập qua membership có khóa ghép; gán xe cho tài xế phải cùng công ty. Giữ lịch sử tài khoản đã xóa cho đến khi xác định cách lưu chủ thể lịch sử. |
| Container và vận hành bãi | `containers` là hồ sơ vật lý. `container_declarations` giữ khai báo của từng lô; `container_visits` giữ từng lượt lưu bãi. `booking_containers` nối cả ba ngữ cảnh với `bookings`. Các bảng này có vòng đời khác nhau. |
| Lịch hẹn và chuyến xe | `bookings` quản lý lịch hẹn ở cảng. `transport_trips` quản lý chuyến của tài xế; `booking_operations`, gate và EIR ghi vận hành. Tạo quan hệ có tên rõ giữa chuyến và lịch hẹn nếu xác minh được, đồng thời giữ dữ liệu chụp tại thời điểm phát sinh. |
| Hãng tàu, manifest và chứng từ | Giữ `shipping_lines` và `shipping_line_accounts` làm nguồn danh tính và quyền. Đối chiếu `shipping_manifests`/ `shipping_manifest_bills` với các bảng chứng từ và `np_sl_*` theo sự kiện đọc/ghi thực tế. Chỉ hợp nhất các bản ghi cùng ý nghĩa và cùng chủ sở hữu. |
| ePort, thanh toán và EIR | Phân biệt đăng ký dịch vụ, hóa đơn, giao dịch, lịch sử và chứng từ đã phát hành. Giữ khóa khớp nhà cung cấp thanh toán và tính bất biến của chứng từ. So sánh báo cáo tiền trước/sau theo đơn và trạng thái. |
| Reefer và dữ liệu vận hành theo thời gian | Các bản đọc nhiệt độ là dữ liệu theo thời gian, khoảng 553 nghìn dòng lúc kiểm kê. Đánh giá chỉ mục và chính sách giữ dữ liệu dựa trên truy vấn thực tế; giữ bản ghi cảnh báo và lịch sử cấp điện độc lập. |

UUID là khóa của các thực thể nghiệp vụ hiện có. Hai đầu của mỗi khóa ngoại dùng cùng kiểu. Giữ khóa tự nhiên của bảng danh mục, khóa ghép của quan hệ và sequence cho số thứ tự hoặc mã do bên tích hợp yêu cầu. Các UUID đang có được tái sử dụng khi chuyển bảng để URL, lịch sử vận hành và chứng từ vẫn trỏ tới đúng hồ sơ.

## Trình tự triển khai

1. **Ngăn lệch schema.** Quản lý DDL qua migrations trên DB dùng chung. Chặn Sequelize sync với Railway và Aiven. Seed Express chỉ nhập dữ liệu vào schema đã chuẩn bị.
2. **Chuyển một nhóm hoàn chỉnh.** Chụp cấu trúc Railway sang PostgreSQL cô lập. Đổi tất cả điểm gọi của nhóm, tạo dữ liệu mẫu, xác minh trên schema mới và thử quay lui. Bản thử đầu tiên là thống nhất ánh xạ Yard/Container về `public.containers` rồi chuyển bảng viết hoa sang schema lưu trữ.
3. **Chuẩn hóa quan hệ.** Kiểm tra quan hệ từ catalog, migrations và code. Với dữ liệu cũ chưa xác minh, dùng ràng buộc chặn lỗi mới nếu phù hợp và xử lý các dòng cũ theo ID gốc. Kiểm tra quyền truy cập chéo công ty khi thêm quan hệ.
4. **Hợp nhất mô hình chồng lấn.** Tách từng thay đổi thành bản mở rộng schema, điền dữ liệu, chuyển code đọc/ghi, đối chiếu và lưu trữ bảng cũ. Giữ API hiện có cho web và mobile trong giai đoạn chuyển đổi. Tránh hai dịch vụ ghi cùng một nghiệp vụ theo hai luồng riêng.
5. **Đóng vòng dữ liệu Di sản.** Sau khi một bảng cũ đã qua cả vòng vận hành cần thiết, xác minh từ logs, cron, workers, báo cáo và tích hợp. Chuyển sang schema lưu trữ trước. Chỉ xóa hẳn sau khi đã thử phục hồi và chốt thời gian lưu.

## Điều kiện chuyển một nhóm lên Railway

- Có danh sách API, tác vụ nền, màn hình và báo cáo bị ảnh hưởng, kèm owner của mỗi bảng.
- Đã kiểm tra tham chiếu, quyền truy cập và số lượng bản ghi trước/sau. Với nhóm tiền hoặc EIR, đối chiếu tổng tiền và danh tính chứng từ.
- Code cũ và code mới chạy được trong khoảng chuyển tiếp, hoặc có cửa sổ bảo trì và phương án quay lui đã thử. Việc đưa bảng cũ ra khỏi schema hoạt động thực hiện sau khi hoàn tất chuyển code.
- Mỗi thay đổi có backup, SQL chạy lại được hoặc kiểm tra trạng thái áp dụng, và ngưỡng dừng nếu khoá bảng hoặc lỗi API vượt dự kiến.

Mốc chuẩn sau đợt sửa quan hệ `20261120_059` còn 3 hồ sơ tài xế và 11 membership trỏ tới tài khoản cũ đã mất. Đây là dữ liệu lịch sử cần xác minh danh tính và trạng thái trước khi hợp nhất hoặc kích hoạt tài khoản.

## Đợt đầu và phần còn thiếu

- Script [lưu trữ bảng container cũ](../database/scripts/archive-legacy-container-table.sql) đã được chạy hai lần trên bản sao chỉ có schema Railway. Kiểm tra đảo chiều bằng `ALTER TABLE nexusport_archive."Containers" SET SCHEMA public` cũng đã chạy thành công trong một giao dịch diễn tập. Tại Railway, thực hiện bước lưu trữ sau khi các phiên bản backend dùng ánh xạ mới đã được triển khai.
- Railway ghi nhận 62 version. Có 10 version chưa tìm thấy file SQL trong checkout này: `20261029_026` và `20261119_050` đến `20261119_058`. Version reefer cũ `005` là alias đã được mô tả trong [tài liệu database](../database/README.md). Cần tìm commit hoặc file nguồn cho 10 bản thiếu trước khi xác nhận có thể dựng lại schema hãng tàu và đội xe từ migrations.

## Đợt dọn dữ liệu sau chuyển Aiven → Railway

- Migration `20261120_060` bỏ 99 bản sao giống hệt và phục hồi khóa cho `booking_dispatch_state`, `container_cargo_types`, `container_visit_access`, `eport_service_order_states` và `gate_transaction_containers`. Quan hệ `container_visit_access.granted_by` dùng `NOT VALID` để giữ nguyên chủ thể lịch sử chưa khôi phục và chặn tham chiếu sai mới.
- Dung lượng đo được khoảng 167 MiB, riêng `reefer_temperature_records` khoảng 118 MiB. Các bảng rỗng cộng lại khoảng 0,8 MiB. Không tìm thấy dòng nhiệt độ trùng theo toàn bộ nội dung ngoài UUID. Bản ghi có ID thiết bị và ghi chú của simulator là nhóm cần xử lý vì tần suất ghi lớn.
- Simulator tự động chuyển sang chế độ bật theo yêu cầu. Đặt `ReeferSimulator__Enabled=true` khi cần trình diễn trên môi trường demo. Sau khi cập nhật backend, xác nhận số dòng mới từ thiết bị mô phỏng ngừng tăng trên Railway.
- [Script xuất lịch sử mô phỏng](../database/scripts/export-reefer-simulator-history.cjs) chỉ đọc Railway, tạo bản gzip có checksum và kiểm tra lại số UUID. Nó giữ số đo bất thường, quanh cảnh báo và mới nhất mỗi phiên ra khỏi tập lưu trữ. Cần chốt chính sách giữ lịch sử trước khi xóa các dòng đã lưu khỏi DB. Đánh giá dung lượng có thể tái sử dụng sau dọn bằng VACUUM; chỉ cân nhắc thu nhỏ file vật lý trong cửa sổ bảo trì khi đủ dung lượng tạm.

## Thu gọn phạm vi cầu bến và giữ chỗ bãi

- Danh mục `public.berths` rỗng và không có khóa ngoại, view hay hàm đang tham chiếu trên Railway. Hai bảng `berth_assignments` và `berth_performance_snapshots` đã vắng mặt. Đã bỏ endpoint quản lý cầu bến chuyên biệt của Node/C# và mục quản trị trên web. [Script dọn danh mục](../database/scripts/retire-empty-berth-catalog.sql) chỉ cho phép xóa bảng rỗng và dùng `RESTRICT`. Chức năng tàu, chứng từ và điều phối bãi tiếp tục dùng bảng của từng nghiệp vụ.
- `persistent_yard_reservations` và `yard_slot_reservations` là ứng viên hợp nhất luồng giữ chỗ. Express hiện đọc cả hai. Trong 9 dòng từ bảng cũ, 8 dòng ánh xạ duy nhất được sang `yard_slots.id` và `containers.id`. Một dòng chưa tìm được ô tương ứng. Chưa thấy dòng nào đã có liên kết giữ chỗ tương ứng ở bảng đích, và 8 dòng ánh xạ được không xung đột với giữ chỗ đang mở. Cần xác minh dòng không khớp, dấu thời gian, mã booking và dữ liệu JSON hiện dùng ở màn hình bãi trước khi đổi luồng ghi.
- `slot_reservations` rỗng nhưng EF dùng cho công việc chuẩn bị theo `work_order_id`. Chỉ tính chung với giữ chỗ bãi sau khi thiết kế được vòng đời hết hạn và quan hệ tới lệnh công việc.
- `shipping_line_documents` và `bl_post_issuance_requests` rỗng và chưa thấy đọc/ghi trực tiếp trong checkout. Cả hai thuộc nhóm chứng từ thay đổi gần đây trên Railway. Giữ lại để đối chiếu code đang triển khai, nhất là các migration hãng tàu còn thiếu.
