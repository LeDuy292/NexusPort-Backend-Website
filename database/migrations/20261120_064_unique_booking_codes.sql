BEGIN;

-- Mã booking xuất phải duy nhất (không phân biệt hoa thường) và luôn có giá trị.
ALTER TABLE export_bookings ALTER COLUMN booking_no SET NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_export_bookings_booking_no ON export_bookings (upper(booking_no));

-- booking_number là cột trùng nghĩa với booking_no, không còn code nào đọc/ghi và đang chứa giá trị trùng.
ALTER TABLE export_bookings DROP COLUMN IF EXISTS booking_number;

-- Mã lịch hẹn (booking cổng) cũng phải duy nhất.
CREATE UNIQUE INDEX IF NOT EXISTS ux_bookings_booking_code ON bookings (upper(booking_code)) WHERE booking_code IS NOT NULL;

INSERT INTO nexusport_schema_versions(version, description)
SELECT '20261120_064', 'Mã booking xuất và mã lịch hẹn duy nhất; bỏ cột trùng export_bookings.booking_number'
WHERE NOT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '20261120_064');

COMMIT;
