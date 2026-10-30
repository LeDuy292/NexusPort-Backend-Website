-- Preserve assigned fleet profiles without backfilling historical bookings.
BEGIN;
ALTER TABLE public.bookings ADD COLUMN IF NOT EXISTS vehicle_snapshot jsonb;
ALTER TABLE public.bookings ADD COLUMN IF NOT EXISTS driver_snapshot jsonb;
ALTER TABLE public.bookings ADD COLUMN IF NOT EXISTS trailer_number text;
COMMENT ON COLUMN public.bookings.vehicle_snapshot IS 'Server-captured vehicle profile at assignment time, excluding payload. NULL for legacy bookings without a captured profile.';
COMMENT ON COLUMN public.bookings.driver_snapshot IS 'Server-captured assigned driver profile; original document keys, never expiring signed URLs.';
INSERT INTO public.nexusport_schema_versions(version,description)
VALUES ('20261029_027','Booking vehicle and driver profile snapshots and trailer plate')
ON CONFLICT(version) DO NOTHING;
COMMIT;
