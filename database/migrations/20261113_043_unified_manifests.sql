BEGIN;
CREATE TABLE IF NOT EXISTS shipping_manifests (
  id uuid PRIMARY KEY,
  owner_id uuid NOT NULL REFERENCES users(id),
  request_key uuid NOT NULL,
  payload jsonb NOT NULL,
  revision integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(owner_id, request_key)
);
CREATE TABLE IF NOT EXISTS shipping_manifest_bills (
  manifest_id uuid NOT NULL REFERENCES shipping_manifests(id) ON DELETE CASCADE,
  owner_id uuid NOT NULL REFERENCES users(id),
  bl_number varchar(100) NOT NULL,
  PRIMARY KEY(owner_id, bl_number)
);
CREATE INDEX IF NOT EXISTS ix_shipping_manifests_owner ON shipping_manifests(owner_id, created_at DESC);
INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261113_043', 'Atomic account-scoped Manifest, Bill of Lading and cargo snapshots')
ON CONFLICT(version) DO NOTHING;
COMMIT;
