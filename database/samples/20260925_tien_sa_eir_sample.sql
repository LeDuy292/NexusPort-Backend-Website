-- OPTIONAL SAMPLE/ACCEPTANCE DATA ONLY. Do not run on production without approval.
-- Reproduces the two Tiên Sa receipts supplied for HAMU2515680.

BEGIN;

INSERT INTO container_types(code, size, category, description)
VALUES ('45G0', 'ft40', 'dry', '40-foot high-cube general-purpose dry container (Tiên Sa ISO 45G0)')
ON CONFLICT (code) DO NOTHING;

INSERT INTO containers(container_type_id, container_no, cargo_type, status)
SELECT id, 'HAMU2515680', 'general', 'expected'
FROM container_types WHERE code = '45G0'
ON CONFLICT (container_no) DO NOTHING;

INSERT INTO container_visits(
  visit_reference, container_id, visit_status, load_status, cargo_type,
  gross_weight_kg, bl_booking_no, customer_name, transport_company_name,
  valid_to, started_at, completed_at
)
SELECT
  'TS-HAMU2515680-20260228-OUT', c.id, 'completed', 'full', 'general',
  28100, 'HLCUMTR251047484',
  'CHI NHÁNH CÔNG TY CỔ PHẦN ITL LOGISTICS TẠI MIỀN TRUNG',
  'CHI NHÁNH CÔNG TY CỔ PHẦN ITL LOGISTICS TẠI MIỀN TRUNG',
  timestamptz '2026-02-28 23:59:59+07',
  timestamptz '2026-02-28 16:24:26+07', timestamptz '2026-02-28 17:02:03+07'
FROM containers c WHERE c.container_no = 'HAMU2515680'
ON CONFLICT (visit_reference) DO NOTHING;

INSERT INTO container_visits(
  visit_reference, container_id, visit_status, load_status, cargo_type,
  gross_weight_kg, bl_booking_no, customer_name, transport_company_name,
  started_at, completed_at
)
SELECT
  'TS-HAMU2515680-20260306-IN', c.id, 'completed', 'full', 'general',
  10000, '24249678',
  'CÔNG TY TNHH HAPAG-LLOYD (VIỆT NAM)',
  'CÔNG TY TNHH HAPAG-LLOYD (VIỆT NAM)',
  timestamptz '2026-03-06 08:57:45+07', timestamptz '2026-03-06 09:04:24+07'
FROM containers c WHERE c.container_no = 'HAMU2515680'
ON CONFLICT (visit_reference) DO NOTHING;

INSERT INTO eir_transactions(
  reference_no, container_visit_id, activity, operator_code, customer_name,
  location_code, gate_label, valid_to, check_in_at, check_out_at, bl_booking_no,
  load_status, iso_code, container_type_description, size_feet,
  cargo_type_description, gross_weight_kg, imdg_class, remark,
  vessel_name, voyage_in, voyage_out, vessel_datetime_1, vessel_datetime_2,
  transport_company_name, plate_number, issued_at, raw_payload
)
SELECT
  '260228SP530895', cv.id, 'out', 'HAG',
  'CHI NHÁNH CÔNG TY CỔ PHẦN ITL LOGISTICS TẠI MIỀN TRUNG',
  'C-02-6-1', 'OUT GATE 1 / NHIPTT_^AP',
  timestamptz '2026-02-28 23:59:59+07',
  timestamptz '2026-02-28 16:24:26+07', timestamptz '2026-02-28 17:02:03+07',
  'HLCUMTR251047484', 'full', '45G0', 'General Dry', 40, 'General', 28100,
  '/', 'mop tran 50cmx50cm', 'AEGEAN EXPRESS', '366S', '366S',
  timestamptz '2026-02-04 22:20:00+07', timestamptz '2026-02-05 09:00:00+07',
  'CHI NHÁNH CÔNG TY CỔ PHẦN ITL LOGISTICS TẠI MIỀN TRUNG', '50H63074',
  timestamptz '2026-02-28 17:02:03+07',
  jsonb_build_object('source', 'port_receipt_image', 'note', 'Text retained as visible on supplied EIR')
FROM container_visits cv WHERE cv.visit_reference = 'TS-HAMU2515680-20260228-OUT'
ON CONFLICT (reference_no) DO NOTHING;

INSERT INTO eir_transactions(
  reference_no, container_visit_id, activity, operator_code, customer_name,
  location_code, gate_label, check_in_at, check_out_at, bl_booking_no,
  load_status, iso_code, container_type_description, size_feet,
  cargo_type_description, gross_weight_kg, imdg_class, sound_damage_code, remark,
  vessel_name, voyage_in, voyage_out, vessel_datetime_1, vessel_datetime_2,
  transport_company_name, plate_number, issued_at, raw_payload
)
SELECT
  '260305PA535225', cv.id, 'in', 'HAG', 'CÔNG TY TNHH HAPAG-LLOYD (VIỆT NAM)',
  'H-06-6-2', 'OUT GATE 1',
  timestamptz '2026-03-06 08:57:45+07', timestamptz '2026-03-06 09:04:24+07',
  '24249678', 'full', '45G0', 'General Dry', 40, 'General', 10000,
  '/', 'S', 'Dinh vi', 'MAERSK HAI PHONG', '554B', '554B',
  timestamptz '2026-03-08 19:20:00+07', timestamptz '2026-03-08 19:20:00+07',
  'CÔNG TY TNHH HAPAG-LLOYD (VIỆT NAM)', '43H00392',
  timestamptz '2026-03-06 09:04:24+07',
  jsonb_build_object('source', 'port_receipt_image', 'note', 'Text retained as visible on supplied EIR')
FROM container_visits cv WHERE cv.visit_reference = 'TS-HAMU2515680-20260306-IN'
ON CONFLICT (reference_no) DO NOTHING;

INSERT INTO eir_seals(eir_transaction_id, sequence_no, seal_number, raw_value, seal_condition, notes)
SELECT id, 1, 'UL6824219', 'UL6824219/ko', 'unknown', 'Port suffix /ko retained; business meaning needs confirmation'
FROM eir_transactions WHERE reference_no = '260228SP530895'
ON CONFLICT (eir_transaction_id, sequence_no) DO NOTHING;

INSERT INTO eir_seals(eir_transaction_id, sequence_no, seal_number, raw_value, seal_condition)
SELECT id, 1, '1', '1', 'unknown'
FROM eir_transactions WHERE reference_no = '260305PA535225'
ON CONFLICT (eir_transaction_id, sequence_no) DO NOTHING;

INSERT INTO eir_seals(eir_transaction_id, sequence_no, seal_number, raw_value, seal_condition)
SELECT id, 2, 'HLC3422709', 'HLC3422709', 'unknown'
FROM eir_transactions WHERE reference_no = '260305PA535225'
ON CONFLICT (eir_transaction_id, sequence_no) DO NOTHING;

INSERT INTO eir_damage_observations(
  eir_transaction_id, component_code, damage_type, description, length_cm, width_cm, raw_payload
)
SELECT id, 'roof', 'dent', 'mop tran 50cmx50cm', 50, 50,
       jsonb_build_object('original_remark', 'mop tran 50cmx50cm')
FROM eir_transactions WHERE reference_no = '260228SP530895'
  AND NOT EXISTS (
    SELECT 1 FROM eir_damage_observations d
    WHERE d.eir_transaction_id = eir_transactions.id
      AND d.description = 'mop tran 50cmx50cm'
  );

COMMIT;
