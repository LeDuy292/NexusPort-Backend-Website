-- Keep the existing TC terminology. Historical references are never rewritten.
BEGIN;
CREATE OR REPLACE FUNCTION yard_new_pickup_code() RETURNS text LANGUAGE sql VOLATILE AS $$
 SELECT 'TC-' || upper(substr(h,1,4)||'-'||substr(h,5,4)||'-'||substr(h,9,4))
 FROM (SELECT replace(gen_random_uuid()::text,'-','') AS h) s
$$;
-- Preserve the random suffix and accept the former YARD alias at the API edge.
-- Completed stays remain immutable; their codes cannot authorize new Pickups.
UPDATE container_visits SET pickup_code='TC-'||substr(pickup_code,6)
 WHERE visit_status IN ('planned','active') AND pickup_code ~ '^YARD-[A-F0-9]{4}-[A-F0-9]{4}-[A-F0-9]{4}$';
INSERT INTO nexusport_schema_versions(version,description) VALUES('20261017_013','Keep TC prefix for private pickup codes') ON CONFLICT DO NOTHING;
COMMIT;
