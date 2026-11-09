# Người 3 — đăng ký dịch vụ, phần 2: lưu nháp

Backend C# nằm cùng module `ServiceCatalog`; frontend nằm trong feature
`src/features/service-catalog`. Danh mục/biểu phí phần 1 được giữ nguyên.

Phần 3 kiểm tra, tính phí và xác nhận từ nháp được mô tả tại
[eport-service-confirmation.md](eport-service-confirmation.md). Sau xác nhận,
GET/danh sách trả trạng thái đăng ký và nháp được khóa chỉnh sửa; nội dung bên
dưới mô tả việc chuẩn bị hồ sơ trước khi xác nhận.

Ô chọn Booking dùng dropdown tìm kiếm theo số Booking, tàu hoặc chuyến; mỗi mục
hiển thị số container và CY cut-off. Chọn một mục sẽ lấy container đúng nguồn.
API chỉ trả Booking có liên kết công ty được kiểm chứng. Nếu bảng Booking thiếu
liên kết `carrier_id` hoặc các trường nguồn bắt buộc, API trả 503 với mã
`SERVICE_SOURCE_SCHEMA_MISMATCH`; cần khôi phục hợp đồng dữ liệu Booking. Việc có
biên nhận migration 035–037 không chứng minh schema nguồn chưa bị thay đổi sau đó.
Không tự ánh xạ quyền công ty từ tên chủ hàng hoặc membership hiện tại của người tạo.

Migration 038 và dữ liệu phát triển có thể lưu/xác nhận thật được hướng dẫn tại
[eport-service-sources.md](eport-service-sources.md). Bộ `DEMO-EPORT-*` trong database
khác với chế độ xem thử UI bên dưới.

Khi chạy frontend bằng `npm run dev`, wizard mới có nút **Nạp dữ liệu mẫu** ngay
trong phần Booking hãng tàu. Bộ mẫu UI gồm 3 Booking `MAU-EXP-001/002/003` và 6
container có hàng (khô 20/40 feet, lạnh 20/40 feet, hàng nguy hiểm, 45 feet).
Người dùng có thể chọn và đi qua bốn bước để xem hồ sơ. Chế độ có nhãn Dữ liệu mẫu,
không gọi API nguồn/container và không cho lưu nháp hoặc gửi xác nhận. Chọn
**Dùng dữ liệu thật** để bỏ các lựa chọn mẫu và trở lại nguồn API. Không thay đổi
database, không tự bật khi API lỗi và không xuất hiện trong bản build production
hoặc khi mở hồ sơ thật đã lưu. Đây không phải dữ liệu seed để thử thanh toán/tác nghiệp.

## Trải nghiệm

Khách hàng Transport Company, Carrier hoặc Carrier Staff mở `/services`, chọn **Tạo đăng ký**
hoặc **Đăng ký dịch vụ** trong khung chi tiết. Wizard gồm hồ sơ nguồn, chọn container,
dịch vụ/thời gian và kiểm tra nháp. Có thể lưu chưa hoàn tất ở bất kỳ bước nào.
**Bản nháp của tôi** mở lại hồ sơ đã lưu của công ty; các nhân viên trong cùng công ty
có thể tiếp tục với kiểm tra phiên bản chống ghi đè.

Mỗi container có các dịch vụ riêng; dịch vụ chính xuất hiện đúng một lần. Có thể
sao chép lựa chọn/thời gian sang các container khác sau khi xem xác nhận. Điện lạnh
không được áp dụng cho container được xác định là loại khô. Giờ/ngày được lưu theo
mốc thời gian, chưa tính số đơn vị tính tiền hoặc áp dụng quy tắc làm tròn.

## Nguồn và quyền

Tenant được lấy từ user active và đúng một membership `carrier_users`; không nhận
quyền truy cập từ company ID hoặc nhãn/snapshot do trình duyệt gửi.

- Xuất: Booking hãng tàu của công ty, được cảng tiếp nhận và đã công bố container.
  Mỗi lựa chọn tham chiếu **export booking container occurrence ID**, không đoán
  từ container vật lý hoặc lịch xe. Chỉ đưa container có hàng vào picker.
- Nhập: nguồn eDO hiện tại của module hãng tàu chưa có liên kết tenant bền vững để
  sử dụng như dữ liệu đã xác minh. Nháp cho khai báo DO/eDO/B/L và luôn ghi
  `UNVERIFIED` / **Chờ đối chiếu**. Container lấy từ lượt lưu bãi có hàng được quyền
  truy cập, ở trạng thái planned/active; chưa khẳng định container đã đến bãi hoặc
  thuộc DO/eDO vừa khai báo. Dữ liệu cargo/seal của công ty khác và pickup code
  không được tiết lộ qua picker hay snapshot nháp.

Các thời điểm từ UI dùng giờ địa phương, API yêu cầu ISO có offset hoặc Z và lưu
UTC. Hạn DO/eDO là ngày `yyyy-MM-dd`. Thời gian kết thúc, nếu nhập cả hai mốc,
phải sau thời gian bắt đầu. Giới hạn 50 container và 12 dịch vụ/container.

## API

Prefix `/api/v1/ServiceDrafts`, chỉ role Transport Company, Carrier, Carrier Staff.

| HTTP | Đường dẫn | Chức năng |
| --- | --- | --- |
| GET | `/sources?flow=EXPORT&search=...` | Booking nguồn của công ty, tối đa 50 |
| GET | `/sources?flow=IMPORT` | Mô tả chế độ khai báo DO/eDO chờ đối chiếu |
| GET | `/containers?flow=EXPORT&sourceId=...&search=...` | Container đúng Booking |
| GET | `/containers?flow=IMPORT&search=...` | Lượt container có hàng trong phạm vi quyền |
| GET | `/?search=...&page=1&pageSize=20` | Nháp riêng của công ty, tối đa 50/trang |
| GET | `/{id}` | Nội dung và snapshot máy chủ của bản nháp |
| PUT | `/{id}` | Tạo/cập nhật nháp bằng UUID cố định và expectedVersion |

`expectedVersion=0` tạo mới; retry cùng UUID/nội dung trả lại nháp đã lưu. Cập nhật
cần version hiện tại; ghi đè phiên bản hoặc retry khác nội dung trả 409. Truy cập
nháp/Booking của công ty khác trả 404. Lỗi schema thiếu migration trả 503.

Nội dung gồm flow, mainServiceId, source, plannedAt, contactName, contactPhone,
note và lines. Mỗi line có sourceContainerId và services; mỗi service lưu serviceId,
startsAt, endsAt, quantity nếu có. Snapshot nguồn/container được resolve tại server.

## Phạm vi và triển khai

Migration additive `20261108_036_eport_service_drafts.sql` tạo duy nhất bảng
`eport_service_drafts` và index; không seed hoặc cập nhật dữ liệu nghiệp vụ cũ.
Không sửa migration đã áp dụng. Cần 035 và các migration nguồn Booking trước đó.
Đăng ký `ServiceDraftService` trong API; controller được nạp cùng assembly phần 1.
Khởi động lại C# sau khi build để nạp API mới; không cần đổi appsettings/proxy.

Mọi hồ sơ phần này chỉ có trạng thái **DRAFT**. Lưu nháp không giữ container, đặt lịch
xe, cấp quyền nhận hàng/qua cổng, tính hoặc chốt phí, gửi duyệt, thu tiền hay cập nhật
tác nghiệp. Các điều kiện phải được kiểm tra lại khi triển khai bước xác nhận tiếp theo.

## Kiểm chứng

`csharp-core/tests/ServiceDraftChecks` chạy HTTP C# thật với PostgreSQL riêng. Checker
chỉ chấp nhận `127.0.0.1:55444/eport_drafts_check`, user `draft_check` và marker
`nexusport.drafts_test` khớp biến môi trường. Truyền `SERVICE_DRAFT_TEST_CONNECTION`
và `SERVICE_DRAFT_TEST_MARKER`; chạy project bằng `dotnet run`. `--serve` giữ API
fixture tại 5510 để browser QA. Không chạy fixture trên DB dùng chung.

Kiểm tra bao gồm auth/tenant, IDOR, company spoofing, snapshot riêng tư, không lấy
container rỗng, lưu/mở lại nháp, retry, phiên bản/cập nhật đồng thời, UTC, dịch vụ
không tương thích và eDO chưa xác minh. Fingerprint xác nhận Booking/container/
declaration/visit/access nguồn không bị thay đổi.
