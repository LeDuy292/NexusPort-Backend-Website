BEGIN;
-- Migration 015 is already deployed; invalidate an old call when the truck changes too.
CREATE OR REPLACE FUNCTION invalidate_booking_dispatch_vehicle() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.truck_id IS DISTINCT FROM OLD.truck_id OR NEW.vehicle_plate IS DISTINCT FROM OLD.vehicle_plate THEN
    UPDATE booking_dispatch_state SET status='withdrawn',action='withdrawn',
      called_at=NULL,call_expires_at=NULL,acknowledged_at=NULL,hold_until=NULL,
      message='Xe được phân công cho booking đã thay đổi. Kiểm tra lại xe và xác nhận đến khu chờ.',
      version=version+1,updated_at=now(),updated_by=NULL
    WHERE booking_id=NEW.id AND status<>'withdrawn';
  END IF;
  RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_invalidate_booking_dispatch_vehicle ON bookings;
CREATE TRIGGER trg_invalidate_booking_dispatch_vehicle AFTER UPDATE OF truck_id,vehicle_plate ON bookings
  FOR EACH ROW EXECUTE FUNCTION invalidate_booking_dispatch_vehicle();
INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261017_016','Invalidate pre-gate dispatch when assigned truck changes')
ON CONFLICT(version) DO NOTHING;
COMMIT;
