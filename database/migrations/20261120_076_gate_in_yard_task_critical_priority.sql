BEGIN;

-- Xe đã qua cổng vào thì nhiệm vụ hạ bãi tự tạo có mức ưu tiên khẩn cấp (Critical) để nhân viên bãi lập kế hoạch lấy container ngay.
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
        'GATE-IN', dest.block_code || '-' || dest.bay || '-' || dest.row_no || '-' || dest.tier, dest.block_code, 'Critical', 'Assigned',
        now() + interval '30 minutes', now(), 'system:gate-in', false, NEW.booking_id, r.container_visit_id,
        'Tự tạo khi xe hạ bãi qua cổng vào. Điều phối cẩu hạ container vào ô đã đặt rồi xác nhận nhận container kèm ảnh.');
    INSERT INTO yard_slot_reservations(id, slot_id, container_id, reserved_by, reserved_from, reason)
    SELECT gen_random_uuid(), dest.slot_id, r.container_id, NULL, now(), 'NXP-059 operation:' || task_id::text
    WHERE NOT EXISTS (SELECT 1 FROM yard_slot_reservations x WHERE x.container_id = r.container_id AND x.slot_id = dest.slot_id AND x.reserved_to IS NULL);
  END LOOP;
  RETURN NEW;
END $$;

UPDATE yard_tasks SET "Priority" = 'Critical', "UpdatedAt" = now()
 WHERE "CreatedBy" = 'system:gate-in' AND "Priority" <> 'Critical' AND lower("Status") NOT IN ('completed', 'cancelled', 'canceled');

INSERT INTO nexusport_schema_versions(version, description)
SELECT '20261120_076', 'Nhiệm vụ hạ bãi tự tạo khi xe qua cổng vào có ưu tiên khẩn cấp'
WHERE NOT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '20261120_076');

COMMIT;
