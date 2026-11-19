BEGIN;

-- Booking (1) — (n) B/L; Manifest (1) — (n) B/L thuộc nhiều booking / nhiều chủ hàng trên cùng chuyến.
-- manifest_id chỉ ghi bản khai đã tạo booking tự động; trong một bản khai mỗi chủ hàng có tối đa một booking tự tạo.
DROP INDEX IF EXISTS ux_export_bookings_manifest;
CREATE UNIQUE INDEX IF NOT EXISTS ux_export_bookings_manifest_shipper ON export_bookings (manifest_id, carrier_id) WHERE manifest_id IS NOT NULL;

INSERT INTO nexusport_schema_versions(version, description)
SELECT '20261120_067', 'Booking theo chủ hàng trong bản khai (bỏ quy tắc 1 bản khai = 1 booking)'
WHERE NOT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '20261120_067');

COMMIT;
