BEGIN;

-- 1) Bỏ các bảng rỗng không còn code nào dùng (DB mới tạo từ các migration cũ sẽ thừa chúng).
DROP TABLE IF EXISTS charge_items, lift_operations, service_prices, surcharges,
  berth_assignments, berth_performance_snapshots, incidents, operation_plans,
  equipment_tasks, optimization_suggestions, role_permissions, booking_fleet_capacities,
  bl_import_batches, booking_storage_notice_deliveries, container_damage_reports, gate_media,
  np_sl_automation_audit_logs, np_sl_bills_of_lading, np_sl_container_eor_repairs,
  np_sl_export_bookings, np_sl_vessel_voyages, np_sl_edo_orders, np_sl_shipping_containers,
  ocr_results, oversized_details, shipping_line_configs, staff_profiles, system_alerts,
  vessel_voyages, yard_traffic_snapshots, "Containers";

-- 2) shipping_line_id kiểu chữ (chứa giá trị giả như 'SL-NEXUS-01', không có khóa ngoại) -> uuid trỏ shipping_lines.
-- Cột cũ có DEFAULT 'SL-NEXUS-01' (và NOT NULL ở export_bookings); code không ghi cột này nên bỏ cả hai.
ALTER TABLE bills_of_lading ALTER COLUMN shipping_line_id DROP DEFAULT;
ALTER TABLE export_bookings ALTER COLUMN shipping_line_id DROP DEFAULT, ALTER COLUMN shipping_line_id DROP NOT NULL;
ALTER TABLE edo_orders      ALTER COLUMN shipping_line_id DROP DEFAULT;

UPDATE bills_of_lading SET shipping_line_id = NULL
 WHERE shipping_line_id IS NOT NULL
   AND NOT (shipping_line_id ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
            AND EXISTS (SELECT 1 FROM shipping_lines l WHERE l.id::text = bills_of_lading.shipping_line_id));
UPDATE export_bookings SET shipping_line_id = NULL
 WHERE shipping_line_id IS NOT NULL
   AND NOT (shipping_line_id ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
            AND EXISTS (SELECT 1 FROM shipping_lines l WHERE l.id::text = export_bookings.shipping_line_id));
UPDATE edo_orders SET shipping_line_id = NULL
 WHERE shipping_line_id IS NOT NULL
   AND NOT (shipping_line_id ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
            AND EXISTS (SELECT 1 FROM shipping_lines l WHERE l.id::text = edo_orders.shipping_line_id));

ALTER TABLE bills_of_lading ALTER COLUMN shipping_line_id TYPE uuid USING shipping_line_id::uuid;
ALTER TABLE export_bookings ALTER COLUMN shipping_line_id TYPE uuid USING shipping_line_id::uuid;
ALTER TABLE edo_orders      ALTER COLUMN shipping_line_id TYPE uuid USING shipping_line_id::uuid;

-- 3) Chuyến tàu / hồ sơ nhập chỉ gắn với tài khoản (line_user_id): thêm hãng tàu sở hữu, lấy từ shipping_line_accounts.
ALTER TABLE export_voyages   ADD COLUMN IF NOT EXISTS shipping_line_id uuid;
ALTER TABLE import_shipments ADD COLUMN IF NOT EXISTS shipping_line_id uuid;
UPDATE export_bookings SET shipping_line_id = a.shipping_line_id
  FROM shipping_line_accounts a WHERE a.user_id = export_bookings.line_user_id AND export_bookings.shipping_line_id IS NULL;
UPDATE export_voyages SET shipping_line_id = a.shipping_line_id
  FROM shipping_line_accounts a WHERE a.user_id = export_voyages.line_user_id AND export_voyages.shipping_line_id IS NULL;
UPDATE import_shipments SET shipping_line_id = a.shipping_line_id
  FROM shipping_line_accounts a WHERE a.user_id = import_shipments.line_user_id AND import_shipments.shipping_line_id IS NULL;

-- 4) Khóa ngoại + chỉ mục.
DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['bills_of_lading','export_bookings','edo_orders','export_voyages','import_shipments'] LOOP
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_' || t || '_shipping_line') THEN
      EXECUTE format('ALTER TABLE %I ADD CONSTRAINT %I FOREIGN KEY (shipping_line_id) REFERENCES shipping_lines(id)', t, 'fk_' || t || '_shipping_line');
    END IF;
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I (shipping_line_id)', 'ix_' || t || '_shipping_line', t);
  END LOOP;
END $$;

-- 5) Mỗi tài khoản hãng tàu chỉ thuộc một hãng; mỗi tiền tố container chỉ thuộc một hãng.
CREATE UNIQUE INDEX IF NOT EXISTS ux_shipping_line_accounts_user ON shipping_line_accounts (user_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_shipping_line_prefixes_prefix ON shipping_line_prefixes (prefix);

INSERT INTO nexusport_schema_versions(version, description)
SELECT '20261120_059', 'Chuẩn hóa tham chiếu hãng tàu (uuid + FK), bỏ bảng rỗng không dùng'
WHERE NOT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '20261120_059');

COMMIT;
