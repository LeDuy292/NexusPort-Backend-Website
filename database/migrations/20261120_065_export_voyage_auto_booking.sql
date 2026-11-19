BEGIN;

-- Chuyến xuất tạo tự động từ bản khai chỉ biết ETA tại Cảng Tiên Sa, chưa biết ETA tại cảng dỡ:
-- cho phép để trống ETA cảng dỡ thay vì điền ngày không có thật; nếu có thì vẫn phải sau ETD.
ALTER TABLE export_voyages ALTER COLUMN eta DROP NOT NULL;
ALTER TABLE export_voyages DROP CONSTRAINT IF EXISTS export_voyages_check;
ALTER TABLE export_voyages ADD CONSTRAINT export_voyages_check CHECK (
  receive_from < cy_cutoff AND cy_cutoff < etd AND vgm_cutoff <= etd AND si_cutoff <= etd AND (eta IS NULL OR eta > etd)
);

-- Mã chuyến xuất phải duy nhất.
CREATE UNIQUE INDEX IF NOT EXISTS ux_export_voyages_voyage_number ON export_voyages (upper(voyage_number));

INSERT INTO nexusport_schema_versions(version, description)
SELECT '20261120_065', 'Chuyến xuất: ETA cảng dỡ có thể trống, mã chuyến duy nhất (tự tạo booking khi phát hành B/L)'
WHERE NOT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '20261120_065');

COMMIT;
