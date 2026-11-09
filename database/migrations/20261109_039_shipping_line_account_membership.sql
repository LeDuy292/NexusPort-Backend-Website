BEGIN;

CREATE TABLE IF NOT EXISTS shipping_line_accounts (
  user_id uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
  shipping_line_id uuid NOT NULL REFERENCES shipping_lines(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_shipping_line_accounts_line ON shipping_line_accounts(shipping_line_id);

DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name = 'users' AND column_name = 'shipping_line_id') THEN
    INSERT INTO shipping_line_accounts(user_id, shipping_line_id)
    SELECT id, shipping_line_id FROM users
    WHERE role = 'Shipping Line' AND shipping_line_id IS NOT NULL
    ON CONFLICT (user_id) DO NOTHING;
  END IF;
END $$;

-- Recover the known import links on databases where schema sync dropped the old column.
WITH imported(username, short_name, full_name) AS (VALUES
  ('ship-hapag', 'Hapag-Lloyd', 'Hapag-Lloyd AG'),
  ('ship-one', 'ONE', 'Ocean Network Express Pte. Ltd.'),
  ('ship-evergreen', 'Evergreen', 'Evergreen Line'),
  ('ship-yangming', 'Yang Ming', 'Yang Ming Marine Transport Corp.'),
  ('ship-msc', 'MSC', 'MSC Mediterranean Shipping Company S.A.')
)
INSERT INTO shipping_line_accounts(user_id, shipping_line_id)
SELECT u.id, l.id
FROM imported i
JOIN users u ON u.username = i.username AND u.email = i.username || '@nexusport.invalid'
JOIN shipping_lines l ON l.short_name = i.short_name AND l.full_name = i.full_name
WHERE u.role = 'Shipping Line'
ON CONFLICT (user_id) DO NOTHING;

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261109_039', 'Store shipping line account memberships outside the shared users schema')
ON CONFLICT (version) DO NOTHING;

COMMIT;
