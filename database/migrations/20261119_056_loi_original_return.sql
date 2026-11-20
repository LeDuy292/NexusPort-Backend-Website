BEGIN;

-- Nhận hàng bằng LOI thì phải nộp lại B/L gốc trong hạn. Hạn tính từ lúc Hãng tàu duyệt LOI;
-- hệ thống nhắc trước hạn và báo khi quá hạn mà chưa nhận được bản gốc.
ALTER TABLE bl_post_issuance_requests
  ADD COLUMN IF NOT EXISTS original_due_at timestamptz,
  ADD COLUMN IF NOT EXISTS original_returned_at timestamptz,
  ADD COLUMN IF NOT EXISTS reminder_sent_at timestamptz,
  ADD COLUMN IF NOT EXISTS overdue_notified_at timestamptz;

CREATE INDEX IF NOT EXISTS ix_bl_post_issuance_loi_due
  ON bl_post_issuance_requests (original_due_at)
  WHERE request_type = 'LOI_RELEASE' AND status = 'APPROVED' AND original_returned_at IS NULL;

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261119_056', 'Hạn nộp lại B/L gốc sau khi nhận hàng bằng LOI')
ON CONFLICT(version) DO NOTHING;

COMMIT;
