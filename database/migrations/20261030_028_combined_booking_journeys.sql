-- One trip, two distinct containers: Export/GateIn then Import/GateOut. Existing single bookings keep their original direction.

BEGIN;

CREATE OR REPLACE FUNCTION public.yard_booking_direction(p_booking uuid,p_container uuid)
RETURNS text LANGUAGE sql STABLE AS $$
 SELECT CASE WHEN b.is_combined THEN
   (SELECT min(CASE o.operation_type WHEN 'Export' THEN 'dropoff' WHEN 'Import' THEN 'pickup' END)
    FROM booking_operations o WHERE o.booking_id=b.id AND o.container_id=p_container HAVING count(*)=1)
 ELSE b.booking_type::text END FROM bookings b WHERE b.id=p_booking
$$;


CREATE OR REPLACE FUNCTION public.yard_booking_lifecycle()
 RETURNS trigger
 LANGUAGE plpgsql
AS $function$
DECLARE v record;
BEGIN
 IF OLD.status=NEW.status THEN RETURN NEW; END IF;
 IF NEW.status::text IN ('checked_in','completed') THEN
  FOR v IN SELECT bc.container_visit_id,bc.container_id FROM booking_containers bc WHERE bc.booking_id=NEW.id AND yard_booking_direction(NEW.id,bc.container_id)='dropoff' LOOP
   IF v.container_visit_id IS NULL THEN RAISE EXCEPTION 'Booking cũ chưa xác định lượt bãi; cần đối soát trước khi vào.' USING ERRCODE='23514'; END IF;
   UPDATE container_visits SET visit_status='active',started_at=COALESCE(started_at,now()) WHERE id=v.container_visit_id AND visit_status='planned';
   UPDATE containers SET status=CASE WHEN NEW.status::text='completed' THEN 'in_yard'::container_status ELSE 'gate_in'::container_status END,
    arrived_at=COALESCE(arrived_at,now()),left_at=NULL,updated_at=now() WHERE id=v.container_id;
  END LOOP;
 END IF;
 RETURN NEW;
END $function$
;

CREATE OR REPLACE FUNCTION public.yard_finish_pickup()
 RETURNS trigger
 LANGUAGE plpgsql
AS $function$
DECLARE v record;
BEGIN
 IF NEW.gate_type::text='gate_out' AND NEW.status::text='completed'
 AND (TG_OP='INSERT' OR OLD.status IS DISTINCT FROM NEW.status) THEN
  FOR v IN SELECT bc.container_visit_id,bc.container_id FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id
   WHERE b.id=NEW.booking_id AND yard_booking_direction(b.id,bc.container_id)='pickup' AND b.status::text IN ('checked_in','completed') LOOP
   IF v.container_visit_id IS NULL THEN RAISE EXCEPTION 'Pickup chưa liên kết lượt bãi.' USING ERRCODE='23514'; END IF;
   UPDATE container_visits SET visit_status='completed',completed_at=COALESCE(NEW.completed_at,NEW.checked_at,now()) WHERE id=v.container_visit_id AND visit_status='active';
   IF NOT FOUND THEN RAISE EXCEPTION 'Không có lượt lưu bãi đang hoạt động để lấy ra.' USING ERRCODE='23514'; END IF;
   UPDATE containers SET status='gate_out',left_at=COALESCE(NEW.completed_at,NEW.checked_at,now()),updated_at=now() WHERE id=v.container_id;
  END LOOP;
 END IF;
 RETURN NEW;
END $function$
;

CREATE OR REPLACE FUNCTION public.yard_link_booking_container()
 RETURNS trigger
 LANGUAGE plpgsql
AS $function$
DECLARE b bookings%ROWTYPE; d container_declarations%ROWTYPE; v container_visits%ROWTYPE;
 inbound_end timestamptz; other_start timestamptz; other_end timestamptz;
 candidate_start timestamptz; candidate_end timestamptz; r record; leg_direction text;
BEGIN
 IF TG_OP='UPDATE' AND NEW IS NOT DISTINCT FROM OLD THEN RETURN NEW; END IF;
 SELECT * INTO b FROM bookings WHERE id=NEW.booking_id;
 leg_direction:=yard_booking_direction(NEW.booking_id,NEW.container_id);
 IF leg_direction IS NULL THEN RAISE EXCEPTION 'Combined booking needs an exact Export or Import operation for each container.' USING ERRCODE='23514'; END IF;
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
  IF leg_direction<>'dropoff' THEN
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
 IF leg_direction='dropoff' AND (v.declaration_id<>d.id OR v.visit_status<>'planned') THEN
  RAISE EXCEPTION 'Dropoff phải dùng bản khai gốc của lượt bãi chưa vào.' USING ERRCODE='23514';
 END IF;
 IF EXISTS(SELECT 1 FROM booking_containers bc JOIN bookings x ON x.id=bc.booking_id
  WHERE bc.container_visit_id=v.id AND bc.booking_id<>b.id AND yard_booking_direction(x.id,bc.container_id)=leg_direction
  AND x.status::text NOT IN ('canceled','rejected','expired')) THEN
  RAISE EXCEPTION 'Lượt bãi đã có booking cùng chiều.' USING ERRCODE='23514';
 END IF;
 SELECT max(x.appointment_end) INTO inbound_end FROM booking_containers bc JOIN bookings x ON x.id=bc.booking_id
 WHERE bc.container_visit_id=v.id AND yard_booking_direction(x.id,bc.container_id)='dropoff' AND x.status::text NOT IN ('canceled','rejected','expired');
 IF leg_direction='pickup' AND (inbound_end IS NULL AND v.started_at IS NULL OR b.appointment_start<COALESCE(inbound_end,v.started_at)) THEN
  RAISE EXCEPTION 'Pickup phải sau lượt nhập bãi hợp lệ.' USING ERRCODE='23514';
 END IF;
 candidate_start:=CASE WHEN leg_direction='dropoff' THEN b.appointment_start ELSE
  COALESCE((SELECT min(x.appointment_start) FROM booking_containers bc JOIN bookings x ON x.id=bc.booking_id
  WHERE bc.container_visit_id=v.id AND yard_booking_direction(x.id,bc.container_id)='dropoff' AND x.status::text NOT IN ('canceled','rejected','expired')),v.started_at) END;
 candidate_end:=CASE WHEN leg_direction='pickup' THEN b.appointment_end ELSE
  (SELECT max(x.appointment_end) FROM booking_containers bc JOIN bookings x ON x.id=bc.booking_id
  WHERE bc.container_visit_id=v.id AND yard_booking_direction(x.id,bc.container_id)='pickup' AND x.status::text NOT IN ('canceled','rejected','expired')) END;
 FOR r IN SELECT ov.id,ov.started_at FROM container_visits ov WHERE ov.container_id=NEW.container_id AND ov.id<>v.id
  AND ov.visit_status IN ('planned','active') AND (ov.started_at IS NOT NULL OR EXISTS(SELECT 1 FROM booking_containers bc JOIN bookings x ON x.id=bc.booking_id
  WHERE bc.container_visit_id=ov.id AND x.status::text NOT IN ('canceled','rejected','expired'))) LOOP
  SELECT COALESCE(min(x.appointment_start) FILTER(WHERE yard_booking_direction(x.id,bc.container_id)='dropoff'),r.started_at),
   max(x.appointment_end) FILTER(WHERE yard_booking_direction(x.id,bc.container_id)='pickup') INTO other_start,other_end
  FROM booking_containers bc JOIN bookings x ON x.id=bc.booking_id WHERE bc.container_visit_id=r.id AND x.status::text NOT IN ('canceled','rejected','expired');
  IF tstzrange(candidate_start,COALESCE(candidate_end,'infinity'),'[)') && tstzrange(other_start,COALESCE(other_end,'infinity'),'[)') THEN
   RAISE EXCEPTION 'Lịch sử dụng container chồng với một lượt bãi khác; cần hoàn tất kế hoạch lấy ra trước.' USING ERRCODE='23514';
  END IF;
 END LOOP;
 RETURN NEW;
END $function$
;

CREATE OR REPLACE FUNCTION public.yard_prepare_pickup(p_container uuid, p_code text, p_carrier uuid)
 RETURNS TABLE("DeclarationId" uuid, "ContainerVisitId" uuid)
 LANGUAGE plpgsql
AS $function$
DECLARE v container_visits%ROWTYPE; new_declaration uuid;
BEGIN
 -- Same lock order as reservation validation; all writes roll back with booking.
 PERFORM 1 FROM containers WHERE id=p_container FOR UPDATE;
 SELECT * INTO v FROM container_visits WHERE container_id=p_container
  AND pickup_code=upper(trim(p_code)) AND visit_status IN ('planned','active') FOR UPDATE;
 IF NOT FOUND OR NOT EXISTS(SELECT 1 FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id
  WHERE bc.container_visit_id=v.id AND yard_booking_direction(b.id,bc.container_id)='dropoff'
   AND b.status::text NOT IN ('canceled','rejected','expired')) THEN
  RAISE EXCEPTION 'Mã lượt bãi không hợp lệ với container hoặc lượt đã kết thúc.' USING ERRCODE='23514';
 END IF;
 -- Preserve the inbound cargo snapshot; never ask the receiving company to
 -- guess/redeclare another company's cargo or overwrite the physical master.
 INSERT INTO container_declarations(container_id,carrier_id,load_status,cargo_type,
  gross_weight_kg,required_power_kw,customer_name,seal_number)
 SELECT p_container,p_carrier,d.load_status,d.cargo_type,d.gross_weight_kg,
  d.required_power_kw,d.customer_name,d.seal_number FROM container_declarations d WHERE d.id=v.declaration_id
 RETURNING id INTO new_declaration;
 IF new_declaration IS NULL THEN
  RAISE EXCEPTION 'Lượt bãi thiếu thông tin giao nhận gốc.' USING ERRCODE='23514';
 END IF;
 INSERT INTO container_visit_access(container_visit_id,carrier_id) VALUES(v.id,p_carrier) ON CONFLICT DO NOTHING;
 RETURN QUERY SELECT new_declaration,v.id;
END $function$
;

CREATE OR REPLACE FUNCTION public.yard_snapshot_visit()
 RETURNS trigger
 LANGUAGE plpgsql
AS $function$
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
 WHERE bc.container_visit_id=NEW.id AND yard_booking_direction(b.id,bc.container_id)=CASE WHEN direction='in' THEN 'dropoff' ELSE 'pickup' END
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
END $function$
;

CREATE OR REPLACE FUNCTION public.yard_validate_schedule()
 RETURNS trigger
 LANGUAGE plpgsql
AS $function$
DECLARE cid uuid; bad boolean;
BEGIN
 -- A transaction can enqueue several updates. Validate its final committed state.
 SELECT * INTO NEW FROM bookings WHERE id=NEW.id;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF NEW.is_combined AND NEW.status::text NOT IN ('canceled','rejected','expired') AND (
   (SELECT count(*) FROM booking_containers WHERE booking_id=NEW.id)<>2 OR
   (SELECT count(*) FROM booking_operations WHERE booking_id=NEW.id)<>2 OR
   (SELECT count(*) FROM booking_operations WHERE booking_id=NEW.id AND operation_type='Export' AND expected_gate_direction='GateIn' AND sequence_number=1)<>1 OR
   (SELECT count(*) FROM booking_operations WHERE booking_id=NEW.id AND operation_type='Import' AND expected_gate_direction='GateOut' AND sequence_number=2)<>1) THEN
   RAISE EXCEPTION 'Combined booking must contain exactly one Export/GateIn and one Import/GateOut with two distinct linked containers.' USING ERRCODE='23514';
 END IF;
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
    count(*) FILTER(WHERE yard_booking_direction(b.id,bc.container_id)='dropoff') AS ins,
    count(*) FILTER(WHERE yard_booking_direction(b.id,bc.container_id)='pickup') AS outs,
    COALESCE(min(b.appointment_start) FILTER(WHERE yard_booking_direction(b.id,bc.container_id)='dropoff'),v.started_at) AS enter_at,
    max(b.appointment_end) FILTER(WHERE yard_booking_direction(b.id,bc.container_id)='dropoff') AS enter_end,
    min(b.appointment_start) FILTER(WHERE yard_booking_direction(b.id,bc.container_id)='pickup') AS exit_start,
    COALESCE(max(b.appointment_end) FILTER(WHERE yard_booking_direction(b.id,bc.container_id)='pickup'),v.completed_at) AS exit_at
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
END $function$
;

INSERT INTO public.nexusport_schema_versions(version,description) VALUES('20261030_028','Combined Booking direction per operation and shared gate lifecycle') ON CONFLICT(version) DO NOTHING;

COMMIT;

