\set ON_ERROR_STOP on

BEGIN;

-- This is an explicit environment reset, not a schema migration.
-- Keep migration history, empty every application table, then recreate only
-- the seven accounts declared in src/scripts/seed.js and the minimum carrier
-- record required by carrier01.
DO $$
DECLARE
  table_list text;
BEGIN
  SELECT string_agg(format('%I.%I', schemaname, tablename), ', ' ORDER BY tablename)
    INTO table_list
    FROM pg_tables
   WHERE schemaname = 'public'
     AND tablename <> 'nexusport_schema_versions';

  IF table_list IS NOT NULL THEN
    EXECUTE 'TRUNCATE TABLE ' || table_list || ' RESTART IDENTITY CASCADE';
  END IF;
END $$;

INSERT INTO carriers (
  id,
  company_name,
  tax_code,
  phone,
  email,
  contact_person,
  status,
  created_at
)
VALUES (
  'c1010101-0000-0000-0000-000000000001',
  'Bien Dong Logistics - Nexus Logistics',
  '0301992811',
  '0909123889',
  'carrier01@nexusport.vn',
  'Vo Hang Tau',
  'active',
  now()
);

WITH default_users(username, email, role, full_name) AS (
  VALUES
    ('admin',        'admin@nexusport.vn',        'Administrator',     'Nguyễn Quản Trị'),
    ('dispatcher01', 'dispatcher01@nexusport.vn', 'Dispatcher',        'Trần Điều Phối'),
    ('gate01',       'gate01@nexusport.vn',       'Gate Officer',      'Lê Kiểm Cổng'),
    ('yard01',       'yard01@nexusport.vn',       'Yard Operator',     'Phạm Bãi Hàng'),
    ('berth01',      'berth01@nexusport.vn',      'Berth Staff',       'Hoàng Cầu Tàu'),
    ('carrier01',    'carrier01@nexusport.vn',    'Transport Company', 'Võ Hãng Tàu'),
    ('driver01',     'driver01@nexusport.vn',     'Driver',            'Đặng Tài Xế')
)
INSERT INTO users (
  id,
  username,
  email,
  password,
  role,
  full_name,
  is_active,
  created_at,
  updated_at
)
SELECT
  gen_random_uuid(),
  username,
  email,
  crypt('NexusPort@2026', gen_salt('bf', 12)),
  role,
  full_name,
  true,
  now(),
  now()
FROM default_users;

INSERT INTO carrier_users(carrier_id, user_id)
SELECT
  'c1010101-0000-0000-0000-000000000001',
  id
FROM users
WHERE username = 'carrier01';

COMMIT;
