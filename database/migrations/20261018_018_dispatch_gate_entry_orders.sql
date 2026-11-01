BEGIN;
-- One-use entry permission; the operational booking remains the same booking.
CREATE TABLE IF NOT EXISTS gate_entry_orders (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  booking_id uuid NOT NULL REFERENCES bookings(id) ON DELETE CASCADE,
  driver_id uuid NOT NULL REFERENCES drivers(id),
  truck_id uuid REFERENCES trucks(id),
  vehicle_plate text NOT NULL,
  status text NOT NULL CHECK(status IN ('pending_ack','active','consumed','expired','revoked')),
  dispatch_version integer NOT NULL,
  issued_at timestamptz NOT NULL DEFAULT now(),
  issued_by uuid REFERENCES users(id),
  valid_from timestamptz NOT NULL,
  valid_until timestamptz NOT NULL,
  acknowledged_at timestamptz,
  consumed_at timestamptz,
  gate_transaction_id uuid REFERENCES gate_transactions(id),
  UNIQUE(booking_id,dispatch_version),
  CHECK(valid_until>valid_from)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_gate_entry_booking_open ON gate_entry_orders(booking_id)
  WHERE status IN ('pending_ack','active');
CREATE INDEX IF NOT EXISTS ix_gate_entry_plate ON gate_entry_orders(vehicle_plate,issued_at DESC);
CREATE INDEX IF NOT EXISTS ix_gate_entry_expiry ON gate_entry_orders(valid_until)
  WHERE status IN ('pending_ack','active');

-- Command acceptance is separate from physical passage. No hardware is implied.
CREATE TABLE IF NOT EXISTS gate_barrier_commands (
  id uuid PRIMARY KEY,
  gate_id uuid NOT NULL REFERENCES gates(id),
  gate_code text NOT NULL,
  action text NOT NULL CHECK(action IN ('open','close')),
  status text NOT NULL DEFAULT 'simulated' CHECK(status IN ('requested','simulated','acknowledged','failed')),
  source text NOT NULL CHECK(source IN ('automatic','manual')),
  verification_id uuid,
  entry_order_id uuid REFERENCES gate_entry_orders(id),
  requested_by uuid NOT NULL REFERENCES users(id),
  reason text,
  requested_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_gate_barrier_commands_gate ON gate_barrier_commands(gate_id,requested_at DESC);

CREATE OR REPLACE FUNCTION sync_dispatch_gate_entry_order() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE b bookings%ROWTYPE; plate text;
BEGIN
  IF NEW.status='called' AND NEW.action='called' THEN
    SELECT * INTO b FROM bookings WHERE id=NEW.booking_id;
    SELECT COALESCE(t.plate_number,b.vehicle_plate) INTO plate FROM trucks t WHERE t.id=b.truck_id;
    plate:=upper(regexp_replace(COALESCE(plate,b.vehicle_plate,''),'[^a-zA-Z0-9]','','g'));
    IF plate='' THEN RAISE EXCEPTION 'Booking must have an assigned truck plate before dispatch'; END IF;
    UPDATE gate_entry_orders SET status='revoked' WHERE booking_id=NEW.booking_id AND status IN ('pending_ack','active');
    INSERT INTO gate_entry_orders(booking_id,driver_id,truck_id,vehicle_plate,status,dispatch_version,issued_at,issued_by,valid_from,valid_until)
    VALUES(NEW.booking_id,NEW.driver_id,b.truck_id,plate,'pending_ack',NEW.version,NEW.called_at,NEW.updated_by,NEW.called_at,NEW.call_expires_at);
  ELSIF NEW.status='acknowledged' THEN
    UPDATE gate_entry_orders SET status='active',acknowledged_at=NEW.acknowledged_at,
      valid_from=NEW.acknowledged_at,valid_until=NEW.acknowledged_at+interval '15 minutes'
      WHERE booking_id=NEW.booking_id AND status='pending_ack' AND valid_until>now();
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
DROP TRIGGER IF EXISTS trg_dispatch_gate_entry_order ON booking_dispatch_state;
CREATE TRIGGER trg_dispatch_gate_entry_order AFTER INSERT OR UPDATE ON booking_dispatch_state
  FOR EACH ROW EXECUTE FUNCTION sync_dispatch_gate_entry_order();

INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261018_018','One-use dispatcher gate entry orders shared by Express and C# auto/manual gate verification')
ON CONFLICT(version) DO NOTHING;
COMMIT;
