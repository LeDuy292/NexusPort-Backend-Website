BEGIN;
DO $$
BEGIN
 IF EXISTS(SELECT 1 FROM nexusport_schema_versions WHERE version='20261114_048') THEN RETURN; END IF;
 -- Three explicitly approved legacy test accounts, matched to the screenshot:
 -- Gia Huy\8k, Nguyen Dinh Tien, Phan Nguyen Gia Huy (license 660225020133).
 UPDATE users SET email_verified_at=COALESCE(email_verified_at,now()),invitation_pending=false,is_active=true,
  password=crypt('NexusPort@2026',gen_salt('bf',12)),session_id=gen_random_uuid()::text,
  reset_token=NULL,reset_token_exp=NULL,updated_at=now()
 WHERE role='Driver' AND id IN ('aaf896d9-0238-4c16-b14a-2e7f952fb05b',
  'e738b67f-dba6-4f5e-895f-eb2e81afe3bc','8b2a5ba6-8985-4926-a0b5-8cc65a29919d');
 UPDATE users SET is_active=false,invitation_pending=true,session_id=gen_random_uuid()::text,updated_at=now()
 WHERE role='Driver' AND email_verified_at IS NULL;
 UPDATE account_invitations SET used_at=now() WHERE used_at IS NULL AND user_id IN
  (SELECT id FROM users WHERE role='Driver' AND email_verified_at IS NOT NULL);
END $$;
CREATE OR REPLACE FUNCTION guard_account_role() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.role='Berth Staff' AND (TG_OP='INSERT' OR NEW.role IS DISTINCT FROM OLD.role OR NEW.is_active) THEN
  RAISE EXCEPTION 'This role has been retired';
 END IF;
 IF TG_OP='UPDATE' AND NEW.role='Driver' AND NEW.email IS DISTINCT FROM OLD.email THEN
  NEW.email_verified_at=NULL; NEW.invitation_pending=true; NEW.is_active=false; NEW.session_id=NULL;
 END IF;
 IF NEW.invitation_pending AND NEW.is_active THEN RAISE EXCEPTION 'Accept account invitation first'; END IF;
 IF NEW.role='Driver' AND NEW.is_active AND NEW.email_verified_at IS NULL THEN
  RAISE EXCEPTION 'Driver email must be verified before account activation';
 END IF;
 RETURN NEW;
END $$;
INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261114_048','Verify three approved legacy drivers; require email verification for all other drivers') ON CONFLICT DO NOTHING;
COMMIT;
