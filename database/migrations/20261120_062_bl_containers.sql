BEGIN;

-- Số vận đơn phải duy nhất theo từng chiều (cùng một B/L có bản xuất và bản nhập).
CREATE UNIQUE INDEX IF NOT EXISTS ux_bills_of_lading_bl_number ON bills_of_lading (upper(bl_number), lower(direction));

-- Container của vận đơn: bảng con có khóa chính/ngoại, đồng bộ từ cột JSON bills_of_lading.containers (code hiện tại vẫn ghi JSON).
CREATE TABLE IF NOT EXISTS bl_containers (
  bl_id           varchar(255) NOT NULL REFERENCES bills_of_lading(id) ON DELETE CASCADE,
  container_no    varchar(11)  NOT NULL,
  seal_no         varchar(50),
  container_type  varchar(20),
  tare_weight_kg  numeric(12,2),
  gross_weight_kg numeric(12,2),
  vgm_kg          numeric(12,2),
  direction       varchar(10) NOT NULL DEFAULT 'export',
  active          boolean NOT NULL DEFAULT true,
  PRIMARY KEY (bl_id, container_no)
);
-- Một container chỉ nằm trên một vận đơn còn hiệu lực.
CREATE UNIQUE INDEX IF NOT EXISTS ux_bl_containers_active ON bl_containers (container_no, direction) WHERE active;

CREATE OR REPLACE FUNCTION np_sync_bl_containers() RETURNS trigger AS $$
BEGIN
  DELETE FROM bl_containers WHERE bl_id = NEW.id;
  INSERT INTO bl_containers (bl_id, container_no, seal_no, container_type, tare_weight_kg, gross_weight_kg, vgm_kg, direction, active)
  SELECT DISTINCT ON (upper(e ->> 'containerNo')) NEW.id, upper(e ->> 'containerNo'), e ->> 'sealNo', e ->> 'type',
         NULLIF(e ->> 'tareWeight', '')::numeric, NULLIF(e ->> 'grossWeight', '')::numeric, NULLIF(e ->> 'vgm', '')::numeric,
         lower(coalesce(NEW.direction, 'export')), upper(coalesce(NEW.status, '')) NOT IN ('CANCELLED', 'VOID')
    FROM jsonb_array_elements(coalesce(NEW.containers, '[]'::jsonb)) e
   WHERE coalesce(e ->> 'containerNo', '') <> '';
  RETURN NULL;
END $$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_bills_of_lading_sync_containers ON bills_of_lading;
CREATE TRIGGER trg_bills_of_lading_sync_containers AFTER INSERT OR UPDATE OF containers, status, direction ON bills_of_lading
  FOR EACH ROW EXECUTE FUNCTION np_sync_bl_containers();

-- Điền cho dữ liệu hiện có.
UPDATE bills_of_lading SET containers = containers;

INSERT INTO nexusport_schema_versions(version, description)
SELECT '20261120_062', 'bl_containers (bảng con) + số B/L duy nhất'
WHERE NOT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '20261120_062');

COMMIT;
