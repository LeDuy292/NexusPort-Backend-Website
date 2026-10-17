BEGIN;
CREATE TABLE IF NOT EXISTS container_cargo_types (
  code cargo_type PRIMARY KEY, name_vi varchar(100) NOT NULL,
  requires_power boolean NOT NULL DEFAULT false, sort_order integer NOT NULL
);
INSERT INTO container_cargo_types(code,name_vi,requires_power,sort_order) VALUES
 ('general','Hàng thông thường',false,1), ('reefer','Hàng đông lạnh',true,2),
 ('dangerous','Hàng nguy hiểm',false,3), ('perishable','Hàng dễ hư hỏng',false,4),
 ('oversized','Hàng quá khổ',false,5), ('overweight','Hàng quá tải',false,6)
ON CONFLICT(code) DO UPDATE SET name_vi=EXCLUDED.name_vi, requires_power=EXCLUDED.requires_power, sort_order=EXCLUDED.sort_order;
-- Reference catalog only. Preserve IDs, weights and operational foreign keys.
INSERT INTO container_types(code,size,category,description) VALUES
 ('20GP','ft20','dry','Container khô 20 feet'), ('40GP','ft40','dry','Container khô 40 feet'),
 ('40HC','ft40','dry','Container khô cao 40 feet'), ('45HC','ft45','dry','Container khô cao 45 feet'),
 ('20RF','ft20','reefer','Container lạnh 20 feet'), ('40RF','ft40','reefer','Container lạnh 40 feet'),
 ('40RH','ft40','reefer','Container lạnh cao 40 feet'),
 ('20OT','ft20','open_top','Container mở nóc 20 feet'), ('40OT','ft40','open_top','Container mở nóc 40 feet'),
 ('20FR','ft20','flat_rack','Container sàn 20 feet'), ('40FR','ft40','flat_rack','Container sàn 40 feet'),
 ('20TK','ft20','tank','Container bồn 20 feet')
ON CONFLICT(code) DO UPDATE SET size=EXCLUDED.size,category=EXCLUDED.category,description=EXCLUDED.description;
ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS required_power_kw numeric(10,2);
ALTER TABLE containers ADD COLUMN IF NOT EXISTS required_power_kw numeric(10,2);
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='ck_visit_required_power') THEN
  ALTER TABLE container_visits ADD CONSTRAINT ck_visit_required_power CHECK(required_power_kw IS NULL OR (cargo_type='reefer' AND required_power_kw>0));
 END IF;
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conname='ck_container_required_power') THEN
  ALTER TABLE containers ADD CONSTRAINT ck_container_required_power CHECK(required_power_kw IS NULL OR (cargo_type='reefer' AND required_power_kw>0));
 END IF;
END $$;
COMMENT ON COLUMN container_visits.required_power_kw IS 'Công suất điện yêu cầu (kW) cho hàng đông lạnh; NULL nếu chưa khai báo';
COMMENT ON COLUMN container_visits.movement_type IS 'Legacy operational history. New declarations leave NULL; direction belongs to bookings.booking_type.';
COMMENT ON COLUMN container_visits.requested_service_date IS 'Legacy declaration date. New appointment times belong to bookings.';
COMMENT ON COLUMN container_visits.source_reference IS 'Legacy reference; no longer accepted by carrier declaration API.';
COMMENT ON COLUMN container_visits.seal_number IS 'Operational/legacy seal; no longer collected by declaration API.';
COMMENT ON COLUMN container_visits.bl_booking_no IS 'Operational/legacy B/L; no longer collected by declaration API.';
INSERT INTO nexusport_schema_versions(version,description) VALUES('20261017_007','Carrier-only yard declarations, Vietnamese catalogs and reefer power in kW') ON CONFLICT(version) DO NOTHING;
COMMIT;
