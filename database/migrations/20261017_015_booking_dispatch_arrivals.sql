BEGIN;
-- Pre-gate coordination only; never changes physical gate state.
CREATE TABLE IF NOT EXISTS booking_dispatch_state (
  booking_id uuid PRIMARY KEY REFERENCES bookings(id) ON DELETE CASCADE,
  driver_id uuid NOT NULL REFERENCES drivers(id),
  status text NOT NULL CHECK(status IN ('arrived','ready','called','acknowledged','withdrawn')),
  action text NOT NULL,
  arrived_at timestamptz NOT NULL DEFAULT now(),
  ready_at timestamptz,
  called_at timestamptz,
  call_expires_at timestamptz,
  acknowledged_at timestamptz,
  hold_until timestamptz,
  message text,
  version integer NOT NULL DEFAULT 1 CHECK(version > 0),
  updated_by uuid REFERENCES users(id) ON DELETE SET NULL,
  updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_booking_dispatch_queue ON booking_dispatch_state(status,ready_at);
CREATE TABLE IF NOT EXISTS booking_dispatch_outbox (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  booking_id uuid NOT NULL REFERENCES bookings(id) ON DELETE CASCADE,
  driver_id uuid NOT NULL REFERENCES drivers(id),
  version integer NOT NULL,
  kind text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  published_at timestamptz,
  UNIQUE(booking_id,version)
);
CREATE INDEX IF NOT EXISTS ix_booking_dispatch_outbox_pending
  ON booking_dispatch_outbox(created_at) WHERE published_at IS NULL;

-- State, persisted notification and socket event commit atomically.
CREATE OR REPLACE FUNCTION booking_dispatch_changed() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE recipient uuid; heading text; code text;
BEGIN
  INSERT INTO booking_dispatch_outbox(booking_id,driver_id,version,kind)
  VALUES(NEW.booking_id,NEW.driver_id,NEW.version,NEW.action) ON CONFLICT DO NOTHING;
  SELECT user_id INTO recipient FROM drivers WHERE id=NEW.driver_id;
  SELECT booking_code INTO code FROM bookings WHERE id=NEW.booking_id;
  heading := CASE NEW.action
    WHEN 'called' THEN 'Điều độ đã gọi bạn'
    WHEN 'delayed' THEN 'Điều độ thông báo chờ thêm'
    WHEN 'released' THEN 'Điều độ đã thu hồi lệnh gọi'
    WHEN 'call_expired' THEN 'Lệnh gọi đã hết thời gian phản hồi'
    WHEN 'withdrawn' THEN 'Điều phối booking đã thay đổi'
    ELSE NULL END;
  IF recipient IS NOT NULL AND heading IS NOT NULL THEN
    INSERT INTO driver_booking_notifications(user_id,booking_id,event_key,title,message)
    VALUES(recipient,NEW.booking_id,'dispatch:'||NEW.version,heading,
      'Booking '||code||'. '||COALESCE(NEW.message,heading))
    ON CONFLICT DO NOTHING;
  END IF;
  PERFORM pg_notify('booking_dispatch_changed',NEW.booking_id::text);
  RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_booking_dispatch_changed ON booking_dispatch_state;
CREATE TRIGGER trg_booking_dispatch_changed AFTER INSERT OR UPDATE ON booking_dispatch_state
  FOR EACH ROW EXECUTE FUNCTION booking_dispatch_changed();

-- C# rescheduling/reassignment/cancellation invalidates old calls immediately.
CREATE OR REPLACE FUNCTION invalidate_booking_dispatch() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.driver_id IS DISTINCT FROM OLD.driver_id
     OR NEW.appointment_start IS DISTINCT FROM OLD.appointment_start
     OR NEW.appointment_end IS DISTINCT FROM OLD.appointment_end
     OR NEW.status::text NOT IN ('approved','ready') THEN
    UPDATE booking_dispatch_state SET status='withdrawn',action='withdrawn',
      called_at=NULL,call_expires_at=NULL,acknowledged_at=NULL,hold_until=NULL,
      message='Booking đổi lịch, đổi tài xế hoặc không còn chờ điều phối. Vui lòng kiểm tra lại.',
      version=version+1,updated_at=now(),updated_by=NULL
    WHERE booking_id=NEW.id AND status<>'withdrawn';
  END IF;
  RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_invalidate_booking_dispatch ON bookings;
CREATE TRIGGER trg_invalidate_booking_dispatch
  AFTER UPDATE OF driver_id,status,appointment_start,appointment_end ON bookings
  FOR EACH ROW EXECUTE FUNCTION invalidate_booking_dispatch();
INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261017_015','Driver arrival/readiness, dispatcher calls and transactional realtime outbox')
ON CONFLICT(version) DO NOTHING;
COMMIT;
