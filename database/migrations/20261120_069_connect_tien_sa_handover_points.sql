BEGIN;

-- Attach the existing handover names to marked stops on the current Tiên Sa
-- road graph. The connector is a logical link at the same coordinates.
WITH handovers(location, node_id, road_id) AS (VALUES
  ('A-S1','tsa:handover:A-S1','tsa:772,1181.25'),
  ('A-S2','tsa:handover:A-S2','tsa:772,1151.25')
), missing AS (
  SELECT m.id,h.*,r.node AS road,legacy.node AS legacy
  FROM smart_yard_maps m CROSS JOIN handovers h
  JOIN LATERAL (SELECT n AS node FROM jsonb_array_elements(m.map_data->'nodes') n WHERE n->>'id'=h.road_id) r ON true
  LEFT JOIN LATERAL (SELECT n AS node FROM jsonb_array_elements(m.map_data->'nodes') n WHERE n->>'id'=h.node_id) legacy ON true
  WHERE m.is_active AND m.version='tien-sa-simple-60f497a2587a'
    AND NOT EXISTS (SELECT 1 FROM jsonb_array_elements(m.map_data->'edges') e WHERE h.node_id IN (e->>'from',e->>'to'))
    AND NOT EXISTS (SELECT 1 FROM jsonb_array_elements(m.map_data->'nodes') n
      WHERE n->>'location'=h.location AND n->>'id'<>h.node_id)
), patch AS (
  SELECT id,
    jsonb_agg(COALESCE(legacy,'{}'::jsonb) || jsonb_build_object(
      'id',node_id,'location',location,'label','Điểm giao nhận '||location,
      'x',road->'x','y',road->'y','restricted',COALESCE(legacy->'restricted','false'::jsonb)
    ) ORDER BY location) AS nodes,
    jsonb_agg(jsonb_build_object('id','access:'||node_id,'from',road_id,'to',node_id,
      'distanceMeters',1,'travelSeconds',1,'bidirectional',true,'restricted',false,
      'instruction','Tiếp cận điểm giao nhận '||location||' trên làn giao nhận.') ORDER BY location) AS edges
  FROM missing GROUP BY id
)
UPDATE smart_yard_maps m
SET map_data=jsonb_set(jsonb_set(m.map_data,'{nodes}',(
  SELECT COALESCE(jsonb_agg(n.node ORDER BY n.ordinality),'[]'::jsonb)
  FROM jsonb_array_elements(m.map_data->'nodes') WITH ORDINALITY n(node,ordinality)
  WHERE NOT EXISTS (SELECT 1 FROM missing h WHERE h.id=m.id AND h.node_id=n.node->>'id')
) || p.nodes),'{edges}',m.map_data->'edges' || p.edges),
updated_at=now()
FROM patch p WHERE m.id=p.id;

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261120_069', 'Connect Tiên Sa handover points to existing truck stops')
ON CONFLICT (version) DO NOTHING;

COMMIT;
