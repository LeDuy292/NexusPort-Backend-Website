BEGIN;
-- Explicit legacy account IDs from the pre-onboarding audit. Never select by
-- current role/date alone: accounts approved through the new flow stay blocked.
DO $$
DECLARE legacy_ids uuid[] := ARRAY[
 'e19f2c18-8b94-4241-aa90-87c3cce637e9',
 '5a568f09-959e-4258-a7fa-31698a452410',
 '8619d48d-0529-4b75-85a5-66720279ca94',
 'aaf896d9-0238-4c16-b14a-2e7f952fb05b',
 'e738b67f-dba6-4f5e-895f-eb2e81afe3bc',
 'ff3f8ab1-5fd4-4d61-8950-7385cfb527dc',
 '8b2a5ba6-8985-4926-a0b5-8cc65a29919d',
 '1b7b1534-1044-4615-b9cd-ac5bcb63f7d9',
 '2209d775-f280-4052-9586-61363c9ede9a'
]::uuid[];
BEGIN
 IF EXISTS(SELECT 1 FROM nexusport_schema_versions WHERE version='20261114_046') THEN RETURN; END IF;
 UPDATE users SET invitation_pending=false,is_active=true,updated_at=now()
 WHERE id=ANY(legacy_ids) AND role='Driver' AND (invitation_pending OR NOT is_active);
 UPDATE account_invitations SET used_at=now()
 WHERE user_id=ANY(legacy_ids) AND used_at IS NULL;
END $$;
INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261114_046','Unblock audited legacy drivers; preserve invitation requirements for new accounts')
ON CONFLICT DO NOTHING;
COMMIT;
