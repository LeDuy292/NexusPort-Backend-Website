BEGIN;

-- Cho phép lưu tàu do Hãng tàu tự thêm vào danh mục đội tàu
ALTER TABLE shipping_line_documents DROP CONSTRAINT IF EXISTS shipping_line_documents_kind_check;
ALTER TABLE shipping_line_documents ADD CONSTRAINT shipping_line_documents_kind_check
  CHECK (kind IN ('VOYAGE', 'MANIFEST', 'CUTOFF', 'SI', 'PORT_REC', 'VESSEL'));

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261119_057', 'Thêm tàu vào danh mục đội tàu của Hãng tàu')
ON CONFLICT(version) DO NOTHING;

COMMIT;
