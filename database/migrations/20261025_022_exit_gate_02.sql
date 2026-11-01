BEGIN;
-- Preserve gate UUIDs and every historical foreign-key relation.
DO $$
DECLARE exit_id uuid; legacy_id uuid; previous smart_yard_maps%ROWTYPE;
  next_id uuid; updated_nodes jsonb;
BEGIN
  IF EXISTS(SELECT 1 FROM nexusport_schema_versions WHERE version='20261025_022') THEN RETURN; END IF;
  SELECT id INTO exit_id FROM gates WHERE code='GATE-03' FOR UPDATE;
  IF exit_id IS NOT NULL THEN
    SELECT id INTO legacy_id FROM gates WHERE code='GATE-02' FOR UPDATE;
    IF legacy_id IS NOT NULL THEN
      IF EXISTS(SELECT 1 FROM gates WHERE id=legacy_id AND is_active) THEN
        RAISE EXCEPTION 'GATE-02 is active; review gate identities before renaming.';
      END IF;
      IF EXISTS(SELECT 1 FROM gates WHERE code='GATE-02-LEGACY') THEN
        RAISE EXCEPTION 'Reserved legacy gate code already exists; manual review required.';
      END IF;
      UPDATE gates SET code='GATE-02-LEGACY' WHERE id=legacy_id;
    END IF;
    UPDATE gates SET code='GATE-02',name='Cổng ra',is_active=true WHERE id=exit_id;
  ELSIF NOT EXISTS(SELECT 1 FROM gates WHERE code='GATE-02' AND is_active) THEN
    RAISE EXCEPTION 'No existing exit gate can be preserved.';
  END IF;
  UPDATE gates SET is_active=false WHERE code NOT IN ('GATE-01','GATE-02');

  -- Clone the active graph. Keep opaque node/edge IDs so recorded driver
  -- positions and road restrictions retain their exact meaning.
  SELECT * INTO previous FROM smart_yard_maps WHERE is_active FOR UPDATE;
  IF previous.id IS NOT NULL AND EXISTS(SELECT 1 FROM jsonb_array_elements(previous.map_data->'nodes') n WHERE n->>'gateCode'='GATE-03') THEN
    SELECT jsonb_agg(CASE WHEN n->>'gateCode'='GATE-03' THEN n||jsonb_build_object('gateCode','GATE-02','location','GATE-02','label','Cổng ra') ELSE n END)
      INTO updated_nodes FROM jsonb_array_elements(previous.map_data->'nodes') n;
    UPDATE smart_yard_maps SET is_active=false WHERE id=previous.id;
    INSERT INTO smart_yard_maps(version,name,map_data,is_active)
      VALUES('NXP-exit-gate02-v1','NexusPort · Cổng vào 01, cổng ra 02',
        previous.map_data||jsonb_build_object('version','NXP-exit-gate02-v1','nodes',updated_nodes,'updatedAt',now()),true)
      RETURNING id INTO next_id;
    INSERT INTO smart_yard_conditions(map_id,target_type,target_id,restricted,congestion,source,valid_until,observed_at,updated_by)
      SELECT next_id,target_type,target_id,restricted,congestion,source,valid_until,observed_at,updated_by
      FROM smart_yard_conditions WHERE map_id=previous.id;
  END IF;
END $$;
INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261025_022','Rename the existing exit to GATE-02; preserve gate UUIDs, historical entrance and map revisions')
ON CONFLICT(version) DO NOTHING;
COMMIT;
