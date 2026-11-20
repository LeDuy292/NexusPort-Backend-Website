BEGIN;

-- Mỗi lần Hãng tàu nhập danh sách vận đơn từ file là một lô: ai nhập, file nào, bao nhiêu dòng đạt hoặc lỗi.
CREATE TABLE IF NOT EXISTS bl_import_batches (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  created_by uuid NOT NULL,
  created_by_name varchar(100),
  bl_file_name varchar(255),
  container_file_name varchar(255),
  bl_file_hash varchar(64) NOT NULL,
  total_rows integer NOT NULL DEFAULT 0,
  created_count integer NOT NULL DEFAULT 0,
  updated_count integer NOT NULL DEFAULT 0,
  skipped_count integer NOT NULL DEFAULT 0,
  result jsonb NOT NULL DEFAULT '[]'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_bl_import_batches_created ON bl_import_batches (created_at DESC);

INSERT INTO nexusport_schema_versions(version, description)
SELECT '20261119_058', 'Lô nhập danh sách vận đơn từ file của Hãng tàu'
WHERE NOT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '20261119_058');

COMMIT;
