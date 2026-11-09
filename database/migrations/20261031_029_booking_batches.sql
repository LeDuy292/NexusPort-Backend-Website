-- A commercial booking batch groups independently controlled physical journeys.
-- Existing bookings, gate passes, declarations and visits are preserved.
BEGIN;
CREATE TABLE IF NOT EXISTS public.booking_batches (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 carrier_id uuid NOT NULL REFERENCES public.carriers(id),
 batch_code varchar(80) NOT NULL UNIQUE,
 request_id uuid NOT NULL,
 request_hash varchar(64) NOT NULL,
 booking_type public.booking_type NOT NULL,
 cargo_type varchar(50) NOT NULL,
 description varchar(1000),
 shared_documents jsonb NOT NULL DEFAULT '[]'::jsonb CHECK(jsonb_typeof(shared_documents)='array'),
 created_by uuid REFERENCES public.users(id),
 documents_confirmed_at timestamptz NOT NULL DEFAULT now(),
 created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(carrier_id,request_id)
);
CREATE TABLE IF NOT EXISTS public.booking_batch_trips (
 batch_id uuid NOT NULL REFERENCES public.booking_batches(id),
 booking_id uuid NOT NULL UNIQUE REFERENCES public.bookings(id),
 sequence_number integer NOT NULL CHECK(sequence_number BETWEEN 1 AND 50),
 PRIMARY KEY(batch_id,sequence_number)
);
CREATE INDEX IF NOT EXISTS ix_booking_batches_carrier_created ON public.booking_batches(carrier_id,created_at DESC,id);

CREATE OR REPLACE FUNCTION public.validate_booking_batch_trip() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE parent public.booking_batches%ROWTYPE; trip public.bookings%ROWTYPE;
BEGIN
 SELECT * INTO parent FROM public.booking_batches WHERE id=NEW.batch_id;
 SELECT * INTO trip FROM public.bookings WHERE id=NEW.booking_id;
 IF parent.id IS NULL OR trip.id IS NULL OR parent.carrier_id<>trip.carrier_id
   OR parent.booking_type<>trip.booking_type OR trip.is_combined THEN
  RAISE EXCEPTION 'Batch trip must belong to the same carrier and direction and contain one physical journey.' USING ERRCODE='23514';
 END IF;
 IF (SELECT count(*) FROM public.booking_containers WHERE booking_id=trip.id)<>1
   OR (SELECT count(*) FROM public.booking_operations WHERE booking_id=trip.id)<>1
   OR EXISTS(SELECT 1 FROM public.booking_operations WHERE booking_id=trip.id AND cargo_type<>parent.cargo_type) THEN
  RAISE EXCEPTION 'Batch trip needs one container of the shared cargo type.' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_validate_booking_batch_trip ON public.booking_batch_trips;
CREATE TRIGGER trg_validate_booking_batch_trip BEFORE INSERT OR UPDATE ON public.booking_batch_trips
 FOR EACH ROW EXECUTE FUNCTION public.validate_booking_batch_trip();
INSERT INTO public.nexusport_schema_versions(version,description)
 VALUES('20261031_029','Booking batches with shared documents and independent per-truck journeys') ON CONFLICT(version) DO NOTHING;
COMMIT;
