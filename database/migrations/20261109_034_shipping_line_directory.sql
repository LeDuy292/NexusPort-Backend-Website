BEGIN;

CREATE TABLE IF NOT EXISTS shipping_lines (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  full_name varchar(250) NOT NULL CHECK (btrim(full_name) <> ''),
  short_name varchar(80) NOT NULL CHECK (btrim(short_name) <> '')
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_shipping_lines_short_name ON shipping_lines (lower(short_name));

CREATE TABLE IF NOT EXISTS shipping_line_prefixes (
  prefix varchar(4) PRIMARY KEY CHECK (prefix ~ '^[A-Z]{3}[UJZ]$'),
  shipping_line_id uuid NOT NULL REFERENCES shipping_lines(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_shipping_line_prefixes_line ON shipping_line_prefixes(shipping_line_id);

ALTER TABLE users ADD COLUMN IF NOT EXISTS shipping_line_id uuid REFERENCES shipping_lines(id);
CREATE INDEX IF NOT EXISTS ix_users_shipping_line ON users(shipping_line_id) WHERE shipping_line_id IS NOT NULL;

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261109_034', 'Shipping line directory, unique BIC prefixes and account links')
ON CONFLICT (version) DO NOTHING;

COMMIT;
