BEGIN;

-- Chuyến tàu, bản khai (manifest), lịch sử cut-off và SI của phân hệ Hãng tàu trước đây nằm trong bộ nhớ
-- và mất khi khởi động lại. Mỗi dòng là một tài liệu; dữ liệu mẫu ban đầu vẫn nằm trong code và chỉ được
-- ghi vào đây khi bị sửa.
CREATE TABLE IF NOT EXISTS shipping_line_documents (
  seq bigserial NOT NULL,
  kind varchar(20) NOT NULL CHECK (kind IN ('VOYAGE', 'MANIFEST', 'CUTOFF', 'SI', 'PORT_REC')),
  doc_key varchar(80) NOT NULL,
  data jsonb NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (kind, doc_key)
);
CREATE INDEX IF NOT EXISTS ix_shipping_line_documents_seq ON shipping_line_documents (seq);

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261119_055', 'Lưu chuyến tàu, manifest, cut-off và SI của Hãng tàu trong DB')
ON CONFLICT(version) DO NOTHING;

COMMIT;
