BEGIN;

-- Khi cổng ghi nhận xe đã qua cổng vào / cổng ra (gate_transactions hoàn tất), chuyến vận chuyển của tài xế
-- (transport_trips) tự chuyển sang "Gate In" / "Gate Out Completed" và ghi lịch sử, không phụ thuộc backend nào ghi lượt qua cổng.
CREATE OR REPLACE FUNCTION sync_trip_on_gate_passage() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE r record; target text; note text;
BEGIN
  IF NEW.booking_id IS NULL OR NEW.status IS DISTINCT FROM 'completed' THEN RETURN NEW; END IF;
  IF NEW.gate_type = 'gate_in' THEN
    target := 'Gate In'; note := 'Cổng ghi nhận xe đã qua cổng vào';
  ELSIF NEW.gate_type = 'gate_out' THEN
    target := 'Gate Out Completed'; note := 'Cổng ghi nhận xe đã qua cổng ra';
  ELSE
    RETURN NEW;
  END IF;
  FOR r IN
    SELECT id, status, driver_id FROM transport_trips
     WHERE booking_id = NEW.booking_id
       AND ((target = 'Gate In' AND status IN ('Assigned', 'Acknowledged', 'Vehicle Picked Up', 'Container Picked Up'))
         OR (target = 'Gate Out Completed' AND status IN ('Assigned', 'Acknowledged', 'Vehicle Picked Up', 'Container Picked Up', 'Gate In', 'In Transit', 'Arrived at Yard', 'Container Delivered')))
  LOOP
    UPDATE transport_trips
       SET status = target,
           gate_in_at  = CASE WHEN target = 'Gate In' THEN COALESCE(gate_in_at, NEW.completed_at, now()) ELSE gate_in_at END,
           gate_out_at = CASE WHEN target = 'Gate Out Completed' THEN COALESCE(gate_out_at, NEW.completed_at, now()) ELSE gate_out_at END,
           updated_at = now()
     WHERE id = r.id;
    INSERT INTO transport_trip_history (id, trip_id, from_status, to_status, changed_by, note, changed_at)
    VALUES (gen_random_uuid(), r.id, r.status, target, r.driver_id, note, now());
  END LOOP;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_sync_trip_on_gate_passage ON gate_transactions;
CREATE TRIGGER trg_sync_trip_on_gate_passage AFTER INSERT OR UPDATE OF status ON gate_transactions
  FOR EACH ROW EXECUTE FUNCTION sync_trip_on_gate_passage();

INSERT INTO nexusport_schema_versions(version, description)
SELECT '20261120_074', 'Chuyến vận chuyển tự cập nhật theo lượt qua cổng (Gate In / Gate Out)'
WHERE NOT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '20261120_074');

COMMIT;
