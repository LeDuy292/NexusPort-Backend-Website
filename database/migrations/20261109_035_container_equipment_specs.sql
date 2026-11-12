BEGIN;
ALTER TABLE containers
  ADD COLUMN IF NOT EXISTS shipping_line_id uuid REFERENCES shipping_lines(id),
  ADD COLUMN IF NOT EXISTS iso_size_type varchar(4) CHECK (iso_size_type ~ '^[A-Z0-9]{2}[A-Z][A-Z0-9]$'),
  ADD COLUMN IF NOT EXISTS equipment_description varchar(250),
  ADD COLUMN IF NOT EXISTS external_length_mm integer CHECK (external_length_mm BETWEEN 1 AND 99999),
  ADD COLUMN IF NOT EXISTS external_width_mm integer CHECK (external_width_mm BETWEEN 1 AND 99999),
  ADD COLUMN IF NOT EXISTS external_height_mm integer CHECK (external_height_mm BETWEEN 1 AND 99999),
  ADD COLUMN IF NOT EXISTS tare_weight_kg numeric(8,2) CHECK (tare_weight_kg > 0),
  ADD COLUMN IF NOT EXISTS max_gross_mass_kg numeric(8,2) CHECK (max_gross_mass_kg > tare_weight_kg),
  ADD COLUMN IF NOT EXISTS capacity_m3 numeric(7,3) CHECK (capacity_m3 > 0),
  ADD COLUMN IF NOT EXISTS manufacturer varchar(150);
CREATE INDEX IF NOT EXISTS ix_containers_shipping_line ON containers(shipping_line_id) WHERE shipping_line_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_containers_iso_size_type ON containers(iso_size_type) WHERE iso_size_type IS NOT NULL;
INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261109_035', 'Container equipment ratings, dimensions and shipping line ownership')
ON CONFLICT (version) DO NOTHING;
COMMIT;
