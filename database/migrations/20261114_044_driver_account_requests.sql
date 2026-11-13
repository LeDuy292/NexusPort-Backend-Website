BEGIN;
CREATE TABLE IF NOT EXISTS account_requests (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 role varchar(50) NOT NULL CHECK (role = 'Driver'),
 carrier_id uuid NOT NULL REFERENCES carriers(id),
 driver_id uuid REFERENCES drivers(id),
 requested_by uuid NOT NULL REFERENCES users(id),
 username varchar(100) NOT NULL,
 email varchar(254) NOT NULL,
 profile jsonb NOT NULL,
 status varchar(20) NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','approved','rejected')),
 reviewed_by uuid REFERENCES users(id), reviewed_at timestamptz,
 user_id uuid REFERENCES users(id),
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_account_request_username ON account_requests(lower(username)) WHERE status='pending';
CREATE UNIQUE INDEX IF NOT EXISTS ux_account_request_email ON account_requests(lower(email)) WHERE status='pending';
CREATE UNIQUE INDEX IF NOT EXISTS ux_account_request_driver ON account_requests(driver_id) WHERE status='pending';
CREATE TABLE IF NOT EXISTS account_invitations (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
 request_id uuid NOT NULL REFERENCES account_requests(id),
 token_hash char(64) NOT NULL UNIQUE,
 expires_at timestamptz NOT NULL,
 used_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE users ADD COLUMN IF NOT EXISTS invitation_pending boolean NOT NULL DEFAULT false;
-- Preserve retired identities and audit/history references; prevent authentication
-- and new assignment of the retired role without rewriting historical migrations.
UPDATE users SET is_active=false, session_id=NULL WHERE role='Berth Staff';
CREATE OR REPLACE FUNCTION guard_account_role() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.role='Berth Staff' AND (TG_OP='INSERT' OR NEW.role IS DISTINCT FROM OLD.role OR NEW.is_active) THEN
  RAISE EXCEPTION 'This role has been retired';
 END IF;
 IF NEW.invitation_pending AND NEW.is_active THEN RAISE EXCEPTION 'Accept account invitation first'; END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_guard_account_role ON users;
CREATE TRIGGER trg_guard_account_role BEFORE INSERT OR UPDATE ON users FOR EACH ROW EXECUTE FUNCTION guard_account_role();
INSERT INTO nexusport_schema_versions(version,description) VALUES('20261114_044','Driver account requests, one-day invitation activation and retired role guard') ON CONFLICT DO NOTHING;
COMMIT;
