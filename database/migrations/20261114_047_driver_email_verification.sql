BEGIN;
-- Account access and proof of email ownership are separate: legacy accounts
-- stay unblocked, but only consuming an invitation proves email ownership.
ALTER TABLE users ADD COLUMN IF NOT EXISTS email_verified_at timestamptz;
INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261114_047','Track driver email verification separately from account blocking')
ON CONFLICT DO NOTHING;
COMMIT;
