BEGIN;

ALTER TABLE gate_entry_orders
  ADD COLUMN IF NOT EXISTS source text NOT NULL DEFAULT 'dispatch' CHECK (source IN ('dispatch','gate_scan'));
ALTER TABLE gate_barrier_commands ADD COLUMN IF NOT EXISTS hardware_requested_at timestamptz;

CREATE OR REPLACE FUNCTION sync_dispatch_gate_entry_order() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE b bookings%ROWTYPE; plate text;
BEGIN
  IF (NEW.status='called' AND NEW.action='called') OR NEW.action='gate_admitted' THEN
    SELECT * INTO b FROM bookings WHERE id=NEW.booking_id;
    SELECT COALESCE(t.plate_number,b.vehicle_plate) INTO plate FROM trucks t WHERE t.id=b.truck_id;
    plate:=upper(regexp_replace(COALESCE(plate,b.vehicle_plate,''),'[^a-zA-Z0-9]','','g'));
    IF plate='' THEN RAISE EXCEPTION 'Booking must have an assigned truck plate before admission'; END IF;
    UPDATE gate_entry_orders SET status=CASE WHEN valid_until<=now() THEN 'expired' ELSE 'revoked' END
      WHERE booking_id=NEW.booking_id AND status IN ('pending_ack','active');
    IF NEW.action='gate_admitted' THEN
      INSERT INTO gate_entry_orders(booking_id,driver_id,truck_id,vehicle_plate,status,dispatch_version,issued_at,issued_by,valid_from,valid_until,source)
      VALUES(NEW.booking_id,NEW.driver_id,b.truck_id,plate,'active',NEW.version,NEW.updated_at,NEW.updated_by,
        NEW.updated_at,NEW.updated_at+interval '15 minutes','gate_scan');
    ELSE
      INSERT INTO gate_entry_orders(booking_id,driver_id,truck_id,vehicle_plate,status,dispatch_version,issued_at,issued_by,valid_from,valid_until)
      VALUES(NEW.booking_id,NEW.driver_id,b.truck_id,plate,'pending_ack',NEW.version,NEW.called_at,NEW.updated_by,NEW.called_at,NEW.call_expires_at);
    END IF;
  ELSIF NEW.status='acknowledged' THEN
    UPDATE gate_entry_orders SET status='active',acknowledged_at=NEW.acknowledged_at,
      valid_from=NEW.acknowledged_at,valid_until=NEW.acknowledged_at+interval '15 minutes'
      WHERE booking_id=NEW.booking_id AND source='dispatch' AND status='pending_ack' AND valid_until>now();
  ELSE
    UPDATE gate_entry_orders SET status=CASE WHEN valid_until<=now() THEN 'expired' ELSE 'revoked' END
      WHERE booking_id=NEW.booking_id AND status IN ('pending_ack','active');
    IF NEW.action='entry_expired' THEN
      INSERT INTO driver_booking_notifications(user_id,booking_id,event_key,title,message)
      SELECT user_id,NEW.booking_id,'dispatch:'||NEW.version,'Lệnh vào cổng đã hết hạn',NEW.message
      FROM drivers WHERE id=NEW.driver_id AND user_id IS NOT NULL ON CONFLICT DO NOTHING;
    END IF;
  END IF;
  RETURN NEW;
END $$;

INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261120_070','Authorize on-time entry at Gate In using the shared yard capacity and gate entry orders')
ON CONFLICT(version) DO NOTHING;
COMMIT;
