BEGIN;
-- Revalidate final transaction state after schedule changes, cancellations and
-- assignment changes. Linked rows are available at deferred-trigger time.
CREATE OR REPLACE FUNCTION yard_validate_schedule() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE cid uuid; bad boolean;
BEGIN
 FOR cid IN SELECT container_id FROM booking_containers WHERE booking_id=NEW.id ORDER BY container_id LOOP
  PERFORM 1 FROM containers WHERE id=cid FOR UPDATE;
  IF EXISTS(SELECT 1 FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id
   JOIN booking_containers xc ON xc.container_id=bc.container_id AND xc.booking_id<>bc.booking_id
   JOIN bookings x ON x.id=xc.booking_id WHERE bc.container_id=cid
   AND b.status::text NOT IN ('canceled','rejected','expired','completed') AND x.status::text NOT IN ('canceled','rejected','expired','completed')
   AND tstzrange(b.appointment_start,b.appointment_end,'[)') && tstzrange(x.appointment_start,x.appointment_end,'[)')) THEN
   RAISE EXCEPTION 'Container có lịch hẹn chồng nhau.' USING ERRCODE='23514';
  END IF;
  WITH stays AS (
   SELECT v.id,v.container_id,v.started_at,v.visit_status,
    count(*) FILTER(WHERE b.booking_type::text='dropoff') AS ins,
    count(*) FILTER(WHERE b.booking_type::text='pickup') AS outs,
    COALESCE(min(b.appointment_start) FILTER(WHERE b.booking_type::text='dropoff'),v.started_at) AS enter_at,
    max(b.appointment_end) FILTER(WHERE b.booking_type::text='dropoff') AS enter_end,
    min(b.appointment_start) FILTER(WHERE b.booking_type::text='pickup') AS exit_start,
    COALESCE(max(b.appointment_end) FILTER(WHERE b.booking_type::text='pickup'),v.completed_at) AS exit_at
   FROM container_visits v LEFT JOIN booking_containers bc ON bc.container_visit_id=v.id
   LEFT JOIN bookings b ON b.id=bc.booking_id AND b.status::text NOT IN ('canceled','rejected','expired')
   WHERE v.container_id=cid AND v.visit_status<>'canceled' GROUP BY v.id
  ) SELECT EXISTS(SELECT 1 FROM stays WHERE ins>1 OR outs>1 OR (outs>0 AND enter_at IS NULL) OR exit_start<COALESCE(enter_end,started_at))
   OR EXISTS(SELECT 1 FROM stays a JOIN stays b ON a.id<b.id WHERE a.enter_at IS NOT NULL AND b.enter_at IS NOT NULL
    AND tstzrange(a.enter_at,COALESCE(a.exit_at,'infinity'),'[)') && tstzrange(b.enter_at,COALESCE(b.exit_at,'infinity'),'[)')) INTO bad;
  IF bad THEN RAISE EXCEPTION 'Thứ tự vào/ra hoặc khoảng lưu bãi không hợp lệ. Kiểm tra các booking liên kết.' USING ERRCODE='23514'; END IF;
 END LOOP;
 IF NEW.driver_id IS NOT NULL THEN PERFORM 1 FROM drivers WHERE id=NEW.driver_id FOR UPDATE; END IF;
 IF NEW.truck_id IS NOT NULL THEN PERFORM 1 FROM trucks WHERE id=NEW.truck_id FOR UPDATE; END IF;
 IF NEW.status::text NOT IN ('canceled','rejected','expired','completed') AND EXISTS(
  SELECT 1 FROM bookings x WHERE x.id<>NEW.id AND x.status::text NOT IN ('canceled','rejected','expired','completed')
  AND (x.driver_id=NEW.driver_id OR x.truck_id=NEW.truck_id)
  AND tstzrange(x.appointment_start,x.appointment_end,'[)') && tstzrange(NEW.appointment_start,NEW.appointment_end,'[)')) THEN
  RAISE EXCEPTION 'Xe hoặc tài xế có lịch hẹn chồng nhau.' USING ERRCODE='23514';
 END IF;
 RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS trg_validate_journey_schedule ON bookings;
CREATE CONSTRAINT TRIGGER trg_validate_journey_schedule AFTER INSERT OR UPDATE ON bookings
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION yard_validate_schedule();

CREATE OR REPLACE FUNCTION yard_protect_visit() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' AND (OLD.visit_status IN ('active','completed') OR EXISTS(SELECT 1 FROM booking_containers WHERE container_visit_id=OLD.id)) THEN
  RAISE EXCEPTION 'Không thể xóa lượt bãi có giao dịch.' USING ERRCODE='23514';
 END IF;
 IF TG_OP='DELETE' THEN RETURN OLD; END IF;
 IF OLD.visit_status='completed' AND (to_jsonb(OLD)-'updated_at') IS DISTINCT FROM (to_jsonb(NEW)-'updated_at') THEN
  RAISE EXCEPTION 'Lượt bãi đã kết thúc là lịch sử bất biến.' USING ERRCODE='23514';
 END IF;
 IF (OLD.container_id,OLD.declaration_id,OLD.customer_name,OLD.seal_number,OLD.cargo_type,OLD.load_status,OLD.gross_weight_kg,OLD.required_power_kw)
 IS DISTINCT FROM (NEW.container_id,NEW.declaration_id,NEW.customer_name,NEW.seal_number,NEW.cargo_type,NEW.load_status,NEW.gross_weight_kg,NEW.required_power_kw)
 AND (OLD.visit_status='active' OR EXISTS(SELECT 1 FROM booking_containers WHERE container_visit_id=OLD.id)) THEN
  RAISE EXCEPTION 'Không sửa thông tin gốc của lượt bãi đã gắn booking.' USING ERRCODE='23514';
 END IF;
 IF NEW.visit_status='active' THEN
  PERFORM 1 FROM containers WHERE id=NEW.container_id FOR UPDATE;
  IF EXISTS(SELECT 1 FROM container_visits WHERE container_id=NEW.container_id AND id<>NEW.id AND visit_status='active') THEN
   RAISE EXCEPTION 'Container đã có lượt đang lưu bãi.' USING ERRCODE='23514';
  END IF;
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_protect_visit ON container_visits;
CREATE TRIGGER trg_protect_visit BEFORE UPDATE OR DELETE ON container_visits FOR EACH ROW EXECUTE FUNCTION yard_protect_visit();

-- Receipts capture per-booking declarations and actual times, never master cargo.
CREATE OR REPLACE FUNCTION yard_snapshot_visit() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE link record; receipt uuid; direction text; happened timestamptz;
BEGIN
 IF OLD.visit_status=NEW.visit_status OR NEW.visit_status NOT IN ('active','completed') THEN RETURN NEW; END IF;
 direction:=CASE WHEN NEW.visit_status='active' THEN 'in' ELSE 'out' END;
 happened:=CASE WHEN direction='in' THEN NEW.started_at ELSE NEW.completed_at END;
 IF happened IS NULL THEN RAISE EXCEPTION 'Thiếu thời điểm giao nhận thực tế.' USING ERRCODE='23514'; END IF;
 SELECT b.id,b.carrier_id,COALESCE(b.vehicle_plate,t.plate_number) AS plate_number,b.truck_id,car.company_name,
  d.id AS declaration_id,d.customer_name,d.load_status,d.cargo_type,d.gross_weight_kg,d.required_power_kw,d.seal_number,
  ct.code,ct.description,ct.size INTO link
 FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id
 JOIN container_declarations d ON d.id=bc.declaration_id JOIN containers c ON c.id=bc.container_id
 JOIN container_types ct ON ct.id=c.container_type_id JOIN carriers car ON car.id=b.carrier_id LEFT JOIN trucks t ON t.id=b.truck_id
 WHERE bc.container_visit_id=NEW.id AND b.booking_type::text=CASE WHEN direction='in' THEN 'dropoff' ELSE 'pickup' END
  AND b.status::text NOT IN ('canceled','rejected','expired') ORDER BY b.appointment_start DESC LIMIT 1;
 IF NOT FOUND THEN RETURN NEW; END IF; -- Preserve unlinked legacy operations without invented provenance.
 INSERT INTO eir_transactions(reference_no,container_visit_id,booking_id,declaration_id,activity,eir_status,
  terminal_code,terminal_name,check_in_at,check_out_at,customer_name,load_status,iso_code,container_type_description,size_feet,
  cargo_type_description,gross_weight_kg,required_power_kw,transport_company_name,truck_id,plate_number,source_system)
 VALUES('EIR-'||gen_random_uuid(),NEW.id,link.id,link.declaration_id,direction,'draft','NEXUS_YARD','NexusPort Yard',happened,happened,
  link.customer_name,link.load_status,link.code,link.description,substring(link.size::text from 3)::smallint,
  (SELECT name_vi FROM container_cargo_types WHERE code=link.cargo_type),link.gross_weight_kg,link.required_power_kw,
  link.company_name,link.truck_id,link.plate_number,'nexusport_yard')
 ON CONFLICT DO NOTHING RETURNING id INTO receipt;
 IF receipt IS NOT NULL THEN
  IF link.seal_number IS NOT NULL THEN INSERT INTO eir_seals(eir_transaction_id,sequence_no,seal_number) VALUES(receipt,1,link.seal_number); END IF;
  UPDATE eir_transactions SET eir_status='issued' WHERE id=receipt;
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_snapshot_visit ON container_visits;
CREATE TRIGGER trg_snapshot_visit AFTER UPDATE ON container_visits FOR EACH ROW EXECUTE FUNCTION yard_snapshot_visit();

-- Driver confirmation completes the yard exchange, not the physical gate exit.
-- Dropoff retains an active stay; Pickup exits only after a passed GateOut.
CREATE OR REPLACE FUNCTION yard_booking_lifecycle() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v record;
BEGIN
 IF OLD.status=NEW.status THEN RETURN NEW; END IF;
 IF NEW.status::text IN ('checked_in','completed') AND NEW.booking_type::text='dropoff' THEN
  FOR v IN SELECT bc.container_visit_id,bc.container_id FROM booking_containers bc WHERE bc.booking_id=NEW.id LOOP
   IF v.container_visit_id IS NULL THEN RAISE EXCEPTION 'Booking cũ chưa xác định lượt bãi; cần đối soát trước khi vào.' USING ERRCODE='23514'; END IF;
   UPDATE container_visits SET visit_status='active',started_at=COALESCE(started_at,now()) WHERE id=v.container_visit_id AND visit_status='planned';
   UPDATE containers SET status=CASE WHEN NEW.status::text='completed' THEN 'in_yard'::container_status ELSE 'gate_in'::container_status END,
    arrived_at=COALESCE(arrived_at,now()),left_at=NULL,updated_at=now() WHERE id=v.container_id;
  END LOOP;
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_booking_journey_lifecycle ON bookings;
CREATE TRIGGER trg_booking_journey_lifecycle AFTER UPDATE ON bookings FOR EACH ROW EXECUTE FUNCTION yard_booking_lifecycle();

CREATE OR REPLACE FUNCTION yard_finish_pickup() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v record;
BEGIN
 IF NEW.gate_type::text='gate_out' AND NEW.status::text='completed'
 AND (TG_OP='INSERT' OR OLD.status IS DISTINCT FROM NEW.status) THEN
  FOR v IN SELECT bc.container_visit_id,bc.container_id FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id
   WHERE b.id=NEW.booking_id AND b.booking_type::text='pickup' AND b.status::text IN ('checked_in','completed') LOOP
   IF v.container_visit_id IS NULL THEN RAISE EXCEPTION 'Pickup chưa liên kết lượt bãi.' USING ERRCODE='23514'; END IF;
   UPDATE container_visits SET visit_status='completed',completed_at=COALESCE(NEW.completed_at,NEW.checked_at,now()) WHERE id=v.container_visit_id AND visit_status='active';
   IF NOT FOUND THEN RAISE EXCEPTION 'Không có lượt lưu bãi đang hoạt động để lấy ra.' USING ERRCODE='23514'; END IF;
   UPDATE containers SET status='gate_out',left_at=COALESCE(NEW.completed_at,NEW.checked_at,now()),updated_at=now() WHERE id=v.container_id;
  END LOOP;
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_finish_pickup_visit ON gate_transactions;
CREATE TRIGGER trg_finish_pickup_visit AFTER INSERT OR UPDATE ON gate_transactions FOR EACH ROW EXECUTE FUNCTION yard_finish_pickup();
CREATE OR REPLACE FUNCTION yard_validate_changed_link() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='DELETE' THEN
  IF EXISTS(SELECT 1 FROM bookings WHERE id=OLD.booking_id AND status::text IN ('checked_in','completed')) THEN
   RAISE EXCEPTION 'Không xóa liên kết booking đã thực hiện.' USING ERRCODE='23514';
  END IF;
  UPDATE bookings SET appointment_start=appointment_start WHERE id=OLD.booking_id;
  RETURN OLD;
 END IF;
 UPDATE bookings SET appointment_start=appointment_start WHERE id=NEW.booking_id;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_validate_changed_link ON booking_containers;
CREATE TRIGGER trg_validate_changed_link AFTER INSERT OR UPDATE OR DELETE ON booking_containers FOR EACH ROW EXECUTE FUNCTION yard_validate_changed_link();
INSERT INTO nexusport_schema_versions(version,description) VALUES('20261017_010','Journey schedule, lifecycle and receipt guards') ON CONFLICT DO NOTHING;
COMMIT;
