BEGIN;

-- Hồ sơ hàng nhập theo số B/L. Tạo từ pre-alert của đầu xuất; Hãng tàu đối chiếu B/L với manifest,
-- gửi Arrival Notice cho Transport company, rồi thu phí, kiểm quyền nhận hàng và phát hành D/O.
CREATE TABLE IF NOT EXISTS import_shipments (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  bl_number varchar(100) NOT NULL UNIQUE,
  pre_alert_id uuid REFERENCES shipment_pre_alerts(id),
  line_user_id uuid NOT NULL,                       -- tài khoản Hãng tàu phụ trách (đại lý đầu nhập)
  consignee_company_id uuid,                        -- Transport company gắn với hồ sơ (phía consignee)
  status varchar(30) NOT NULL DEFAULT 'RECEIVED' CHECK (status IN
    ('RECEIVED', 'MISMATCH', 'NOTICE_SENT', 'AMENDMENT_REQUESTED', 'CONFIRMED', 'RELEASE_VERIFIED', 'DO_ISSUED')),
  version integer NOT NULL DEFAULT 1,
  -- Dữ liệu B/L theo pre-alert (đã sửa theo xác nhận của đầu xuất nếu có)
  vessel_name varchar(200),
  voyage_number varchar(100),
  port_of_loading varchar(200),
  port_of_discharge varchar(200),
  etd timestamptz,
  eta timestamptz,
  bl_type varchar(20),
  freight_terms varchar(20),
  shipper text,
  consignee text,
  notify_party text,
  cargo_description text,
  containers jsonb NOT NULL DEFAULT '[]'::jsonb,
  -- Đối chiếu với manifest
  reconciled_at timestamptz,
  mismatch jsonb,                                   -- [{field, blValue, manifestValue}] khi lệch
  mismatch_note text,
  -- Arrival Notice
  notice jsonb,
  notice_version integer NOT NULL DEFAULT 0,
  notice_deadline timestamptz,
  auto_accepted boolean NOT NULL DEFAULT false,
  invoice_info jsonb,
  contact_info jsonb,
  amendment_request jsonb,
  -- Quyền nhận hàng
  authorization_file_url text,
  release_check jsonb,
  do_number varchar(100),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_import_shipments_line ON import_shipments (line_user_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_import_shipments_company ON import_shipments (consignee_company_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_import_shipments_deadline ON import_shipments (notice_deadline) WHERE status = 'NOTICE_SENT';

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261119_053', 'Hồ sơ hàng nhập: đối chiếu B/L-manifest, Arrival Notice, D/O')
ON CONFLICT(version) DO NOTHING;

COMMIT;
