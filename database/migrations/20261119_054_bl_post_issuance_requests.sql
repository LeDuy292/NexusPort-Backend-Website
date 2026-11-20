BEGIN;

-- Yêu cầu ngoại lệ sau khi B/L đã phát hành: LOI nhận hàng không cần B/L gốc, đổi consignee, đổi cảng dỡ,
-- đổi lịch tàu (rớt tàu/đổi chuyến). Hãng tàu duyệt, ghi nhận phí và lịch sử vào shipment_document_events.
CREATE TABLE IF NOT EXISTS bl_post_issuance_requests (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  import_shipment_id uuid NOT NULL REFERENCES import_shipments(id),
  bl_number varchar(100) NOT NULL,
  request_type varchar(30) NOT NULL CHECK (request_type IN ('LOI_RELEASE', 'CHANGE_CONSIGNEE', 'CHANGE_POD', 'SCHEDULE_CHANGE')),
  status varchar(20) NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING', 'APPROVED', 'REJECTED', 'CANCELLED')),
  initiated_by varchar(20) NOT NULL CHECK (initiated_by IN ('COMPANY', 'LINE')),
  requested_by uuid,
  payload jsonb NOT NULL DEFAULT '{}'::jsonb,       -- giá trị mới, URL thư LOI/ủy quyền, lý do
  fee_vnd numeric(14, 2) NOT NULL DEFAULT 0,
  fee_paid boolean NOT NULL DEFAULT false,
  resolution_note text,
  resolved_by uuid,
  resolved_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

-- Mỗi hồ sơ chỉ có một yêu cầu đang chờ cho mỗi loại
CREATE UNIQUE INDEX IF NOT EXISTS ux_bl_post_issuance_pending
  ON bl_post_issuance_requests (import_shipment_id, request_type) WHERE status = 'PENDING';
CREATE INDEX IF NOT EXISTS ix_bl_post_issuance_bl ON bl_post_issuance_requests (bl_number);

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261119_054', 'Yêu cầu ngoại lệ sau phát hành B/L: LOI, đổi consignee, đổi cảng dỡ, đổi lịch tàu')
ON CONFLICT(version) DO NOTHING;

COMMIT;
