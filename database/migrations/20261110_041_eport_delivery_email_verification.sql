BEGIN;
-- Private challenges may precede saving a draft. Source systems are not changed.
CREATE TABLE IF NOT EXISTS eport_service_email_verifications (
 id uuid PRIMARY KEY,
 draft_id uuid NOT NULL,
 actor_id uuid NOT NULL REFERENCES users(id),
 carrier_id uuid NOT NULL REFERENCES carriers(id),
 email varchar(254) NOT NULL,
 source_hash varchar(64) NOT NULL,
 source_payload jsonb NOT NULL,
 code_hash varchar(128) NOT NULL,
 salt varchar(128) NOT NULL,
 attempts integer NOT NULL DEFAULT 0 CHECK(attempts BETWEEN 0 AND 5),
 created_at timestamptz NOT NULL DEFAULT now(),
 expires_at timestamptz NOT NULL DEFAULT now()+interval '5 minutes',
 verified_at timestamptz,
 invalidated_at timestamptz
);
CREATE INDEX IF NOT EXISTS ix_eport_service_email_actor ON eport_service_email_verifications(actor_id,created_at DESC);
CREATE INDEX IF NOT EXISTS ix_eport_service_email_draft ON eport_service_email_verifications(draft_id,actor_id,source_hash);
CREATE OR REPLACE FUNCTION enforce_eport_service_registration_email() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF COALESCE(NEW.snapshot->'content'->>'workflowVersion','0') <> '2' OR NOT EXISTS(
  SELECT 1 FROM eport_service_email_verifications e JOIN users u ON u.id=e.actor_id AND u.is_active
  WHERE e.draft_id=NEW.draft_id AND e.actor_id=NEW.confirmed_by AND e.carrier_id=NEW.carrier_id
   AND e.email=lower(trim(u.email)) AND e.invalidated_at IS NULL AND e.verified_at>now()-interval '30 minutes'
   AND e.source_payload=jsonb_build_object('flow',NEW.snapshot->'content'->'flow',
     'mainServiceId',NEW.snapshot->'content'->'mainServiceId','source',NEW.snapshot->'content'->'source')
 ) THEN
  RAISE EXCEPTION 'Service registration requires current source-bound email verification'
   USING ERRCODE='23514',CONSTRAINT='eport_service_registration_email_guard';
 END IF;
 RETURN NEW;
END $$;
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid='eport_service_registrations'::regclass AND tgname='eport_service_registration_email_guard') THEN
  CREATE TRIGGER eport_service_registration_email_guard BEFORE INSERT ON eport_service_registrations
   FOR EACH ROW EXECUTE FUNCTION enforce_eport_service_registration_email();
 END IF;
END $$;
INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261110_041','Email-bound delivery registration verification; private OTP challenges and server-enforced new registration workflow')
ON CONFLICT(version) DO NOTHING;
COMMIT;
