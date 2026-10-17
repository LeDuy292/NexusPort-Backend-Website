BEGIN;
-- Preserve unknown cargo on migrated historical visits, without inventing a code.
ALTER TABLE container_declarations ALTER COLUMN cargo_type DROP NOT NULL;
DROP TRIGGER IF EXISTS trg_lock_declaration ON container_declarations;
UPDATE container_declarations d SET cargo_type=NULL FROM container_visits v
 WHERE d.id=v.id AND d.container_id=v.container_id AND v.cargo_type IS NULL AND d.cargo_type='general';
CREATE TRIGGER trg_lock_declaration BEFORE UPDATE OR DELETE ON container_declarations FOR EACH ROW EXECUTE FUNCTION yard_lock_declaration();
CREATE OR REPLACE FUNCTION yard_validate_schedule() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE cid uuid; bad boolean;
BEGIN
 -- A transaction can enqueue several updates. Validate its final committed state.
 SELECT * INTO NEW FROM bookings WHERE id=NEW.id;
 IF NOT FOUND THEN RETURN NULL; END IF;
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
INSERT INTO nexusport_schema_versions(version,description) VALUES('20261017_011','Preserve unknown legacy cargo and validate final schedule state') ON CONFLICT DO NOTHING;
COMMIT;
