BEGIN;

CREATE TABLE IF NOT EXISTS eport_services (
 id uuid PRIMARY KEY,
 code varchar(40) NOT NULL UNIQUE,
 name varchar(100) NOT NULL,
 description varchar(600) NOT NULL,
 direction varchar(6) NOT NULL CHECK(direction IN ('EXPORT','IMPORT','BOTH')),
 unit varchar(10) NOT NULL CHECK(unit IN ('CONTAINER','MOVE','HOUR','DAY','WEIGH')),
 icon varchar(20) NOT NULL,
 conditions jsonb NOT NULL CHECK(jsonb_typeof(conditions)='array' AND jsonb_array_length(conditions) BETWEEN 1 AND 12),
 is_active boolean NOT NULL DEFAULT true,
 requires_approval boolean NOT NULL DEFAULT false,
 version integer NOT NULL DEFAULT 1,
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS eport_tariffs (
 id uuid PRIMARY KEY,
 service_id uuid NOT NULL REFERENCES eport_services(id),
 size varchar(4) NOT NULL CHECK(size IN ('ALL','20FT','40FT','45FT')),
 equipment varchar(6) NOT NULL CHECK(equipment IN ('ALL','DRY','REEFER','DG','OOG')),
 amount numeric(14,0) NOT NULL CHECK(amount BETWEEN 0 AND 999999999999),
 currency char(3) NOT NULL DEFAULT 'VND' CHECK(currency='VND'),
 unit varchar(10) NOT NULL,
 effective_from date NOT NULL,
 effective_to date,
 is_active boolean NOT NULL DEFAULT true,
 note varchar(500) NOT NULL DEFAULT '',
 created_by uuid NOT NULL,
 retired_by uuid,
 created_at timestamptz NOT NULL DEFAULT now(),
 retired_at timestamptz,
 CHECK(effective_to IS NULL OR effective_to>=effective_from)
);
CREATE INDEX IF NOT EXISTS ix_eport_tariff_service ON eport_tariffs(service_id,effective_from) WHERE is_active;
CREATE TABLE IF NOT EXISTS eport_catalog_audit (
 id uuid PRIMARY KEY,
 entity_id uuid NOT NULL,
 actor_id uuid NOT NULL,
 action varchar(40) NOT NULL,
 before_data jsonb,
 after_data jsonb NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now()
);

-- Reference service definitions only. No prices, customer records or orders are seeded.
INSERT INTO eport_services(id,code,name,description,direction,unit,icon,conditions,is_active,requires_approval) VALUES
('03500000-0000-4000-8000-000000000001','EXPORT_DROPOFF','Hạ container xuất','Đăng ký tiếp nhận và hạ container hàng xuất vào bãi để chuẩn bị xếp tàu.','EXPORT','CONTAINER','container','["Booking hãng tàu đã được duyệt","Container thuộc danh sách được công bố","VGM hợp lệ và còn trong hạn CY cut-off","Container và Booking không bị giữ hàng"]',true,false),
('03500000-0000-4000-8000-000000000002','IMPORT_PICKUP','Lấy container nhập','Yêu cầu giao container hàng nhập từ bãi cảng cho đơn vị được quyền nhận hàng.','IMPORT','CONTAINER','truck','["Có e-D/O đúng container và còn hiệu lực","Container đã dỡ và đang trong bãi","Bên đặt có quyền nhận hoặc được ủy quyền","Container không bị giữ hàng"]',true,false),
('03500000-0000-4000-8000-000000000003','REEFER_POWER','Cắm điện container lạnh','Yêu cầu cấp điện cho container lạnh đang lưu tại bãi cảng.','BOTH','HOUR','plug','["Container thuộc loại lạnh","Container đang trong bãi","Nhân viên bãi xác nhận tác nghiệp và thời gian cấp điện"]',true,false),
('03500000-0000-4000-8000-000000000004','YARD_RELOCATION','Đảo chuyển container','Yêu cầu di chuyển container giữa các vị trí lưu bãi theo kế hoạch điều độ.','BOTH','MOVE','move','["Container đang trong bãi","Container không bị giữ hàng","Vị trí và thiết bị thực hiện do điều độ bố trí"]',true,false),
('03500000-0000-4000-8000-000000000005','STORAGE_EXTENSION','Gia hạn lưu bãi','Gửi yêu cầu kéo dài thời gian lưu bãi để điều độ xem xét và phê duyệt.','BOTH','DAY','clock','["Container đang lưu bãi và chưa ra cổng","Yêu cầu được điều độ chấp thuận","Gia hạn lưu bãi không tự gia hạn e-D/O hoặc CY cut-off"]',true,true),
('03500000-0000-4000-8000-000000000006','CONTAINER_WEIGH','Cân container','Yêu cầu cân tại địa điểm được phép và nhận kết quả từ bộ phận thực hiện.','BOTH','WEIGH','scale','["Container tại cổng hoặc trong bãi theo quy trình được phép","Địa điểm và quy trình cân phải được cảng xác nhận"]',false,false)
ON CONFLICT(code) DO NOTHING;

INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261108_035','e-Port service catalog, dated VND tariffs and append-only catalog audit') ON CONFLICT(version) DO NOTHING;
COMMIT;
