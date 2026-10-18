BEGIN;
-- The pre-existing C# gate verifier had no persisted table in the shared schema.
CREATE TABLE IF NOT EXISTS gate_verification_records (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  verification_code varchar(100) NOT NULL UNIQUE,
  gate_code varchar(50) NOT NULL,
  lane_code varchar(50),
  verification_type varchar(50) NOT NULL,
  status varchar(50) NOT NULL,
  failure_reason varchar(100),
  verified_at timestamptz NOT NULL DEFAULT now(),
  detected_plate varchar(50) NOT NULL,
  plate_confidence double precision,
  rfid_tag varchar(100),
  vehicle_detected boolean NOT NULL DEFAULT true,
  camera_id varchar(100),
  booking_id uuid REFERENCES bookings(id),
  booking_number text,
  truck_id uuid REFERENCES trucks(id),
  vehicle_plate text,
  driver_id uuid REFERENCES drivers(id),
  driver_name text,
  plate_image_url text,
  overview_image_url text,
  notes varchar(1000),
  processed_by text,
  ocr_raw_data text
);
CREATE INDEX IF NOT EXISTS ix_gate_verification_gate_time ON gate_verification_records(gate_code,verified_at DESC);
CREATE INDEX IF NOT EXISTS ix_gate_verification_plate ON gate_verification_records(detected_plate);
INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261018_019','Persist C# gate recognition evidence including gate code for barrier command validation')
ON CONFLICT(version) DO NOTHING;
COMMIT;
