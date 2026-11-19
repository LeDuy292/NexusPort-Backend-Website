BEGIN;

-- Hạn xác nhận B/L draft, yêu cầu sửa từ shipper và phí sửa đổi (chiều xuất)
ALTER TABLE export_bookings
  ADD COLUMN IF NOT EXISTS draft_deadline timestamptz,
  ADD COLUMN IF NOT EXISTS amendment_request jsonb,
  ADD COLUMN IF NOT EXISTS amendment_fee_vnd numeric(14,0),
  ADD COLUMN IF NOT EXISTS amendment_fee_status varchar(20)
    CHECK (amendment_fee_status IS NULL OR amendment_fee_status IN ('Proposed', 'Approved', 'Waived'));

CREATE INDEX IF NOT EXISTS ix_export_bookings_draft_deadline
  ON export_bookings (draft_deadline) WHERE si_status = 'Draft';

-- Pre-alert: bộ thông tin đầu xuất gửi đầu nhập sau khi phát hành B/L. Đầu nhập tạo hồ sơ từ đây.
CREATE TABLE IF NOT EXISTS shipment_pre_alerts (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  export_booking_id uuid NOT NULL,
  booking_no varchar(100) NOT NULL,
  bl_number varchar(100) NOT NULL,
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
  containers jsonb NOT NULL DEFAULT '[]'::jsonb,
  payload jsonb NOT NULL,
  status varchar(20) NOT NULL DEFAULT 'SENT' CHECK (status IN ('SENT', 'RECEIVED')),
  sent_at timestamptz NOT NULL DEFAULT now(),
  received_at timestamptz,
  UNIQUE (bl_number)
);

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261119_052', 'Hạn xác nhận draft, yêu cầu sửa, phí sửa đổi và pre-alert (chiều xuất)')
ON CONFLICT(version) DO NOTHING;

COMMIT;
