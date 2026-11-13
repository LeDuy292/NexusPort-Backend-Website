BEGIN;
CREATE OR REPLACE FUNCTION is_valid_container_iso6346(number text) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE STRICT AS $$
DECLARE total integer:=0; n integer; ch text; i integer;
BEGIN
  IF number !~ '^[A-Z]{3}[UJZ][0-9]{7}$' THEN RETURN false; END IF;
  FOR i IN 1..10 LOOP
    ch:=substring(number FROM i FOR 1);
    n:=CASE WHEN ch BETWEEN '0' AND '9' THEN ascii(ch)-48 ELSE ascii(ch)-55+(ascii(ch)-56)/10 END;
    total:=total+n*(1 << (i-1));
  END LOOP;
  RETURN (total%11)%10=substring(number FROM 11 FOR 1)::integer;
END $$;
ALTER TABLE containers ADD COLUMN IF NOT EXISTS identity_quarantined boolean NOT NULL DEFAULT false;
DO $$ BEGIN
  IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='ck_containers_active_iso6346' AND conrelid='containers'::regclass) THEN
    ALTER TABLE containers ADD CONSTRAINT ck_containers_active_iso6346
      CHECK(identity_quarantined OR is_valid_container_iso6346(container_no)) NOT VALID;
  END IF;
END $$;
CREATE TABLE IF NOT EXISTS container_identity_repairs (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  container_id uuid REFERENCES containers(id) ON DELETE SET NULL,
  old_number text NOT NULL,
  new_number text,
  action text NOT NULL CHECK(action IN ('checksum_correction','quarantine')),
  reason text NOT NULL,
  before_snapshot jsonb NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261113_044','ISO checksum guard, identity quarantine and audited identity corrections')
ON CONFLICT(version) DO NOTHING;
COMMIT;
