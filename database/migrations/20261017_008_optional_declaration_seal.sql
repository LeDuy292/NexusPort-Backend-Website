BEGIN;
-- Seal remains optional and belongs to the declaration/storage occurrence.
-- Existing nullable columns are reused; preserve all historical seal/EIR data.
COMMENT ON COLUMN container_visits.seal_number IS 'Optional carrier-declared seal number for this yard visit; NULL when not supplied. Physical verification and seal changes belong to later operational workflows.';
COMMENT ON COLUMN containers.seal_no IS 'Current seal projection from the latest declaration; optional, NULL when unknown. Historical seals are retained per visit/EIR.';
INSERT INTO nexusport_schema_versions(version,description)
VALUES ('20261017_008','Allow optional carrier-declared seal using existing nullable visit and container columns')
ON CONFLICT(version) DO NOTHING;
COMMIT;
