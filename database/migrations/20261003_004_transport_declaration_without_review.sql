BEGIN;

-- Transport-company intake is a declaration, not an approval workflow.
-- Keep data_status only for sources that actually perform data verification.
ALTER TABLE container_visits
  ALTER COLUMN data_status DROP NOT NULL,
  ALTER COLUMN data_status DROP DEFAULT;

UPDATE container_visits
   SET data_status = NULL
 WHERE source_type = 'transport_company';

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261003_004', 'Remove reconciliation state from transport container declarations')
ON CONFLICT (version) DO NOTHING;

COMMIT;
