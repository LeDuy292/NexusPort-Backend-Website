BEGIN;

-- 1 bản khai (manifest) = 1 booking; 1 booking có nhiều vận đơn (bills_of_lading.booking_no).
ALTER TABLE export_bookings ADD COLUMN IF NOT EXISTS manifest_id uuid;
CREATE UNIQUE INDEX IF NOT EXISTS ux_export_bookings_manifest ON export_bookings (manifest_id) WHERE manifest_id IS NOT NULL;

INSERT INTO nexusport_schema_versions(version, description)
SELECT '20261120_071', 'Booking xuất gắn với đúng một bản khai (manifest_id)'
WHERE NOT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '20261120_071');

COMMIT;
