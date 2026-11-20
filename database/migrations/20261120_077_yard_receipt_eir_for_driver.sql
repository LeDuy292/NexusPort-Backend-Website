BEGIN;

-- Khi nhân viên bãi xác nhận nhận container, chuyến vận chuyển của tài xế nhận số phiếu e-EIR, tình trạng vỏ, seal thực tế và ảnh.
ALTER TABLE transport_trips ADD COLUMN IF NOT EXISTS eir_no varchar(80);
ALTER TABLE transport_trips ADD COLUMN IF NOT EXISTS eir_photo_urls jsonb NOT NULL DEFAULT '[]'::jsonb;

CREATE OR REPLACE FUNCTION sync_driver_on_yard_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE task record; bk record; cond text; r record; eir record; visit uuid; cond_label text; seal_state text;
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

  -- e-EIR chiều vào của lượt bãi đã phát hành là bất biến, nên không ghi thêm vào phiếu: tình trạng vỏ, seal thực tế và ảnh nằm trong
-- biên bản nhận bãi (yard_receipts) và được gắn với số phiếu EIR để hiển thị cho tài xế.
  visit := COALESCE(task.container_visit_id,
    (SELECT bc.container_visit_id FROM booking_containers bc WHERE bc.booking_id = task.booking_id AND bc.container_id = task."ContainerId" LIMIT 1));
  SELECT e.id, e.reference_no INTO eir FROM eir_transactions e
   WHERE e.activity = 'in' AND e.eir_status = 'issued' AND e.container_visit_id = visit
   ORDER BY e.issued_at DESC LIMIT 1;
  cond_label := CASE cond WHEN 'OK' THEN 'Nguyên vẹn' WHEN 'DAMAGED' THEN 'Hư hỏng' ELSE 'Seal hỏng / có vấn đề' END;
  UPDATE transport_trips SET eir_no = eir.reference_no, seal_no = COALESCE(NULLIF(NEW.actual_seal_no, ''), seal_no),
         container_condition = cond_label, eir_photo_urls = NEW.photo_urls, updated_at = now()
   WHERE booking_id = task.booking_id AND eir.id IS NOT NULL;

  FOR r IN SELECT id, status, driver_id FROM transport_trips
            WHERE booking_id = task.booking_id AND status IN ('Gate In', 'In Transit', 'Arrived at Yard') LOOP
    UPDATE transport_trips SET status = 'Container Delivered', updated_at = now() WHERE id = r.id;
    INSERT INTO transport_trip_history(id, trip_id, from_status, to_status, changed_by, note, changed_at)
    VALUES (gen_random_uuid(), r.id, r.status, 'Container Delivered', r.driver_id, 'Nhân viên bãi đã xác nhận nhận container', now());
  END LOOP;

  INSERT INTO driver_booking_notifications(user_id, booking_id, event_key, title, message)
  SELECT d.user_id, task.booking_id, 'yard_received:' || NEW.id::text, 'Bãi đã nhận container',
         'Nhân viên bãi đã xác nhận nhận container ' || NEW.container_no || COALESCE('. Phiếu e-EIR ' || eir.reference_no || ' đã phát hành', '') || '. Làm theo hướng dẫn để ra cổng.'
  FROM drivers d WHERE d.id = task."DriverId" AND d.user_id IS NOT NULL
  ON CONFLICT DO NOTHING;
  RETURN NEW;
END $$;

INSERT INTO nexusport_schema_versions(version, description)
SELECT '20261120_077', 'Bãi nhận container: gửi số e-EIR, tình trạng vỏ, seal và ảnh cho tài xế'
WHERE NOT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '20261120_077');

COMMIT;
