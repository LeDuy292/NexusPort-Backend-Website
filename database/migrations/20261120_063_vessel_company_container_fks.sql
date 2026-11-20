BEGIN;

-- ── 1) Tàu: tên tàu dạng chữ ở 8 bảng -> vessel_id trỏ vessels ───────────────────────────────────────────
CREATE UNIQUE INDEX IF NOT EXISTS ux_vessels_name ON vessels (upper(btrim(name)));

CREATE OR REPLACE FUNCTION np_resolve_vessel(p_name text) RETURNS uuid AS $$
DECLARE v uuid;
BEGIN
  IF p_name IS NULL OR btrim(p_name) = '' THEN RETURN NULL; END IF;
  SELECT id INTO v FROM vessels WHERE upper(btrim(name)) = upper(btrim(p_name));
  IF v IS NULL THEN
    INSERT INTO vessels (id, name) VALUES (gen_random_uuid(), btrim(p_name)) ON CONFLICT DO NOTHING;
    SELECT id INTO v FROM vessels WHERE upper(btrim(name)) = upper(btrim(p_name));
  END IF;
  RETURN v;
END $$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION np_fill_vessel_id() RETURNS trigger AS $$
BEGIN
  IF NEW.vessel_name IS NOT NULL AND (TG_OP = 'INSERT' OR NEW.vessel_name IS DISTINCT FROM OLD.vessel_name OR NEW.vessel_id IS NULL) THEN
    NEW.vessel_id := np_resolve_vessel(NEW.vessel_name);
  END IF;
  RETURN NEW;
END $$ LANGUAGE plpgsql;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['bills_of_lading','delivery_orders','export_voyages','import_shipments','shipment_pre_alerts',
                           'yard_gate_out_requests','preparation_tasks','unclaimed_consignee_queue'] LOOP
    EXECUTE format('ALTER TABLE %I ADD COLUMN IF NOT EXISTS vessel_id uuid', t);
    EXECUTE format('UPDATE %I SET vessel_id = np_resolve_vessel(vessel_name) WHERE vessel_name IS NOT NULL AND vessel_id IS NULL', t);
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_' || t || '_vessel') THEN
      EXECUTE format('ALTER TABLE %I ADD CONSTRAINT %I FOREIGN KEY (vessel_id) REFERENCES vessels(id)', t, 'fk_' || t || '_vessel');
    END IF;
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I (vessel_id)', 'ix_' || t || '_vessel', t);
    EXECUTE format('DROP TRIGGER IF EXISTS trg_%s_fill_vessel ON %I', t, t);
    EXECUTE format('CREATE TRIGGER trg_%s_fill_vessel BEFORE INSERT OR UPDATE OF vessel_name ON %I FOR EACH ROW EXECUTE FUNCTION np_fill_vessel_id()', t, t);
  END LOOP;
END $$;

-- ── 2) Công ty: người gửi/nhận hàng đã đăng ký (carriers) nhận diện qua mã số thuế ───────────────────────────
ALTER TABLE bills_of_lading ADD COLUMN IF NOT EXISTS shipper_company_id uuid;
ALTER TABLE bills_of_lading ADD COLUMN IF NOT EXISTS consignee_company_id uuid;
ALTER TABLE delivery_orders ADD COLUMN IF NOT EXISTS consignee_company_id uuid;

CREATE OR REPLACE FUNCTION np_fill_company_ids() RETURNS trigger AS $$
BEGIN
  IF TG_TABLE_NAME = 'bills_of_lading' THEN
    IF NEW.shipper_tax_code IS NOT NULL AND (TG_OP = 'INSERT' OR NEW.shipper_tax_code IS DISTINCT FROM OLD.shipper_tax_code OR NEW.shipper_company_id IS NULL) THEN
      SELECT id INTO NEW.shipper_company_id FROM carriers WHERE btrim(tax_code) = btrim(NEW.shipper_tax_code) LIMIT 1;
    END IF;
  END IF;
  IF NEW.consignee_tax_code IS NOT NULL AND (TG_OP = 'INSERT' OR NEW.consignee_tax_code IS DISTINCT FROM OLD.consignee_tax_code OR NEW.consignee_company_id IS NULL) THEN
    SELECT id INTO NEW.consignee_company_id FROM carriers WHERE btrim(tax_code) = btrim(NEW.consignee_tax_code) LIMIT 1;
  END IF;
  RETURN NEW;
END $$ LANGUAGE plpgsql;

UPDATE bills_of_lading b SET shipper_company_id = c.id FROM carriers c WHERE b.shipper_company_id IS NULL AND btrim(c.tax_code) = btrim(b.shipper_tax_code);
UPDATE bills_of_lading b SET consignee_company_id = c.id FROM carriers c WHERE b.consignee_company_id IS NULL AND btrim(c.tax_code) = btrim(b.consignee_tax_code);
UPDATE delivery_orders d SET consignee_company_id = c.id FROM carriers c WHERE d.consignee_company_id IS NULL AND btrim(c.tax_code) = btrim(d.consignee_tax_code);

DO $$
DECLARE r record;
BEGIN
  FOR r IN SELECT * FROM (VALUES ('bills_of_lading','shipper_company_id'),('bills_of_lading','consignee_company_id'),('delivery_orders','consignee_company_id')) v(t, c) LOOP
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_' || r.t || '_' || r.c) THEN
      EXECUTE format('ALTER TABLE %I ADD CONSTRAINT %I FOREIGN KEY (%I) REFERENCES carriers(id)', r.t, 'fk_' || r.t || '_' || r.c, r.c);
    END IF;
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I (%I)', 'ix_' || r.t || '_' || r.c, r.t, r.c);
  END LOOP;
END $$;
DROP TRIGGER IF EXISTS trg_bills_of_lading_fill_company ON bills_of_lading;
CREATE TRIGGER trg_bills_of_lading_fill_company BEFORE INSERT OR UPDATE OF shipper_tax_code, consignee_tax_code ON bills_of_lading
  FOR EACH ROW EXECUTE FUNCTION np_fill_company_ids();
DROP TRIGGER IF EXISTS trg_delivery_orders_fill_company ON delivery_orders;
CREATE TRIGGER trg_delivery_orders_fill_company BEFORE INSERT OR UPDATE OF consignee_tax_code ON delivery_orders
  FOR EACH ROW EXECUTE FUNCTION np_fill_company_ids();

-- ── 3) Bảng con container cho import_shipments, shipment_pre_alerts, delivery_orders (đồng bộ từ cột JSON) ──────
CREATE TABLE IF NOT EXISTS import_shipment_containers (
  shipment_id     uuid        NOT NULL REFERENCES import_shipments(id) ON DELETE CASCADE,
  container_no    varchar(11) NOT NULL,
  iso_type        varchar(20),
  seal_no         varchar(50),
  gross_weight_kg numeric(12,2),
  PRIMARY KEY (shipment_id, container_no)
);
CREATE TABLE IF NOT EXISTS pre_alert_containers (
  pre_alert_id uuid        NOT NULL REFERENCES shipment_pre_alerts(id) ON DELETE CASCADE,
  container_no varchar(11) NOT NULL,
  iso_type     varchar(20),
  seal_no      varchar(50),
  vgm_kg       numeric(12,2),
  PRIMARY KEY (pre_alert_id, container_no)
);
CREATE TABLE IF NOT EXISTS delivery_order_containers (
  delivery_order_id uuid        NOT NULL REFERENCES delivery_orders(id) ON DELETE CASCADE,
  container_no      varchar(11) NOT NULL,
  container_type    varchar(20),
  seal_no           varchar(50),
  tare_weight_kg    numeric(12,2),
  gross_weight_kg   numeric(12,2),
  cargo_weight_kg   numeric(12,2),
  temperature_c     numeric(6,2),
  cargo_description text,
  PRIMARY KEY (delivery_order_id, container_no)
);

CREATE OR REPLACE FUNCTION np_sync_import_shipment_containers() RETURNS trigger AS $$
BEGIN
  DELETE FROM import_shipment_containers WHERE shipment_id = NEW.id;
  INSERT INTO import_shipment_containers (shipment_id, container_no, iso_type, seal_no, gross_weight_kg)
  SELECT DISTINCT ON (upper(coalesce(e ->> 'containerNumber', e ->> 'containerNo'))) NEW.id, upper(coalesce(e ->> 'containerNumber', e ->> 'containerNo')),
         coalesce(e ->> 'isoType', e ->> 'type'), coalesce(e ->> 'sealNumber', e ->> 'sealNo'), NULLIF(coalesce(e ->> 'grossWeightKg', e ->> 'grossWeight'), '')::numeric
    FROM jsonb_array_elements(coalesce(NEW.containers, '[]'::jsonb)) e
   WHERE coalesce(e ->> 'containerNumber', e ->> 'containerNo', '') <> '';
  RETURN NULL;
END $$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION np_sync_pre_alert_containers() RETURNS trigger AS $$
BEGIN
  DELETE FROM pre_alert_containers WHERE pre_alert_id = NEW.id;
  INSERT INTO pre_alert_containers (pre_alert_id, container_no, iso_type, seal_no, vgm_kg)
  SELECT DISTINCT ON (upper(coalesce(e ->> 'containerNumber', e ->> 'containerNo'))) NEW.id, upper(coalesce(e ->> 'containerNumber', e ->> 'containerNo')),
         coalesce(e ->> 'isoType', e ->> 'type'), coalesce(e ->> 'sealNumber', e ->> 'sealNo'), NULLIF(coalesce(e ->> 'vgmKg', e ->> 'vgm'), '')::numeric
    FROM jsonb_array_elements(coalesce(NEW.containers, '[]'::jsonb)) e
   WHERE coalesce(e ->> 'containerNumber', e ->> 'containerNo', '') <> '';
  RETURN NULL;
END $$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION np_sync_delivery_order_containers() RETURNS trigger AS $$
BEGIN
  DELETE FROM delivery_order_containers WHERE delivery_order_id = NEW.id;
  INSERT INTO delivery_order_containers (delivery_order_id, container_no, container_type, seal_no, tare_weight_kg, gross_weight_kg, cargo_weight_kg, temperature_c, cargo_description)
  SELECT DISTINCT ON (upper(coalesce(e ->> 'containerNo', e ->> 'containerNumber'))) NEW.id, upper(coalesce(e ->> 'containerNo', e ->> 'containerNumber')),
         e ->> 'type', coalesce(e ->> 'sealNo', e ->> 'sealNumber'), NULLIF(e ->> 'tareWeight', '')::numeric, NULLIF(e ->> 'grossWeight', '')::numeric,
         NULLIF(e ->> 'cargoWeight', '')::numeric, NULLIF(e ->> 'temperature', '')::numeric, e ->> 'cargoDescription'
    FROM jsonb_array_elements(coalesce(NEW.containers, '[]'::jsonb)) e
   WHERE coalesce(e ->> 'containerNo', e ->> 'containerNumber', '') <> '';
  RETURN NULL;
END $$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_import_shipments_sync_containers ON import_shipments;
CREATE TRIGGER trg_import_shipments_sync_containers AFTER INSERT OR UPDATE OF containers ON import_shipments
  FOR EACH ROW EXECUTE FUNCTION np_sync_import_shipment_containers();
DROP TRIGGER IF EXISTS trg_shipment_pre_alerts_sync_containers ON shipment_pre_alerts;
CREATE TRIGGER trg_shipment_pre_alerts_sync_containers AFTER INSERT OR UPDATE OF containers ON shipment_pre_alerts
  FOR EACH ROW EXECUTE FUNCTION np_sync_pre_alert_containers();
DROP TRIGGER IF EXISTS trg_delivery_orders_sync_containers ON delivery_orders;
CREATE TRIGGER trg_delivery_orders_sync_containers AFTER INSERT OR UPDATE OF containers ON delivery_orders
  FOR EACH ROW EXECUTE FUNCTION np_sync_delivery_order_containers();

UPDATE import_shipments SET containers = containers;
UPDATE shipment_pre_alerts SET containers = containers;
UPDATE delivery_orders SET containers = containers;

CREATE INDEX IF NOT EXISTS ix_import_shipment_containers_no ON import_shipment_containers (container_no);
CREATE INDEX IF NOT EXISTS ix_pre_alert_containers_no ON pre_alert_containers (container_no);
CREATE INDEX IF NOT EXISTS ix_delivery_order_containers_no ON delivery_order_containers (container_no);

INSERT INTO nexusport_schema_versions(version, description)
SELECT '20261120_063', 'vessel_id/company_id FK + bảng con container cho import_shipments, pre_alerts, delivery_orders'
WHERE NOT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '20261120_063');

COMMIT;
