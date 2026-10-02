# Đối chiếu schema NexusPort với EIR Cảng Tiên Sa

## Kết luận

Schema gốc quản lý tốt container, booking, gate, yard và camera, nhưng đặt nhiều
giá trị thay đổi theo chuyến (`seal_no`, `gross_weight_kg`, `vessel_call_id`,
`arrived_at`, `left_at`) trực tiếp trên `containers`. Hai EIR của cùng
`HAMU2515680` chứng minh các giá trị này không phải master data và không thể bị
ghi đè sau mỗi lần container quay lại cảng.

Migration mới thêm ranh giới dữ liệu sau:

```text
containers (danh tính vật lý)
  └─ container_visits (một lần/chặng khai thác tại cảng)
       └─ eir_transactions (ảnh chụp pháp lý/nghiệp vụ của lần giao nhận)
            ├─ eir_seals
            ├─ eir_damage_observations
            └─ eir_media
```

## Ánh xạ dữ liệu cảng

| Trường EIR Tiên Sa | Nơi lưu mới | Ghi chú |
|---|---|---|
| CONTAINER | `containers.container_no` qua `container_visits.container_id` | Một container có nhiều visit/EIR |
| REFERENCE | `eir_transactions.reference_no` | Unique |
| ACTIVITY In/Out | `eir_transactions.activity` | Không đồng nhất với lifecycle status |
| LOCATION | `location_code`, tùy chọn `yard_slot_id` | Giữ cả chuỗi nguồn và liên kết slot chuẩn hóa |
| OPERATOR | `operator_code`, `operator_name` | Snapshot theo phiếu |
| CUSTOMER | `customer_name` | Snapshot, không phụ thuộc master đổi tên |
| VALID TO | `valid_to` | Có thể null như phiếu In |
| CHECK IN/OUT | `check_in_at`, `check_out_at` | Có constraint thứ tự thời gian |
| BL/Booking No | `bl_booking_no` | Không ép là booking nội bộ |
| FULL/EMPTY | `load_status` | Tách khỏi `container_status` |
| ISO / TYPE / SIZE | Các cột snapshot tương ứng | `45G0` và 40 feet không bị trộn |
| SEAL 1/2 | `eir_seals` | 0..n seal, giữ `raw_value` như `/ko` |
| WEIGHT | `gross_weight_kg` | Theo EIR/visit, không ghi đè master |
| SOUND/DAMAGE | `sound_damage_code` và `eir_damage_observations` | Mã nguồn được giữ nguyên |
| REMARK | `remark`; damage có cấu trúc riêng | Hỗ trợ móp trần 50x50 cm |
| IMDG/TEMP/VENT | Các cột EIR tương ứng | Nullable |
| VESSEL/VOYAGE/DATE | Snapshot vessel/voyage/time | Vẫn có thể liên kết `vessel_call_id` ở visit |
| TRANS COMPANY | `transport_company_name` | Snapshot lịch sử |
| PLATE NO | `truck_id` + `plate_number` | Liên kết tùy chọn, chuỗi snapshot bắt buộc giữ lịch sử |
| EMPTY RETURN PLACE | `empty_return_place` | Nullable |
| Chữ ký | Hai cột signatory | Có thể bổ sung file chữ ký trong `eir_media` |

## Hình ảnh và video

Schema gốc đã có `gate_media`, `ocr_results`, `plate_image_url` và
`overview_image_url`, nhưng chưa mô tả ảnh chụp đối tượng nào, ở giai đoạn nào,
góc nào và thuộc EIR nào.

`eir_media` bổ sung:

- Đối tượng: vehicle, container, seal, damage, document, overview, driver.
- Loại file: image, video, document.
- Giai đoạn: approach, check-in, inspection, yard, check-out, departure, manual,
  import.
- Hướng chuyển động In/Out, góc chụp tự do, camera và thứ tự ảnh.
- S3 bucket/object key bền vững; presigned URL chỉ là giá trị tùy chọn vì sẽ hết hạn.
- MIME, kích thước, SHA-256.
- Kết quả OCR/AI dưới dạng trường chuẩn và JSON mở rộng.

Các bảng ảnh cũ được thêm `eir_transaction_id` để có thể chuyển đổi dần, không
cần xóa dữ liệu hoặc buộc code hiện tại đổi ngay.

## Quyết định tương thích

- Không xóa hoặc đổi kiểu cột hiện có.
- Không tự suy diễn `/ko`, mã `S` hoặc chuỗi cổng bị lỗi font; luôn giữ raw data.
- Không đưa ảnh binary vào PostgreSQL.
- Không dùng presigned S3 URL làm định danh lâu dài của ảnh/video.
- File sample tách khỏi migration production để không làm bẩn dữ liệu thật.
- Chưa thêm unique index cho vị trí hiện tại vì DB đang dùng có thể đã tồn tại
  duplicate. File verify phát hiện vấn đề này để làm sạch trước khi siết constraint.

## Những việc cố ý để dành cho giai đoạn code

- Chuyển API đọc/ghi từ các cột legacy sang visit/EIR.
- Cập nhật Gate In và Gate Out theo semantics riêng.
- Import ảnh hiện có sang `eir_media` hoặc bridge bằng khóa EIR.
- Chuẩn hóa ý nghĩa mã `/ko`, `S` và tên lane/gate sau khi cảng xác nhận.
