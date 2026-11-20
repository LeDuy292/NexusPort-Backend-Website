BEGIN;
-- Số e-D/O tuần tự do DB cấp, không đếm theo độ dài mảng trong bộ nhớ như trước.
CREATE SEQUENCE IF NOT EXISTS delivery_order_number_seq START WITH 1000;
INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261119_051', 'Sequence đánh số e-D/O')
ON CONFLICT(version) DO NOTHING;
COMMIT;
