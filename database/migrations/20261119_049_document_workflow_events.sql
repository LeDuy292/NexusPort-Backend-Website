BEGIN;

-- Lịch sử bất biến của hồ sơ chứng từ hãng tàu (hàng xuất theo booking, hàng nhập theo B/L).
-- Mỗi lần gửi thông tin, xác nhận hoặc yêu cầu sửa là một dòng: ai làm, lúc nào, kênh nào,
-- trường nào đổi, giá trị cũ và mới. Không UPDATE/DELETE, chỉ INSERT.
CREATE TABLE IF NOT EXISTS shipment_document_events (
  id bigserial PRIMARY KEY,
  direction varchar(10) NOT NULL CHECK (direction IN ('EXPORT', 'IMPORT')),
  file_ref varchar(100) NOT NULL,            -- số booking (xuất) hoặc số B/L (nhập)
  document_type varchar(30) NOT NULL,        -- BOOKING, SI, VGM, DRAFT_BL, BL, PRE_ALERT, ARRIVAL_NOTICE, DO, PAYMENT...
  action varchar(40) NOT NULL,               -- SUBMITTED, SENT, CONFIRMED, REVISION_REQUESTED, ISSUED, AUTO_ACCEPTED...
  from_status varchar(40),
  to_status varchar(40),
  actor_user_id uuid,
  actor_username varchar(100),
  actor_role varchar(40),
  channel varchar(20) NOT NULL DEFAULT 'PORTAL' CHECK (channel IN ('PORTAL', 'EMAIL', 'SYSTEM')),
  field_changes jsonb NOT NULL DEFAULT '[]'::jsonb,   -- [{ "field": "...", "old": ..., "new": ... }]
  document_version integer,                  -- phiên bản SI/draft/Arrival Notice tại thời điểm đó
  snapshot jsonb,                            -- bản sao nội dung chứng từ đã gửi
  note text,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_sde_file ON shipment_document_events(direction, file_ref, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_sde_actor ON shipment_document_events(actor_user_id, created_at DESC);

CREATE OR REPLACE FUNCTION shipment_document_events_immutable() RETURNS trigger AS $$
BEGIN
  RAISE EXCEPTION 'shipment_document_events là bảng chỉ ghi thêm, không được sửa hoặc xóa';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_sde_immutable ON shipment_document_events;
CREATE TRIGGER trg_sde_immutable
  BEFORE UPDATE OR DELETE ON shipment_document_events
  FOR EACH ROW EXECUTE FUNCTION shipment_document_events_immutable();

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261119_049', 'Append-only lịch sử hồ sơ chứng từ hãng tàu (xuất/nhập)')
ON CONFLICT(version) DO NOTHING;

COMMIT;
