-- 20261110_034_seed_standard_gates.sql
-- Seed standard entry and exit gates (GATE-01 and GATE-03) for Smart Route navigation

BEGIN;

INSERT INTO gates (id, code, name, location, is_active)
VALUES 
  ('a0000000-0000-0000-0000-000000000001', 'GATE-01', 'Cổng vào', 'Khu vực cổng phía Bắc', true),
  ('a0000000-0000-0000-0000-000000000003', 'GATE-03', 'Cổng ra', 'Khu vực cổng phía Nam', true)
ON CONFLICT (id) DO UPDATE 
SET is_active = EXCLUDED.is_active, 
    code = EXCLUDED.code, 
    name = EXCLUDED.name, 
    location = EXCLUDED.location;

INSERT INTO nexusport_schema_versions (version, description)
VALUES ('20261110_034', 'Seed standard gates GATE-01 and GATE-03 for Smart Route')
ON CONFLICT (version) DO NOTHING;

COMMIT;
