BEGIN;
-- Legacy dev sync had removed the FK, leaving booked profiles pointing to
-- deleted users. New writes must retain referential integrity. Existing broken
-- links remain visible for explicit repair rather than guessed backfills.
DO $$
BEGIN
 IF NOT EXISTS (
  SELECT 1 FROM pg_constraint c JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=ANY(c.conkey)
  WHERE c.contype='f' AND c.conrelid='drivers'::regclass AND c.confrelid='users'::regclass AND a.attname='user_id'
 ) THEN
  ALTER TABLE drivers ADD CONSTRAINT fk_driver_login_user FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE RESTRICT NOT VALID;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM drivers d LEFT JOIN users u ON u.id=d.user_id WHERE d.user_id IS NOT NULL AND u.id IS NULL)
 AND EXISTS(SELECT 1 FROM pg_constraint WHERE conname='fk_driver_login_user' AND conrelid='drivers'::regclass) THEN
  ALTER TABLE drivers VALIDATE CONSTRAINT fk_driver_login_user;
 END IF;
END $$;
INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261114_045','Restore driver login referential integrity after legacy sync') ON CONFLICT DO NOTHING;
COMMIT;
