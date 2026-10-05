-- Post-migration integrity checks. Invalid-row queries should return zero rows.

SELECT version, description, applied_at
FROM nexusport_schema_versions
WHERE version = '20260925_001';

-- No receipt may end before it starts.
SELECT reference_no, check_in_at, check_out_at
FROM eir_transactions
WHERE check_out_at IS NOT NULL AND check_out_at < check_in_at;

-- Every EIR container must match the visit container through the search view.
SELECT * FROM vw_eir_search ORDER BY check_in_at;

-- Detect accidental duplicate current positions already possible in the legacy schema.
SELECT container_id, count(*) AS current_position_count
FROM container_positions
WHERE is_current
GROUP BY container_id
HAVING count(*) > 1;

SELECT slot_id, count(*) AS current_container_count
FROM container_positions
WHERE is_current
GROUP BY slot_id
HAVING count(*) > 1;

-- Acceptance result for the optional supplied-receipt sample.
SELECT
  s.container_no, s.reference_no, s.activity, s.activity_vi, s.location_code,
  s.check_in_at, s.check_out_at, s.plate_number, s.media_count,
  array_agg(es.raw_value ORDER BY es.sequence_no) FILTER (WHERE es.id IS NOT NULL) AS seals
FROM vw_eir_search s
LEFT JOIN eir_seals es ON es.eir_transaction_id = s.eir_id
WHERE s.container_no = 'HAMU2515680'
GROUP BY s.eir_id, s.container_no, s.reference_no, s.activity, s.activity_vi,
         s.location_code, s.check_in_at, s.check_out_at, s.plate_number, s.media_count
ORDER BY s.check_in_at;
