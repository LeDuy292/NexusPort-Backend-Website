BEGIN;

ALTER TABLE users ADD COLUMN IF NOT EXISTS shipping_line_id uuid REFERENCES shipping_lines(id);
CREATE INDEX IF NOT EXISTS ix_users_shipping_line ON users(shipping_line_id) WHERE shipping_line_id IS NOT NULL;

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261109_036', 'Restore the shipping line account link if removed by development schema sync')
ON CONFLICT (version) DO NOTHING;

COMMIT;
