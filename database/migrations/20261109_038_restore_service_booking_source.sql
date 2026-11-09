BEGIN;

-- Some deployments retained the export tables but lost the company/line/request
-- columns used by the C# service source. Restore the contract additively. Legacy
-- rows remain unassigned: never guess company ownership from names or creators.
ALTER TABLE export_bookings ADD COLUMN IF NOT EXISTS carrier_id uuid REFERENCES carriers(id);
ALTER TABLE export_bookings ADD COLUMN IF NOT EXISTS line_user_id uuid REFERENCES users(id);
ALTER TABLE export_bookings ADD COLUMN IF NOT EXISTS request_id uuid;
ALTER TABLE export_bookings ADD COLUMN IF NOT EXISTS request_hash text;

CREATE INDEX IF NOT EXISTS ix_eport_export_source_company
 ON export_bookings(carrier_id,created_at DESC,id) WHERE carrier_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_eport_export_source_request
 ON export_bookings(carrier_id,request_id) WHERE carrier_id IS NOT NULL AND request_id IS NOT NULL;

INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261109_038','Restore explicit company, shipping-line and request links for C# service Booking sources without assigning legacy rows')
ON CONFLICT(version) DO NOTHING;
COMMIT;
