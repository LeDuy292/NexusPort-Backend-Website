BEGIN;
UPDATE gates SET is_active=false WHERE code NOT IN ('GATE-01','GATE-03');
UPDATE gates SET name=CASE code WHEN 'GATE-01' THEN 'Cổng vào' ELSE 'Cổng ra' END,is_active=true
  WHERE code IN ('GATE-01','GATE-03');

-- Preserve old map revisions and gate transactions; publish a new operational map.
DO $$
DECLARE previous smart_yard_maps%ROWTYPE; nodes jsonb; edges jsonb; next_map uuid;
BEGIN
  IF NOT EXISTS(SELECT 1 FROM smart_yard_maps WHERE version='NXP114-single-entry-exit-v2') THEN
    SELECT * INTO previous FROM smart_yard_maps WHERE is_active FOR UPDATE;
    IF previous.id IS NOT NULL THEN
      SELECT COALESCE(jsonb_agg(CASE WHEN n->>'id'='north-gate-02' THEN
        (n-'gateCode'-'location')||jsonb_build_object('label','Đường một chiều phía Bắc') ELSE n END),'[]'::jsonb)
        INTO nodes FROM jsonb_array_elements(previous.map_data->'nodes') n WHERE n->>'gateCode' IS NULL OR n->>'gateCode' IN ('GATE-01','GATE-03');
      SELECT COALESCE(jsonb_agg(e),'[]'::jsonb) INTO edges FROM jsonb_array_elements(previous.map_data->'edges') e
        WHERE e->>'from' IN (SELECT n->>'id' FROM jsonb_array_elements(nodes)n)
          AND e->>'to' IN (SELECT n->>'id' FROM jsonb_array_elements(nodes)n);
      UPDATE smart_yard_maps SET is_active=false WHERE id=previous.id;
      INSERT INTO smart_yard_maps(version,name,map_data,is_active)
        VALUES('NXP114-single-entry-exit-v2','NexusPort · Một cổng vào, một cổng ra',
          previous.map_data||jsonb_build_object('version','NXP114-single-entry-exit-v2','nodes',nodes,'edges',edges,'updatedAt',now()),true)
        RETURNING id INTO next_map;
      -- Keep live restrictions on roads that still exist.
      INSERT INTO smart_yard_conditions(map_id,target_type,target_id,restricted,congestion,source,valid_until,observed_at,updated_by)
        SELECT next_map,c.target_type,c.target_id,c.restricted,c.congestion,c.source,c.valid_until,c.observed_at,c.updated_by
        FROM smart_yard_conditions c WHERE c.map_id=previous.id AND
          ((c.target_type='node' AND c.target_id IN (SELECT n->>'id' FROM jsonb_array_elements(nodes)n)) OR
           (c.target_type='edge' AND c.target_id IN (SELECT e->>'id' FROM jsonb_array_elements(edges)e)));
    END IF;
  END IF;
END $$;
INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261018_020','One active entrance GATE-01 and one exit GATE-03; retire GATE-02 and publish map revision')
ON CONFLICT(version) DO NOTHING;
COMMIT;
