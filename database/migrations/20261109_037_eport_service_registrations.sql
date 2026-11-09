BEGIN;

-- Immutable confirmations of private drafts. No billing/payment, source workflow,
-- container lifecycle or gate permission is changed by this migration.
CREATE TABLE IF NOT EXISTS eport_service_registrations (
 id uuid PRIMARY KEY,
 draft_id uuid NOT NULL UNIQUE REFERENCES eport_service_drafts(id),
 carrier_id uuid NOT NULL REFERENCES carriers(id),
 confirmed_by uuid NOT NULL REFERENCES users(id),
 number varchar(40) NOT NULL UNIQUE,
 draft_version integer NOT NULL CHECK(draft_version>0),
 quote_token varchar(64) NOT NULL,
 status varchar(24) NOT NULL CHECK(status IN ('CONFIRMED','PENDING_APPROVAL')),
 payment_status varchar(16) NOT NULL CHECK(payment_status IN ('UNPAID','NOT_REQUIRED')),
 currency char(3) NOT NULL DEFAULT 'VND' CHECK(currency='VND'),
 total numeric(24,0) NOT NULL CHECK(total>=0),
 snapshot jsonb NOT NULL CHECK(jsonb_typeof(snapshot)='object'),
 confirmed_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_eport_service_registrations_company
 ON eport_service_registrations(carrier_id,confirmed_at DESC,id);

CREATE OR REPLACE FUNCTION guard_eport_service_registration_immutable() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
 RAISE EXCEPTION 'Confirmed service registration snapshots are immutable.' USING ERRCODE='23514';
END $$;
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid='eport_service_registrations'::regclass
               AND tgname='eport_service_registration_immutable') THEN
  CREATE TRIGGER eport_service_registration_immutable BEFORE UPDATE OR DELETE
   ON eport_service_registrations FOR EACH ROW EXECUTE FUNCTION guard_eport_service_registration_immutable();
 END IF;
END $$;

INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261109_037','Immutable company service confirmations, checked fee snapshots and independent approval/payment statuses')
ON CONFLICT(version) DO NOTHING;
COMMIT;
