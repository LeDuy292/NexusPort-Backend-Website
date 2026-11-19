BEGIN;

-- Chuyển B/L và e-D/O của phân hệ Hãng tàu từ bộ nhớ sang DB: bổ sung các cột mà
-- bản trong bộ nhớ có nhưng bảng chưa có. Chỉ thêm cột, không đổi hay xóa dữ liệu cũ.
ALTER TABLE bills_of_lading
  ADD COLUMN IF NOT EXISTS bl_type varchar(20) NOT NULL DEFAULT 'ORIGINAL',
  ADD COLUMN IF NOT EXISTS package_unit varchar(30),
  ADD COLUMN IF NOT EXISTS measurement_cbm numeric(12,3),
  ADD COLUMN IF NOT EXISTS freight_amount_usd numeric(14,2),
  ADD COLUMN IF NOT EXISTS freight_paid boolean NOT NULL DEFAULT false,
  ADD COLUMN IF NOT EXISTS freight_payment_status varchar(20),
  ADD COLUMN IF NOT EXISTS local_charges_status varchar(20) NOT NULL DEFAULT 'UNPAID',
  ADD COLUMN IF NOT EXISTS source_type varchar(30),
  ADD COLUMN IF NOT EXISTS source_reference varchar(100),
  ADD COLUMN IF NOT EXISTS issue_date timestamptz,
  ADD COLUMN IF NOT EXISTS attached_files jsonb NOT NULL DEFAULT '[]'::jsonb,
  ADD COLUMN IF NOT EXISTS consignee_hold boolean NOT NULL DEFAULT false,
  ADD COLUMN IF NOT EXISTS consignee_hold_reason text,
  ADD COLUMN IF NOT EXISTS is_customs_hold boolean NOT NULL DEFAULT false,
  ADD COLUMN IF NOT EXISTS is_carrier_hold boolean NOT NULL DEFAULT false,
  ADD COLUMN IF NOT EXISTS in_manifest boolean NOT NULL DEFAULT true;

ALTER TABLE delivery_orders
  ADD COLUMN IF NOT EXISTS free_time_days integer,
  ADD COLUMN IF NOT EXISTS pin_code varchar(30),
  ADD COLUMN IF NOT EXISTS issued_by varchar(100),
  ADD COLUMN IF NOT EXISTS release_officer varchar(150),
  ADD COLUMN IF NOT EXISTS notes text,
  ADD COLUMN IF NOT EXISTS cancelled_at timestamptz,
  ADD COLUMN IF NOT EXISTS cancel_reason text,
  ADD COLUMN IF NOT EXISTS cancelled_by varchar(100);

CREATE UNIQUE INDEX IF NOT EXISTS uq_bills_of_lading_number_active
  ON bills_of_lading (upper(btrim(bl_number))) WHERE status <> 'CANCELLED' AND bl_number IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS uq_delivery_orders_number ON delivery_orders (do_number);
-- Ghi chú: DB đang có dữ liệu thử với nhiều e-D/O còn hiệu lực trên cùng một B/L, nên quy tắc
-- "một B/L một e-D/O còn hiệu lực" được kiểm bằng code (khóa dòng B/L khi phát hành), chưa ép bằng index.

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261119_050', 'B/L và e-D/O của Hãng tàu lưu trong DB thay vì bộ nhớ')
ON CONFLICT(version) DO NOTHING;

COMMIT;
