-- Shared identity, private declarations, linked yard stays and immutable receipts.
-- Legacy columns are retained for a rolling deployment; no master/history is deleted.
BEGIN;
CREATE TABLE IF NOT EXISTS container_declarations (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 container_id uuid NOT NULL REFERENCES containers(id),
 declared_by uuid REFERENCES users(id),
 carrier_id uuid REFERENCES carriers(id),
 load_status varchar(20) NOT NULL CHECK(load_status IN ('full','empty','unknown')),
 cargo_type cargo_type NOT NULL,
 gross_weight_kg numeric(12,2) CHECK(gross_weight_kg >= 0),
 required_power_kw numeric(12,2),
 customer_name varchar(250), seal_number varchar(100),
 import_batch_id uuid REFERENCES container_import_batches(id), source_row_number integer,
 created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
 CHECK(required_power_kw IS NULL OR (cargo_type='reefer' AND required_power_kw>0)),
 UNIQUE(id,container_id)
);
CREATE INDEX IF NOT EXISTS ix_declarations_creator_container ON container_declarations(declared_by,container_id,created_at DESC);
CREATE INDEX IF NOT EXISTS ix_declarations_carrier_container ON container_declarations(carrier_id,container_id,created_at DESC);
ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS declaration_id uuid REFERENCES container_declarations(id);
ALTER TABLE booking_containers ADD COLUMN IF NOT EXISTS declaration_id uuid REFERENCES container_declarations(id);
ALTER TABLE booking_containers ADD COLUMN IF NOT EXISTS container_visit_id uuid REFERENCES container_visits(id);
CREATE INDEX IF NOT EXISTS ix_booking_containers_visit ON booking_containers(container_visit_id);
CREATE INDEX IF NOT EXISTS ix_booking_containers_declaration ON booking_containers(declaration_id);
CREATE INDEX IF NOT EXISTS ix_visits_declaration ON container_visits(declaration_id);
CREATE TABLE IF NOT EXISTS container_visit_access (
 container_visit_id uuid NOT NULL REFERENCES container_visits(id),
 carrier_id uuid NOT NULL REFERENCES carriers(id),
 granted_by uuid REFERENCES users(id), granted_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(container_visit_id,carrier_id)
);
-- Preserve exact existing visit data. Membership is assigned only when unambiguous.
INSERT INTO container_declarations(id,container_id,declared_by,carrier_id,load_status,cargo_type,
 gross_weight_kg,required_power_kw,customer_name,seal_number,import_batch_id,source_row_number,created_at,updated_at)
SELECT v.id,v.container_id,v.declared_by,
 (SELECT min(cu.carrier_id::text)::uuid FROM carrier_users cu WHERE cu.user_id=v.declared_by HAVING count(*)=1),
 v.load_status,COALESCE(v.cargo_type,'general'),v.gross_weight_kg,v.required_power_kw,
 v.customer_name,v.seal_number,v.import_batch_id,v.source_row_number,v.created_at,v.updated_at
FROM container_visits v ON CONFLICT(id) DO NOTHING;
UPDATE container_visits SET declaration_id=id WHERE declaration_id IS NULL;
-- Backfill only explicit links; do not guess a visit from the latest master row.
UPDATE booking_containers bc SET container_visit_id=v.id,declaration_id=v.declaration_id
FROM container_visits v WHERE v.booking_id=bc.booking_id AND v.container_id=bc.container_id
 AND bc.container_visit_id IS NULL;
INSERT INTO container_visit_access(container_visit_id,carrier_id)
SELECT v.id,d.carrier_id FROM container_visits v JOIN container_declarations d ON d.id=v.declaration_id
WHERE d.carrier_id IS NOT NULL ON CONFLICT DO NOTHING;
-- Existing C# fields are managed through migrations, never request-time DDL.
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS vehicle_plate varchar(50);
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS vehicle_id uuid;
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS driver_name varchar(250);
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS valid_from timestamptz;
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS valid_to timestamptz;
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS gate_type varchar(30);
ALTER TABLE bookings ADD COLUMN IF NOT EXISTS description text;
ALTER TABLE eir_transactions ADD COLUMN IF NOT EXISTS booking_id uuid REFERENCES bookings(id);
ALTER TABLE eir_transactions ADD COLUMN IF NOT EXISTS declaration_id uuid REFERENCES container_declarations(id);
ALTER TABLE eir_transactions ADD COLUMN IF NOT EXISTS required_power_kw numeric(12,2);
CREATE UNIQUE INDEX IF NOT EXISTS ux_eir_booking_visit_activity ON eir_transactions(booking_id,container_visit_id,activity) WHERE booking_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_containers_number_pattern ON containers(container_no varchar_pattern_ops);
CREATE INDEX IF NOT EXISTS ix_eir_plate_normalized ON eir_transactions((regexp_replace(upper(plate_number),'[^A-Z0-9]','','g')));

CREATE OR REPLACE FUNCTION yard_lock_declaration() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM booking_containers WHERE declaration_id=OLD.id)
 OR EXISTS(SELECT 1 FROM container_visits WHERE declaration_id=OLD.id AND visit_status IN ('active','completed')) THEN
  RAISE EXCEPTION 'Bản khai đã gắn với booking/lịch sử; hãy tạo bản khai mới.' USING ERRCODE='23514';
 END IF;
 IF TG_OP='DELETE' THEN RETURN OLD; END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_lock_declaration ON container_declarations;
CREATE TRIGGER trg_lock_declaration BEFORE UPDATE OR DELETE ON container_declarations FOR EACH ROW EXECUTE FUNCTION yard_lock_declaration();

-- One canonical validator for both Node and C#. Physical row locks serialize
-- competing reservations, including requests from different application servers.
CREATE OR REPLACE FUNCTION yard_link_booking_container() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE b bookings%ROWTYPE; d container_declarations%ROWTYPE; v container_visits%ROWTYPE;
 inbound_end timestamptz; other_start timestamptz; other_end timestamptz;
 candidate_start timestamptz; candidate_end timestamptz; r record;
BEGIN
 IF TG_OP='UPDATE' AND NEW IS NOT DISTINCT FROM OLD THEN RETURN NEW; END IF;
 SELECT * INTO b FROM bookings WHERE id=NEW.booking_id;
 PERFORM 1 FROM containers WHERE id=NEW.container_id FOR UPDATE;
 IF b.status::text IN ('completed','checked_in') THEN
  RAISE EXCEPTION 'Không thể sửa container của booking đã thực hiện.' USING ERRCODE='23514';
 END IF;
 IF NEW.declaration_id IS NULL THEN
  RAISE EXCEPTION 'Cần chọn bản khai cho lần booking này.' USING ERRCODE='23514';
 END IF;
 SELECT * INTO d FROM container_declarations WHERE id=NEW.declaration_id AND container_id=NEW.container_id FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Bản khai không khớp container.' USING ERRCODE='23514'; END IF;
 IF d.carrier_id IS DISTINCT FROM b.carrier_id THEN
  RAISE EXCEPTION 'Bản khai không thuộc công ty đang đặt lịch.' USING ERRCODE='23514';
 END IF;
 IF NEW.container_visit_id IS NULL THEN
  IF b.booking_type::text<>'dropoff' THEN
   RAISE EXCEPTION 'Pickup phải chọn đúng lượt bãi được cấp quyền.' USING ERRCODE='23514';
  END IF;
  INSERT INTO container_visits(visit_reference,container_id,declaration_id,load_status,cargo_type,gross_weight_kg,
   required_power_kw,customer_name,seal_number,source_type,declared_by,data_status)
  VALUES('YARD-'||gen_random_uuid(),NEW.container_id,d.id,d.load_status,d.cargo_type,d.gross_weight_kg,
   d.required_power_kw,d.customer_name,d.seal_number,'transport_company',d.declared_by,NULL) RETURNING id INTO NEW.container_visit_id;
  INSERT INTO container_visit_access(container_visit_id,carrier_id) VALUES(NEW.container_visit_id,b.carrier_id) ON CONFLICT DO NOTHING;
 END IF;
 SELECT * INTO v FROM container_visits WHERE id=NEW.container_visit_id AND container_id=NEW.container_id FOR UPDATE;
 IF NOT FOUND OR v.visit_status IN ('completed','canceled') THEN
  RAISE EXCEPTION 'Lượt bãi không hợp lệ hoặc đã kết thúc.' USING ERRCODE='23514';
 END IF;
 IF NOT EXISTS(SELECT 1 FROM container_visit_access WHERE container_visit_id=v.id AND carrier_id=b.carrier_id) THEN
  RAISE EXCEPTION 'Công ty chưa được cấp quyền cho lượt bãi này.' USING ERRCODE='23514';
 END IF;
 IF b.booking_type::text='dropoff' AND (v.declaration_id<>d.id OR v.visit_status<>'planned') THEN
  RAISE EXCEPTION 'Dropoff phải dùng bản khai gốc của lượt bãi chưa vào.' USING ERRCODE='23514';
 END IF;
 IF EXISTS(SELECT 1 FROM booking_containers bc JOIN bookings x ON x.id=bc.booking_id
  WHERE bc.container_visit_id=v.id AND bc.booking_id<>b.id AND x.booking_type=b.booking_type
  AND x.status::text NOT IN ('canceled','rejected','expired')) THEN
  RAISE EXCEPTION 'Lượt bãi đã có booking cùng chiều.' USING ERRCODE='23514';
 END IF;
 SELECT max(x.appointment_end) INTO inbound_end FROM booking_containers bc JOIN bookings x ON x.id=bc.booking_id
 WHERE bc.container_visit_id=v.id AND x.booking_type::text='dropoff' AND x.status::text NOT IN ('canceled','rejected','expired');
 IF b.booking_type::text='pickup' AND (inbound_end IS NULL AND v.started_at IS NULL OR b.appointment_start<COALESCE(inbound_end,v.started_at)) THEN
  RAISE EXCEPTION 'Pickup phải sau lượt nhập bãi hợp lệ.' USING ERRCODE='23514';
 END IF;
 candidate_start:=CASE WHEN b.booking_type::text='dropoff' THEN b.appointment_start ELSE
  COALESCE((SELECT min(x.appointment_start) FROM booking_containers bc JOIN bookings x ON x.id=bc.booking_id
  WHERE bc.container_visit_id=v.id AND x.booking_type::text='dropoff' AND x.status::text NOT IN ('canceled','rejected','expired')),v.started_at) END;
 candidate_end:=CASE WHEN b.booking_type::text='pickup' THEN b.appointment_end ELSE
  (SELECT max(x.appointment_end) FROM booking_containers bc JOIN bookings x ON x.id=bc.booking_id
  WHERE bc.container_visit_id=v.id AND x.booking_type::text='pickup' AND x.status::text NOT IN ('canceled','rejected','expired')) END;
 FOR r IN SELECT ov.id,ov.started_at FROM container_visits ov WHERE ov.container_id=NEW.container_id AND ov.id<>v.id
  AND ov.visit_status IN ('planned','active') AND (ov.started_at IS NOT NULL OR EXISTS(SELECT 1 FROM booking_containers bc JOIN bookings x ON x.id=bc.booking_id
  WHERE bc.container_visit_id=ov.id AND x.status::text NOT IN ('canceled','rejected','expired'))) LOOP
  SELECT COALESCE(min(x.appointment_start) FILTER(WHERE x.booking_type::text='dropoff'),r.started_at),
   max(x.appointment_end) FILTER(WHERE x.booking_type::text='pickup') INTO other_start,other_end
  FROM booking_containers bc JOIN bookings x ON x.id=bc.booking_id WHERE bc.container_visit_id=r.id AND x.status::text NOT IN ('canceled','rejected','expired');
  IF tstzrange(candidate_start,COALESCE(candidate_end,'infinity'),'[)') && tstzrange(other_start,COALESCE(other_end,'infinity'),'[)') THEN
   RAISE EXCEPTION 'Lịch sử dụng container chồng với một lượt bãi khác; cần hoàn tất kế hoạch lấy ra trước.' USING ERRCODE='23514';
  END IF;
 END LOOP;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_link_booking_container ON booking_containers;
CREATE TRIGGER trg_link_booking_container BEFORE INSERT OR UPDATE ON booking_containers FOR EACH ROW EXECUTE FUNCTION yard_link_booking_container();

CREATE OR REPLACE FUNCTION yard_protect_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF OLD.eir_status<>'draft' THEN RAISE EXCEPTION 'EIR đã phát hành là lịch sử bất biến.' USING ERRCODE='23514'; END IF;
 IF TG_OP='DELETE' THEN RETURN OLD; END IF; RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_protect_receipt ON eir_transactions;
CREATE TRIGGER trg_protect_receipt BEFORE UPDATE OR DELETE ON eir_transactions FOR EACH ROW EXECUTE FUNCTION yard_protect_receipt();
CREATE OR REPLACE FUNCTION yard_protect_receipt_child() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE receipt_id uuid;
BEGIN
 receipt_id:=CASE WHEN TG_OP='DELETE' THEN OLD.eir_transaction_id ELSE NEW.eir_transaction_id END;
 IF EXISTS(SELECT 1 FROM eir_transactions WHERE id=receipt_id AND eir_status<>'draft')
 OR (TG_OP='UPDATE' AND EXISTS(SELECT 1 FROM eir_transactions WHERE id=OLD.eir_transaction_id AND eir_status<>'draft')) THEN
  RAISE EXCEPTION 'Không thể sửa dữ liệu EIR đã phát hành.' USING ERRCODE='23514';
 END IF;
 IF TG_OP='DELETE' THEN RETURN OLD; END IF; RETURN NEW;
END $$;
DO $$ DECLARE t text; BEGIN
 FOREACH t IN ARRAY ARRAY['eir_seals','eir_damage_observations','eir_media'] LOOP
  EXECUTE format('DROP TRIGGER IF EXISTS trg_protect_receipt_child ON %I',t);
  EXECUTE format('CREATE TRIGGER trg_protect_receipt_child BEFORE INSERT OR UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION yard_protect_receipt_child()',t);
 END LOOP;
END $$;
COMMENT ON TABLE containers IS 'Shared physical identity and actual operational state. Legacy cargo columns are retired; use declarations/visits/EIR.';
COMMENT ON TABLE container_declarations IS 'Private per-movement cargo input. Immutable once linked to a booking. Carrier is access scope, not container ownership.';
INSERT INTO nexusport_schema_versions(version,description) VALUES('20261017_009','Shared container identity, private declarations and linked yard journeys') ON CONFLICT DO NOTHING;
COMMIT;
