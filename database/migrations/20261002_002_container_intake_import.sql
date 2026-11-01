-- NXP-038 - Container intake/import foundation (no EIR processing)
-- Additive migration. It keeps the existing containers table compatible.

BEGIN;

CREATE TABLE IF NOT EXISTS nexusport_schema_versions (
  version varchar(80) PRIMARY KEY,
  description text NOT NULL,
  applied_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS container_visits (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  visit_reference varchar(100) NOT NULL UNIQUE,
  container_id uuid NOT NULL REFERENCES containers(id),
  booking_id uuid REFERENCES bookings(id),
  vessel_call_id uuid REFERENCES vessel_calls(id),
  visit_status varchar(30) NOT NULL DEFAULT 'planned'
    CHECK (visit_status IN ('planned', 'active', 'completed', 'canceled')),
  load_status varchar(20) NOT NULL DEFAULT 'unknown'
    CHECK (load_status IN ('full', 'empty', 'unknown')),
  cargo_type cargo_type,
  gross_weight_kg numeric(12,2),
  bl_booking_no varchar(150),
  customer_name varchar(250),
  transport_company_name varchar(250),
  valid_to timestamptz,
  started_at timestamptz,
  completed_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS container_import_batches (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  source_type varchar(30) NOT NULL
    CHECK (source_type IN ('port_vessel', 'transport_company')),
  file_name varchar(500) NOT NULL,
  file_size_bytes bigint NOT NULL DEFAULT 0,
  import_status varchar(20) NOT NULL DEFAULT 'processing'
    CHECK (import_status IN ('processing', 'completed', 'partial', 'failed')),
  total_rows integer NOT NULL DEFAULT 0,
  success_rows integer NOT NULL DEFAULT 0,
  duplicate_rows integer NOT NULL DEFAULT 0,
  failed_rows integer NOT NULL DEFAULT 0,
  created_by uuid REFERENCES users(id),
  created_at timestamptz NOT NULL DEFAULT now(),
  completed_at timestamptz
);

CREATE TABLE IF NOT EXISTS container_import_rows (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  import_batch_id uuid NOT NULL REFERENCES container_import_batches(id) ON DELETE CASCADE,
  row_number integer NOT NULL,
  normalized_container_no varchar(30),
  result_status varchar(30) NOT NULL
    CHECK (result_status IN ('created_master', 'created_visit', 'updated_visit', 'duplicate', 'rejected')),
  container_id uuid REFERENCES containers(id),
  container_visit_id uuid REFERENCES container_visits(id),
  raw_data jsonb NOT NULL DEFAULT '{}'::jsonb,
  errors jsonb NOT NULL DEFAULT '[]'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (import_batch_id, row_number)
);

ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS source_type varchar(30);
ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS movement_type varchar(30);
ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS data_status varchar(30) NOT NULL DEFAULT 'pending_verification';
ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS source_reference varchar(150);
ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS seal_number varchar(100);
ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS expected_arrival_at timestamptz;
ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS expected_available_at timestamptz;
ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS requested_pickup_date date;
ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS declared_by uuid REFERENCES users(id);
ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS import_batch_id uuid REFERENCES container_import_batches(id);
ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS source_row_number integer;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_container_visits_source_type') THEN
    ALTER TABLE container_visits ADD CONSTRAINT ck_container_visits_source_type
      CHECK (source_type IS NULL OR source_type IN ('port_vessel', 'transport_company'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_container_visits_movement_type') THEN
    ALTER TABLE container_visits ADD CONSTRAINT ck_container_visits_movement_type
      CHECK (movement_type IS NULL OR movement_type IN ('vessel_discharge', 'truck_dropoff', 'pickup_request'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_container_visits_data_status') THEN
    ALTER TABLE container_visits ADD CONSTRAINT ck_container_visits_data_status
      CHECK (data_status IN ('pending_verification', 'verified', 'rejected'));
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_container_import_batches_created_at
  ON container_import_batches(created_at DESC);
CREATE INDEX IF NOT EXISTS ix_container_import_rows_batch
  ON container_import_rows(import_batch_id, row_number);
CREATE INDEX IF NOT EXISTS ix_container_visits_intake_match
  ON container_visits(container_id, movement_type, expected_arrival_at, requested_pickup_date);
CREATE UNIQUE INDEX IF NOT EXISTS ux_container_visits_source_reference
  ON container_visits(container_id, source_type, movement_type, source_reference)
  WHERE source_reference IS NOT NULL;

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261002_002', 'NXP-038 container master, visit intake and Excel import history')
ON CONFLICT (version) DO NOTHING;

COMMIT;
