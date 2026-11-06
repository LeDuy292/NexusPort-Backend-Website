BEGIN;

-- Login accounts and fleet driver profiles have different IDs. Link explicitly;
-- never infer an account from a name (names may be duplicated across carriers).
ALTER TABLE drivers ADD COLUMN IF NOT EXISTS user_id uuid REFERENCES users(id) ON DELETE SET NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_drivers_user_id ON drivers(user_id) WHERE user_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS driver_booking_notifications (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  booking_id uuid NOT NULL REFERENCES bookings(id) ON DELETE CASCADE,
  event_key text NOT NULL,
  title text NOT NULL,
  message text NOT NULL,
  is_read boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(user_id, booking_id, event_key)
);
CREATE INDEX IF NOT EXISTS ix_driver_booking_notifications_user
  ON driver_booking_notifications(user_id, created_at DESC);

-- The website's C# booking workflow and the app share these persisted events.
CREATE OR REPLACE FUNCTION driver_booking_changed() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE recipient uuid;
BEGIN
  SELECT user_id INTO recipient FROM drivers WHERE id=NEW.driver_id;
  IF recipient IS NULL THEN RETURN NEW; END IF;

  IF TG_OP='INSERT' OR NEW.driver_id IS DISTINCT FROM OLD.driver_id THEN
    INSERT INTO driver_booking_notifications(user_id,booking_id,event_key,title,message)
    VALUES(recipient,NEW.id,'assigned','Bạn được giao booking mới',
      'Booking ' || NEW.booking_code || ' đã được công ty vận tải phân công cho bạn.')
    ON CONFLICT DO NOTHING;
  END IF;
  IF NEW.approved_at IS NOT NULL AND NEW.status::text IN ('approved','ready') THEN
    INSERT INTO driver_booking_notifications(user_id,booking_id,event_key,title,message)
    VALUES(recipient,NEW.id,'approved','Booking đã được duyệt',
      'Booking ' || NEW.booking_code || ' đã được duyệt. Xem lịch hẹn và thông tin xe, hàng hóa trước khi khởi hành.')
    ON CONFLICT DO NOTHING;
  END IF;
  IF NEW.status::text IN ('rejected','canceled','expired') THEN
    INSERT INTO driver_booking_notifications(user_id,booking_id,event_key,title,message)
    VALUES(recipient,NEW.id,NEW.status::text,'Booking đã thay đổi trạng thái',
      'Booking ' || NEW.booking_code || ': ' || NEW.status::text || '. Vui lòng kiểm tra lại lịch.')
    ON CONFLICT DO NOTHING;
  END IF;
  IF TG_OP='UPDATE' AND (NEW.appointment_start IS DISTINCT FROM OLD.appointment_start OR NEW.appointment_end IS DISTINCT FROM OLD.appointment_end) THEN
    INSERT INTO driver_booking_notifications(user_id,booking_id,event_key,title,message)
    VALUES(recipient,NEW.id,'schedule:' || NEW.appointment_start::text || ':' || NEW.appointment_end::text,
      'Lịch hẹn booking đã thay đổi','Booking ' || NEW.booking_code || ' có khung giờ mới. Vui lòng xem lại lịch hẹn.')
    ON CONFLICT DO NOTHING;
  END IF;
  RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_driver_booking_changed ON bookings;
CREATE TRIGGER trg_driver_booking_changed AFTER INSERT OR UPDATE OF driver_id,status,approved_at,appointment_start,appointment_end
  ON bookings FOR EACH ROW EXECUTE FUNCTION driver_booking_changed();

INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261017_014','Explicit driver login mapping and persisted booking assignment/approval notifications')
ON CONFLICT(version) DO NOTHING;
COMMIT;
