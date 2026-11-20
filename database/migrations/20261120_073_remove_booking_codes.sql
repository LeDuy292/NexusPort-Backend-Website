BEGIN;

-- Hệ thống không dùng mã booking riêng: ePort khai báo dịch vụ hàng xuất theo số vận đơn.
-- Mỗi vận đơn xuất có một bản ghi nguồn (export_bookings.booking_no = số vận đơn), nên một bản khai
-- có thể có nhiều nguồn của cùng chủ hàng.
DROP INDEX IF EXISTS ux_export_bookings_manifest_shipper;
CREATE INDEX IF NOT EXISTS ix_export_bookings_manifest ON export_bookings (manifest_id) WHERE manifest_id IS NOT NULL;

-- Bỏ mã booking cũ đã ghi trên vận đơn.
UPDATE bills_of_lading SET booking_no = NULL WHERE booking_no IS NOT NULL;

INSERT INTO nexusport_schema_versions(version, description)
SELECT '20261120_073', 'Bỏ mã booking: ePort khai báo theo số vận đơn'
WHERE NOT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '20261120_073');

COMMIT;
