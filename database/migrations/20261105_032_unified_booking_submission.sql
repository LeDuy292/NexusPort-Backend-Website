BEGIN;
-- A customer proposal is not an accepted port appointment or an actual yard stay.
-- Existing export bookings keep NULL and continue through their original workflow.
ALTER TABLE export_bookings ADD COLUMN IF NOT EXISTS submission jsonb;
INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261105_032','Unified customer cargo, fleet proposal and document submission before shipping line approval')
ON CONFLICT(version) DO NOTHING;
COMMIT;
