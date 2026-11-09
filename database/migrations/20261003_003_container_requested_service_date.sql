BEGIN;

ALTER TABLE container_visits
  ADD COLUMN IF NOT EXISTS requested_service_date date;

UPDATE container_visits
   SET requested_service_date = requested_pickup_date
 WHERE requested_service_date IS NULL
   AND requested_pickup_date IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_container_visits_requested_service_date
  ON container_visits(requested_service_date);

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261003_003', 'Add common requested service date for transport container declarations')
ON CONFLICT (version) DO NOTHING;

COMMIT;
