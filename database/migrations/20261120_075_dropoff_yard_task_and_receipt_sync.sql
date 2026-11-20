BEGIN;

-- Ảnh container do nhân viên bãi chụp khi xác nhận nhận container (mảng URL S3, không lưu ảnh trong DB).
ALTER TABLE yard_receipts ADD COLUMN IF NOT EXISTS photo_urls jsonb NOT NULL DEFAULT '[]'::jsonb;

-- Ô bãi (yard_slots, container 40ft chiếm 2 module) chứa vị trí do điều độ đặt trong persistent_yard_reservations.
-- Vị trí đặt dùng số bay theo module (1..n); yard_slots lưu bay = 2 x module_start của ô 40ft.
CREATE OR REPLACE FUNCTION yard_reservation_slot(p_container_no text)
RETURNS TABLE(slot_id uuid, block_code text, bay integer, row_no integer, tier integer, zone text)
LANGUAGE sql STABLE AS $$
  SELECT s.id, b.code::text, s.bay, s.row_no, s.tier, b.zone::text
  FROM persistent_yard_reservations r
  JOIN yard_blocks b ON upper(b.code) = upper(r.block_code)
  JOIN yard_slots s ON s.block_id = b.id AND s.row_no = r.row_no AND s.tier = r.tier
    AND r.bay >= s.module_start AND r.bay < s.module_start + COALESCE(s.module_span, 1)
  WHERE upper(r.container_no) = upper(p_container_no) AND r.status IN ('reserved', 'occupied')
  ORDER BY r.updated_at DESC LIMIT 1
$$;

-- Xe hạ bãi qua cổng vào: tự tạo nhiệm vụ yard (Unload) đến đúng ô đã đặt để nhân viên bãi điều phối cẩu.
CREATE OR REPLACE FUNCTION create_yard_task_on_gate_in() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE r record; dest record; task_id uuid;
BEGIN
  IF NEW.booking_id IS NULL OR NEW.gate_type IS DISTINCT FROM 'gate_in' OR NEW.status IS DISTINCT FROM 'completed' THEN RETURN NEW; END IF;
  FOR r IN
    SELECT bc.container_id, bc.container_visit_id, c.container_no, b.truck_id, b.driver_id, d.full_name AS driver_name,
           COALESCE(t.plate_number, b.vehicle_plate) AS plate
    FROM booking_containers bc
    JOIN containers c ON c.id = bc.container_id
    JOIN bookings b ON b.id = bc.booking_id
    LEFT JOIN drivers d ON d.id = b.driver_id
    LEFT JOIN trucks t ON t.id = b.truck_id
    WHERE bc.booking_id = NEW.booking_id AND yard_booking_direction(NEW.booking_id, bc.container_id) = 'dropoff'
  LOOP
    IF EXISTS (SELECT 1 FROM yard_tasks y WHERE y.booking_id = NEW.booking_id AND y."ContainerId" = r.container_id
               AND y."IsDeleted" = false AND lower(y."Status") NOT IN ('cancelled', 'canceled')) THEN CONTINUE; END IF;
    SELECT * INTO dest FROM yard_reservation_slot(r.container_no);
    IF dest.slot_id IS NULL THEN CONTINUE; END IF; -- chưa có vị trí đặt: chờ điều độ đặt rồi nhân viên bãi tạo tay
    task_id := gen_random_uuid();
    INSERT INTO yard_tasks("Id","TaskCode","OperationType","ContainerId","ContainerNo","VehicleId","VehiclePlate","DriverId","DriverName",
        "FromLocation","ToLocation","BlockCode","Priority","Status","DueTime","CreatedAt","CreatedBy","IsDeleted","booking_id","container_visit_id","Notes")
    VALUES(task_id, 'TSK-IN-' || upper(substr(md5(task_id::text), 1, 6)), 'Unload', r.container_id, r.container_no, r.truck_id, r.plate, r.driver_id, r.driver_name,
        'GATE-IN', dest.block_code || '-' || dest.bay || '-' || dest.row_no || '-' || dest.tier, dest.block_code, 'High', 'Assigned',
        now() + interval '30 minutes', now(), 'system:gate-in', false, NEW.booking_id, r.container_visit_id,
        'Tự tạo khi xe hạ bãi qua cổng vào. Điều phối cẩu hạ container vào ô đã đặt rồi xác nhận nhận container kèm ảnh.');
    INSERT INTO yard_slot_reservations(id, slot_id, container_id, reserved_by, reserved_from, reason)
    SELECT gen_random_uuid(), dest.slot_id, r.container_id, NULL, now(), 'NXP-059 operation:' || task_id::text
    WHERE NOT EXISTS (SELECT 1 FROM yard_slot_reservations x WHERE x.container_id = r.container_id AND x.slot_id = dest.slot_id AND x.reserved_to IS NULL);
  END LOOP;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_create_yard_task_on_gate_in ON gate_transactions;
CREATE TRIGGER trg_create_yard_task_on_gate_in AFTER INSERT OR UPDATE OF status ON gate_transactions
  FOR EACH ROW EXECUTE FUNCTION create_yard_task_on_gate_in();

-- Nhân viên bãi xác nhận đã nhận container (kèm ảnh): hoàn tất nhiệm vụ yard, tự xác nhận phía tài xế và chuyển chuyến sang "Container Delivered".
CREATE OR REPLACE FUNCTION sync_driver_on_yard_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE task record; bk record; cond text; r record;
BEGIN
  SELECT y.* INTO task FROM yard_tasks y
   WHERE upper(y."ContainerNo") = upper(NEW.container_no) AND y.booking_id IS NOT NULL AND y."IsDeleted" = false
     AND lower(y."Status") NOT IN ('cancelled', 'canceled')
   ORDER BY y."CreatedAt" DESC LIMIT 1;
  IF task."Id" IS NULL THEN RETURN NEW; END IF;

  UPDATE yard_tasks SET "Status" = 'Completed', "EndTime" = COALESCE("EndTime", now()),
         "CompletedLocation" = COALESCE("CompletedLocation", "ToLocation"), "ReceivedAt" = COALESCE("ReceivedAt", NEW.received_at),
         "ReceivedBy" = COALESCE("ReceivedBy", NEW.received_by), "Condition" = COALESCE("Condition", NEW.condition), "UpdatedAt" = now()
   WHERE "Id" = task."Id" AND lower("Status") <> 'completed';

  INSERT INTO yard_operation_events("Id", operation_id, container_id, driver_id, operation_status, event_type, delivery_status, occurred_at, published_at, created_at)
  SELECT gen_random_uuid(), task."Id", task."ContainerId", task."DriverId", 'Completed', 'YardOperationCompleted', 'Published', now(), now(), now()
  WHERE task."DriverId" IS NOT NULL AND task."ContainerId" IS NOT NULL
    AND NOT EXISTS (SELECT 1 FROM yard_operation_events e WHERE e.operation_id = task."Id" AND e.operation_status = 'Completed');

  cond := CASE WHEN NEW.condition ILIKE 'good' THEN 'OK' WHEN NEW.condition ILIKE 'damaged' THEN 'DAMAGED' ELSE 'ISSUE' END;
  INSERT INTO container_confirmation_audits(id, booking_id, container_id, driver_id, condition, notes, container_status_after, confirmed_at, created_at)
  SELECT gen_random_uuid(), task.booking_id, task."ContainerId", task."DriverId", cond,
         'Nhân viên bãi đã xác nhận nhận container' || COALESCE(' - ' || NEW.notes, ''), 'in_yard', now(), now()
  WHERE task."DriverId" IS NOT NULL AND task."ContainerId" IS NOT NULL
  ON CONFLICT (booking_id, container_id) DO NOTHING;

  UPDATE containers SET status = 'in_yard'::container_status, updated_at = now() WHERE id = task."ContainerId";

  UPDATE bookings b SET status = 'completed', updated_at = now() WHERE b.id = task.booking_id AND b.status = 'checked_in'
    AND NOT EXISTS (SELECT 1 FROM booking_containers bc WHERE bc.booking_id = b.id AND NOT EXISTS (
      SELECT 1 FROM container_confirmation_audits a WHERE a.booking_id = b.id AND a.container_id = bc.container_id));

  FOR r IN SELECT id, status, driver_id FROM transport_trips
            WHERE booking_id = task.booking_id AND status IN ('Gate In', 'In Transit', 'Arrived at Yard') LOOP
    UPDATE transport_trips SET status = 'Container Delivered', updated_at = now() WHERE id = r.id;
    INSERT INTO transport_trip_history(id, trip_id, from_status, to_status, changed_by, note, changed_at)
    VALUES (gen_random_uuid(), r.id, r.status, 'Container Delivered', r.driver_id, 'Nhân viên bãi đã xác nhận nhận container', now());
  END LOOP;

  INSERT INTO driver_booking_notifications(user_id, booking_id, event_key, title, message)
  SELECT d.user_id, task.booking_id, 'yard_received:' || NEW.id::text, 'Bãi đã nhận container',
         'Nhân viên bãi đã xác nhận nhận container ' || NEW.container_no || '. Làm theo hướng dẫn để ra cổng.'
  FROM drivers d WHERE d.id = task."DriverId" AND d.user_id IS NOT NULL
  ON CONFLICT DO NOTHING;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_sync_driver_on_yard_receipt ON yard_receipts;
CREATE TRIGGER trg_sync_driver_on_yard_receipt AFTER INSERT ON yard_receipts
  FOR EACH ROW EXECUTE FUNCTION sync_driver_on_yard_receipt();

INSERT INTO nexusport_schema_versions(version, description)
SELECT '20261120_075', 'Tự tạo nhiệm vụ yard khi xe hạ bãi qua cổng; bãi xác nhận nhận container (kèm ảnh) tự chuyển trạng thái tài xế'
WHERE NOT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '20261120_075');

COMMIT;
