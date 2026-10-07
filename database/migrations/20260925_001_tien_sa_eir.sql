-- NexusPort - Tiên Sa EIR data model
-- PostgreSQL migration for the schema in nexusport.sql (2026-09-25).
-- This migration is additive: it does not drop or rewrite legacy columns.

BEGIN;

CREATE TABLE IF NOT EXISTS nexusport_schema_versions (
  version varchar(80) PRIMARY KEY,
  description text NOT NULL,
  applied_at timestamptz NOT NULL DEFAULT now()
);

-- -----------------------------------------------------------------------------
-- DEVELOP 26a2948 COMPATIBILITY
-- The current database predates fields already mapped by the latest develop
-- branch. Keep these structural changes in migration history instead of letting
-- application services execute DDL at runtime.
-- -----------------------------------------------------------------------------
ALTER TYPE booking_status ADD VALUE IF NOT EXISTS 'ready' AFTER 'pending';

ALTER TYPE driver_status ADD VALUE IF NOT EXISTS 'waiting_confirmation';
ALTER TYPE driver_status ADD VALUE IF NOT EXISTS 'receiving_vehicle';
ALTER TYPE driver_status ADD VALUE IF NOT EXISTS 'vehicle_received';
ALTER TYPE driver_status ADD VALUE IF NOT EXISTS 'booking_confirmed';
ALTER TYPE driver_status ADD VALUE IF NOT EXISTS 'transporting';
ALTER TYPE driver_status ADD VALUE IF NOT EXISTS 'transport_completed';

ALTER TABLE drivers ADD COLUMN IF NOT EXISTS photo_url varchar(500);
ALTER TABLE drivers ADD COLUMN IF NOT EXISTS id_card_front_url varchar(500);
ALTER TABLE drivers ADD COLUMN IF NOT EXISTS id_card_back_url varchar(500);
ALTER TABLE drivers ADD COLUMN IF NOT EXISTS license_image_url varchar(500);
ALTER TABLE drivers ADD COLUMN IF NOT EXISTS license_back_image_url varchar(500);
ALTER TABLE drivers ADD COLUMN IF NOT EXISTS id_card_expiry_date timestamp without time zone;
ALTER TABLE drivers ADD COLUMN IF NOT EXISTS license_expiry_date timestamp without time zone;

ALTER TABLE trucks ADD COLUMN IF NOT EXISTS driver_id uuid;
ALTER TABLE trucks ADD COLUMN IF NOT EXISTS rfid_tag varchar(100);
ALTER TABLE trucks ADD COLUMN IF NOT EXISTS description text;
ALTER TABLE trucks ADD COLUMN IF NOT EXISTS registration_image_url text;
ALTER TABLE trucks ADD COLUMN IF NOT EXISTS photo_url text;
ALTER TABLE trucks ADD COLUMN IF NOT EXISTS current_location text;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'fk_trucks_driver_id'
      AND conrelid = 'trucks'::regclass
  ) THEN
    ALTER TABLE trucks ADD CONSTRAINT fk_trucks_driver_id
      FOREIGN KEY (driver_id) REFERENCES drivers(id) ON DELETE SET NULL;
  END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_trucks_rfid_tag
  ON trucks(rfid_tag) WHERE rfid_tag IS NOT NULL;

-- A visit is one operational stay/journey of a physical container at the terminal.
-- Fields such as seal, cargo weight and vessel call belong to a visit/EIR, not to
-- the permanent container identity.
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
  gross_weight_kg numeric(12,2) CHECK (gross_weight_kg IS NULL OR gross_weight_kg >= 0),
  bl_booking_no varchar(150),
  customer_name varchar(250),
  transport_company_name varchar(250),
  valid_to timestamptz,
  started_at timestamptz,
  completed_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  CHECK (completed_at IS NULL OR started_at IS NULL OR completed_at >= started_at)
);

CREATE INDEX IF NOT EXISTS ix_container_visits_container_time
  ON container_visits(container_id, started_at DESC);
CREATE INDEX IF NOT EXISTS ix_container_visits_booking ON container_visits(booking_id);
CREATE INDEX IF NOT EXISTS ix_container_visits_vessel_call ON container_visits(vessel_call_id);
CREATE INDEX IF NOT EXISTS ix_container_visits_status ON container_visits(visit_status);

-- Immutable operational snapshot of an Equipment Interchange Receipt.
-- Snapshot text is intentional: the historical receipt must remain readable even
-- when a carrier, truck, vessel or master-data name changes later.
CREATE TABLE IF NOT EXISTS eir_transactions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  reference_no varchar(100) NOT NULL UNIQUE,
  container_visit_id uuid NOT NULL REFERENCES container_visits(id),
  gate_transaction_id uuid REFERENCES gate_transactions(id),
  activity varchar(10) NOT NULL CHECK (activity IN ('in', 'out')),
  eir_status varchar(20) NOT NULL DEFAULT 'issued'
    CHECK (eir_status IN ('draft', 'issued', 'voided')),

  terminal_code varchar(50) NOT NULL DEFAULT 'TIEN_SA',
  terminal_name varchar(200) NOT NULL DEFAULT 'TIEN SA CONTAINER TERMINAL',
  operator_code varchar(80),
  operator_name varchar(250),
  customer_name varchar(250),
  location_code varchar(100),
  yard_slot_id uuid REFERENCES yard_slots(id),
  gate_label varchar(150),
  lane_code varchar(50),
  valid_to timestamptz,
  check_in_at timestamptz NOT NULL,
  check_out_at timestamptz,
  bl_booking_no varchar(150),

  load_status varchar(20) NOT NULL DEFAULT 'unknown'
    CHECK (load_status IN ('full', 'empty', 'unknown')),
  iso_code varchar(10),
  container_type_description varchar(100),
  size_feet smallint CHECK (size_feet IS NULL OR size_feet IN (20, 40, 45, 48, 53)),
  cargo_type_description varchar(100),
  gross_weight_kg numeric(12,2) CHECK (gross_weight_kg IS NULL OR gross_weight_kg >= 0),
  temperature_c numeric(6,2),
  ventilation_description varchar(150),
  imdg_class varchar(30),
  sound_damage_code varchar(30),
  remark text,

  vessel_name varchar(150),
  voyage_in varchar(80),
  voyage_out varchar(80),
  vessel_datetime_1 timestamptz,
  vessel_datetime_2 timestamptz,

  transport_company_name varchar(250),
  truck_id uuid REFERENCES trucks(id),
  plate_number varchar(30),
  empty_return_place varchar(250),
  terminal_signatory varchar(150),
  transportation_signatory varchar(150),

  source_system varchar(100) NOT NULL DEFAULT 'tien_sa_eir',
  source_file_name varchar(500),
  raw_payload jsonb NOT NULL DEFAULT '{}'::jsonb,
  issued_at timestamptz NOT NULL DEFAULT now(),
  created_by uuid REFERENCES users(id),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),

  CHECK (check_out_at IS NULL OR check_out_at >= check_in_at)
);

CREATE INDEX IF NOT EXISTS ix_eir_transactions_visit ON eir_transactions(container_visit_id);
CREATE INDEX IF NOT EXISTS ix_eir_transactions_gate_transaction ON eir_transactions(gate_transaction_id);
CREATE INDEX IF NOT EXISTS ix_eir_transactions_check_in ON eir_transactions(check_in_at DESC);
CREATE INDEX IF NOT EXISTS ix_eir_transactions_plate ON eir_transactions(plate_number);
CREATE INDEX IF NOT EXISTS ix_eir_transactions_bl_booking ON eir_transactions(bl_booking_no);
CREATE INDEX IF NOT EXISTS ix_eir_transactions_location ON eir_transactions(location_code);

-- A receipt may have no seal, one seal or several seals. raw_value preserves the
-- exact port notation (for example "UL6824219/ko") until its meaning is confirmed.
CREATE TABLE IF NOT EXISTS eir_seals (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  eir_transaction_id uuid NOT NULL REFERENCES eir_transactions(id) ON DELETE CASCADE,
  sequence_no smallint NOT NULL CHECK (sequence_no > 0),
  seal_number varchar(100),
  raw_value varchar(150),
  seal_condition varchar(30) NOT NULL DEFAULT 'unknown'
    CHECK (seal_condition IN ('intact', 'broken', 'missing', 'mismatch', 'unknown')),
  notes text,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (eir_transaction_id, sequence_no)
);

CREATE INDEX IF NOT EXISTS ix_eir_seals_number ON eir_seals(seal_number);

-- Damage/condition is an inspection observation at a particular interchange,
-- not a permanent state of the container.
CREATE TABLE IF NOT EXISTS eir_damage_observations (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  eir_transaction_id uuid NOT NULL REFERENCES eir_transactions(id) ON DELETE CASCADE,
  condition_code varchar(30),
  component_code varchar(50),
  damage_type varchar(80),
  description text NOT NULL,
  length_cm numeric(10,2) CHECK (length_cm IS NULL OR length_cm >= 0),
  width_cm numeric(10,2) CHECK (width_cm IS NULL OR width_cm >= 0),
  height_cm numeric(10,2) CHECK (height_cm IS NULL OR height_cm >= 0),
  severity severity_level,
  observed_at timestamptz NOT NULL DEFAULT now(),
  observed_by uuid REFERENCES users(id),
  raw_payload jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX IF NOT EXISTS ix_eir_damage_eir ON eir_damage_observations(eir_transaction_id);

-- Flexible evidence model for current images and future camera/video integration.
-- One row describes one stored object; binaries remain in object storage.
CREATE TABLE IF NOT EXISTS eir_media (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  eir_transaction_id uuid NOT NULL REFERENCES eir_transactions(id) ON DELETE CASCADE,
  container_visit_id uuid REFERENCES container_visits(id),
  gate_transaction_id uuid REFERENCES gate_transactions(id),
  container_id uuid REFERENCES containers(id),
  truck_id uuid REFERENCES trucks(id),

  subject_type varchar(30) NOT NULL
    CHECK (subject_type IN ('vehicle', 'container', 'seal', 'damage', 'document', 'overview', 'driver', 'other')),
  media_kind varchar(20) NOT NULL
    CHECK (media_kind IN ('image', 'video', 'document')),
  capture_stage varchar(30) NOT NULL
    CHECK (capture_stage IN ('approach', 'check_in', 'inspection', 'yard', 'check_out', 'departure', 'manual', 'import')),
  movement_direction varchar(10)
    CHECK (movement_direction IS NULL OR movement_direction IN ('in', 'out')),
  view_label varchar(50),
  sequence_no integer NOT NULL DEFAULT 1 CHECK (sequence_no > 0),

  media_url text,
  storage_provider varchar(50),
  bucket_name varchar(150),
  storage_key text,
  mime_type varchar(150),
  file_size_bytes bigint CHECK (file_size_bytes IS NULL OR file_size_bytes >= 0),
  sha256 char(64),
  camera_id varchar(100),
  captured_at timestamptz NOT NULL,
  captured_by uuid REFERENCES users(id),

  detected_value text,
  recognition_confidence numeric(7,6)
    CHECK (recognition_confidence IS NULL OR recognition_confidence BETWEEN 0 AND 1),
  ai_result jsonb NOT NULL DEFAULT '{}'::jsonb,
  metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_primary boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now(),
  CHECK (media_url IS NOT NULL OR storage_key IS NOT NULL)
);

CREATE INDEX IF NOT EXISTS ix_eir_media_eir_stage
  ON eir_media(eir_transaction_id, capture_stage, subject_type, captured_at);
CREATE INDEX IF NOT EXISTS ix_eir_media_container_time
  ON eir_media(container_id, captured_at DESC);
CREATE INDEX IF NOT EXISTS ix_eir_media_truck_time
  ON eir_media(truck_id, captured_at DESC);
CREATE INDEX IF NOT EXISTS ix_eir_media_gate_transaction ON eir_media(gate_transaction_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_eir_media_primary_subject
  ON eir_media(eir_transaction_id, subject_type, capture_stage)
  WHERE is_primary;

-- Bridge the existing camera/OCR tables to the new historical EIR model.
ALTER TABLE gate_media ADD COLUMN IF NOT EXISTS eir_transaction_id uuid;
ALTER TABLE gate_media ADD COLUMN IF NOT EXISTS subject_type varchar(30);
ALTER TABLE gate_media ADD COLUMN IF NOT EXISTS capture_stage varchar(30);
ALTER TABLE gate_media ADD COLUMN IF NOT EXISTS movement_direction varchar(10);
ALTER TABLE gate_media ADD COLUMN IF NOT EXISTS view_label varchar(50);
ALTER TABLE gate_media ADD COLUMN IF NOT EXISTS camera_id varchar(100);
ALTER TABLE gate_media ADD COLUMN IF NOT EXISTS storage_provider varchar(50);
ALTER TABLE gate_media ADD COLUMN IF NOT EXISTS bucket_name varchar(150);
ALTER TABLE gate_media ADD COLUMN IF NOT EXISTS storage_key text;
ALTER TABLE gate_media ADD COLUMN IF NOT EXISTS mime_type varchar(150);
ALTER TABLE gate_media ADD COLUMN IF NOT EXISTS file_size_bytes bigint;
ALTER TABLE gate_media ADD COLUMN IF NOT EXISTS sha256 char(64);
ALTER TABLE gate_media ADD COLUMN IF NOT EXISTS metadata jsonb NOT NULL DEFAULT '{}'::jsonb;

ALTER TABLE ocr_results ADD COLUMN IF NOT EXISTS eir_transaction_id uuid;
ALTER TABLE ocr_results ADD COLUMN IF NOT EXISTS detected_container_no varchar(30);
ALTER TABLE ocr_results ADD COLUMN IF NOT EXISTS detected_seal_no varchar(100);
ALTER TABLE ocr_results ADD COLUMN IF NOT EXISTS subject_type varchar(30);

ALTER TABLE container_damage_reports ADD COLUMN IF NOT EXISTS container_visit_id uuid;
ALTER TABLE container_damage_reports ADD COLUMN IF NOT EXISTS eir_transaction_id uuid;
ALTER TABLE container_damage_reports ADD COLUMN IF NOT EXISTS component_code varchar(50);
ALTER TABLE container_damage_reports ADD COLUMN IF NOT EXISTS damage_type varchar(80);
ALTER TABLE container_damage_reports ADD COLUMN IF NOT EXISTS length_cm numeric(10,2);
ALTER TABLE container_damage_reports ADD COLUMN IF NOT EXISTS width_cm numeric(10,2);
ALTER TABLE container_damage_reports ADD COLUMN IF NOT EXISTS height_cm numeric(10,2);

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'fk_gate_media_eir_transaction'
      AND conrelid = 'gate_media'::regclass
  ) THEN
    ALTER TABLE gate_media ADD CONSTRAINT fk_gate_media_eir_transaction
      FOREIGN KEY (eir_transaction_id) REFERENCES eir_transactions(id) ON DELETE SET NULL;
  END IF;
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'fk_ocr_results_eir_transaction'
      AND conrelid = 'ocr_results'::regclass
  ) THEN
    ALTER TABLE ocr_results ADD CONSTRAINT fk_ocr_results_eir_transaction
      FOREIGN KEY (eir_transaction_id) REFERENCES eir_transactions(id) ON DELETE SET NULL;
  END IF;
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'fk_damage_reports_container_visit'
      AND conrelid = 'container_damage_reports'::regclass
  ) THEN
    ALTER TABLE container_damage_reports ADD CONSTRAINT fk_damage_reports_container_visit
      FOREIGN KEY (container_visit_id) REFERENCES container_visits(id) ON DELETE SET NULL;
  END IF;
  IF NOT EXISTS (
    SELECT 1 FROM pg_constraint
    WHERE conname = 'fk_damage_reports_eir_transaction'
      AND conrelid = 'container_damage_reports'::regclass
  ) THEN
    ALTER TABLE container_damage_reports ADD CONSTRAINT fk_damage_reports_eir_transaction
      FOREIGN KEY (eir_transaction_id) REFERENCES eir_transactions(id) ON DELETE SET NULL;
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_gate_media_eir_transaction ON gate_media(eir_transaction_id);
CREATE INDEX IF NOT EXISTS ix_ocr_results_eir_transaction ON ocr_results(eir_transaction_id);
CREATE INDEX IF NOT EXISTS ix_damage_reports_eir_transaction ON container_damage_reports(eir_transaction_id);

-- The original columns remain for code compatibility, but new operational writes
-- should use container_visits/eir_transactions instead of overwriting history.
COMMENT ON COLUMN containers.seal_no IS
  'LEGACY current-value cache. Authoritative historical seals are in eir_seals.';
COMMENT ON COLUMN containers.gross_weight_kg IS
  'LEGACY current-value cache. Transaction weight is stored in container_visits/eir_transactions.';
COMMENT ON COLUMN containers.vessel_call_id IS
  'LEGACY current-value cache. Visit vessel call is stored in container_visits.';
COMMENT ON COLUMN containers.arrived_at IS
  'LEGACY current-value cache. Use EIR check-in and container visit timestamps for history.';
COMMENT ON COLUMN containers.left_at IS
  'LEGACY current-value cache. Use EIR check-out and container visit timestamps for history.';

CREATE OR REPLACE FUNCTION set_eir_updated_at()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  NEW.updated_at := now();
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_container_visits_updated_at ON container_visits;
CREATE TRIGGER trg_container_visits_updated_at
BEFORE UPDATE ON container_visits
FOR EACH ROW EXECUTE FUNCTION set_eir_updated_at();

DROP TRIGGER IF EXISTS trg_eir_transactions_updated_at ON eir_transactions;
CREATE TRIGGER trg_eir_transactions_updated_at
BEFORE UPDATE ON eir_transactions
FOR EACH ROW EXECUTE FUNCTION set_eir_updated_at();

-- Search projection matching the Tiên Sa EIR lookup screen.
CREATE OR REPLACE VIEW vw_eir_search AS
SELECT
  e.id AS eir_id,
  e.reference_no,
  c.container_no,
  e.plate_number,
  e.activity,
  CASE e.activity WHEN 'in' THEN 'Hạ' WHEN 'out' THEN 'Bốc' END AS activity_vi,
  e.location_code,
  e.check_in_at,
  e.check_out_at,
  e.eir_status,
  count(m.id)::integer AS media_count
FROM eir_transactions e
JOIN container_visits cv ON cv.id = e.container_visit_id
JOIN containers c ON c.id = cv.container_id
LEFT JOIN eir_media m ON m.eir_transaction_id = e.id
GROUP BY e.id, c.container_no;

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20260925_001', 'Develop compatibility plus Tiên Sa visit, EIR, seal, damage and durable S3 media model')
ON CONFLICT (version) DO NOTHING;

COMMIT;
