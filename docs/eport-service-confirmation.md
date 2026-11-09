# Người 3 — phần 3: kiểm tra và xác nhận đăng ký dịch vụ

Backend C# dùng cùng `ServiceDraftService` trong module ServiceCatalog. UI nằm trong
feature `service-catalog`; không bổ sung backend Node hoặc sửa module nguồn.

## Luồng sử dụng

Tại `/services`, chọn **Hồ sơ của tôi → Kiểm tra & xác nhận**, hoặc hoàn tất wizard
bằng **Lưu & kiểm tra**. Backend kiểm tra bản nháp đã lưu, trả đầy đủ các thông tin
còn thiếu, lưu ý và từng dòng phí. Người dùng xem số tiền rồi xác nhận. Có thể quay
lại chỉnh sửa nếu chưa xác nhận.

Xác nhận tạo hồ sơ riêng, giữ nguyên nội dung nguồn, điều kiện dịch vụ, đơn vị,
số lượng, biểu phí được chọn và đơn giá. Bản nháp đã xác nhận không được sửa lại;
thay đổi biểu phí sau đó không làm đổi hồ sơ lịch sử. GET không tính lại giá đã lưu.

## Kiểm tra phía server

- JWT phải khớp user active, role Transport Company/Carrier/Carrier Staff và đúng
  một công ty trong `carrier_users`. Không nhận company, giá hay trạng thái từ UI.
- Kiểm tra phiên bản nháp; đọc lại Booking và đúng occurrence container đã chọn.
  Loại bỏ container rỗng, nguồn bị hủy/từ chối, container không còn được cấp quyền.
- Hàng xuất: Booking cảng đã tiếp nhận, danh sách công bố, chuyến Scheduled,
  thời gian dự kiến còn ở tương lai và trong receive-from/CY cut-off, VGM dương,
  có seal. Trạng thái Hold/CustomsHold/Blocked/Inactive/DamagedUnsafe chặn xác nhận.
- Người liên hệ, điện thoại và thời gian dự kiến là bắt buộc khi xác nhận.
  Dịch vụ phải còn active và đúng luồng hàng. Điện lạnh cần đúng thiết bị lạnh.
- Các điều kiện tự do trong danh mục không được coi là quy tắc máy đã kiểm tra.
  Dịch vụ mới hoặc bộ điều kiện khác bộ tham chiếu chuyển sang cần cảng đối chiếu.
- Chưa thông quan là lưu ý tại đăng ký; vẫn phải kiểm tra khi qua cổng/xếp tàu.
  Xác nhận dịch vụ không cấp quyền đi cổng hoặc quyền nhận hàng.
- Dịch vụ tại bãi cần đúng lượt lưu bãi active, có started_at, chưa completed_at.
  Với dịch vụ kèm hạ xuất đăng ký trước khi vào bãi, hệ thống cho ghi nhận yêu cầu
  dự kiến ở trạng thái **Chờ cảng duyệt**, hiển thị điều kiện vào bãi còn chờ.
  Dịch vụ riêng cần ở bãi không được tự vượt qua điều kiện này.

## Tính phí của ứng dụng

Đây là quy tắc tính của dự án, không phải công bố biểu phí hay quy tắc thu tiền
của cảng Hải Phòng. Quản trị viên phải khai báo giá; không seed giá vận hành.

| Đơn vị | Số lượng tính phí |
| --- | --- |
| CONTAINER | 1 cho mỗi container được chọn |
| MOVE / WEIGH | Số lượt nguyên dương đã đăng ký |
| HOUR | Khoảng kết thúc trừ bắt đầu, làm tròn lên 0,01 giờ |
| DAY | Khoảng kết thúc trừ bắt đầu, làm tròn lên mỗi 24 giờ |

Giới hạn 10.000 đơn vị mỗi dòng. Giờ lưu UTC; ngày áp dụng giá tính theo UTC+7.
Dịch vụ theo giờ/ngày dùng ngày bắt đầu; các dịch vụ khác dùng ngày thực hiện
dự kiến đã được kiểm tra. Timestamp phụ trên dịch vụ CONTAINER/MOVE/WEIGH không
được dùng để chọn biểu phí khác ngày thực hiện.

Kích thước lấy từ container_types; fallback ISO nhận đúng 45G0 là 40ft high-cube.
Nhóm DG/OOG theo cargo được phép xem, thiết bị lạnh và loại container theo nguồn.
Không suy đoán cargo của công ty khác từ master vật lý. Thiếu nhóm/kích thước chặn giá.

Chọn giá active, đúng đơn vị/VND và ngày hiệu lực (bao gồm cả ngày kết thúc), ưu
tiên đúng size+equipment → đúng size → đúng equipment → ALL. Trùng hai biểu phí
cùng phạm vi chặn xác nhận. Thiếu giá trả amount/total null, không đổi thành giá 0.
Giá 0 chỉ hợp lệ khi có biểu phí 0 được khai báo rõ.

Thành tiền từng dòng làm tròn số nguyên VND, midpoint away-from-zero. Tổng bằng
tổng từng dòng, **chưa bao gồm thuế và các khoản ngoài danh mục**. Không phát hành
hóa đơn. Chặn số tiền vượt giới hạn hiển thị chính xác của trình duyệt
(9.007.199.254.740.991 VND), không cho duyệt số đã bị làm tròn sai trên UI.

## Trạng thái và giới hạn nguồn

| Trạng thái | Ý nghĩa |
| --- | --- |
| DRAFT | Bản nháp, có thể chỉnh sửa |
| READY / INCOMPLETE | Kết quả kiểm tra tạm thời, không phải trạng thái tác nghiệp |
| CONFIRMED | Đăng ký đã xác nhận và lưu giá |
| PENDING_APPROVAL | Đã lưu đăng ký/giá, còn cần cảng duyệt điều kiện/kế hoạch |
| UNPAID / NOT_REQUIRED | Trạng thái tiền riêng; không đánh dấu đã thanh toán |

Nguồn DO/eDO hiện chỉ là tham chiếu khai báo, chưa có dữ liệu cấp lệnh bền vững
liên kết công ty/container. Luồng nhập luôn báo `EDO_UNVERIFIED` và **chặn xác nhận**,
vẫn có thể tính dòng phí và giữ nháp. Không dùng danh sách eDO giả trong Node.

Bộ chọn container hàng xuất hiện chỉ nhận occurrence Expected của phần 2. Tạo dịch vụ tại bãi
riêng sau khi export GateIn cần mở rộng hợp đồng nguồn bằng đúng lượt lưu bãi;
chức năng hiện tại không đoán lượt mới nhất hoặc vượt qua hạn chế này.
Phần này chưa duyệt thay cảng, thu tiền, tạo lịch xe, cấp quyền cổng hay tác nghiệp.

## API và tính nguyên tử

Prefix `/api/v1/ServiceDrafts`, cùng phân quyền phần 2:

| API | Nội dung |
| --- | --- |
| POST `/{id}/validate` | `{expectedVersion}` → issues, fees, subtotal, total, canConfirm, quoteToken |
| POST `/{id}/confirm` | `{expectedVersion,quoteToken}` → hồ sơ đã lưu, số tiền, trạng thái |
| GET `/{id}/registration` | Snapshot lịch sử; không đối chiếu giá hiện tại |

GET nháp/danh sách thêm registrationId, trạng thái thực tế và số tiền sau xác nhận.
PUT nháp sau xác nhận trả 409, kể cả phát lại request tạo nháp ban đầu.

Token kiểm tra gắn với nội dung, phiên bản, nguồn hiện tại, dịch vụ và biểu phí
được chọn. Khi xác nhận, server khóa hồ sơ và các nguồn/giá liên quan, kiểm tra
lại và so token. Thay đổi giá/nguồn hợp lệ trả 409 yêu cầu xem lại; thiếu điều kiện
trả 422. Cùng token và phiên bản retry trả lại một đăng ký duy nhất. Tenant khác
trả 404. Dữ liệu nguồn chỉ được đọc.

Migration additive/idempotent `20261109_037_eport_service_registrations.sql` tạo
bảng riêng và trigger chống UPDATE/DELETE snapshot. Không sửa 035/036 hay dữ liệu
Booking/container cũ. Triển khai 037 và khởi động lại backend C# để nạp endpoint.

## Kiểm chứng

`csharp-core/tests/ServiceRegistrationChecks` chạy controller C# thật với PostgreSQL
riêng, chỉ cho phép `127.0.0.1:55445/eport_registration_check`, user registration_check.
`REGISTRATION_TEST_CONNECTION`, `REGISTRATION_TEST_MARKER` phải khớp custom setting
`nexusport.registration_test`. Không dùng fixture trên database dự án.

Chạy `dotnet run --project csharp-core/tests/ServiceRegistrationChecks`; `--serve`
giữ HTTP 5512 cho browser QA. Kiểm tra tenant, giá do server tính, thiếu điều kiện,
thời gian/ngày giá, giá 0, tariff ưu tiên, eDO chưa xác minh, retry/concurrent confirm,
khóa sửa và giữ lịch sử sau khi thay nguồn/biểu phí. Fingerprint xác nhận không
thay đổi dữ liệu nghiệp vụ nguồn.
