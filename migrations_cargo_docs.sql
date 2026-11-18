-- Migration: Create tables delivery_orders and bills_of_lading
-- Date: 2026-11-17

-- 1. Table: delivery_orders (e-D/O cho luồng nhập khẩu)
CREATE TABLE IF NOT EXISTS delivery_orders (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    do_number VARCHAR(80) NOT NULL UNIQUE,
    bl_number VARCHAR(100) NOT NULL,
    carrier_id UUID REFERENCES carriers(id) ON DELETE SET NULL,
    consignee_name VARCHAR(255) NOT NULL,
    consignee_tax_code VARCHAR(50),
    vessel_name VARCHAR(150),
    voyage_no VARCHAR(50),
    valid_until TIMESTAMPTZ,
    issued_date TIMESTAMPTZ DEFAULT now(),
    empty_return_depot VARCHAR(255),
    containers JSONB DEFAULT '[]'::jsonb,
    status VARCHAR(50) DEFAULT 'RELEASED', -- 'RELEASED', 'HELD', 'CANCELLED', 'EXPIRED'
    hold_reason TEXT,
    qr_data TEXT,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_edo_do_number ON delivery_orders(do_number);
CREATE INDEX IF NOT EXISTS idx_edo_bl_number ON delivery_orders(bl_number);
CREATE INDEX IF NOT EXISTS idx_edo_carrier_id ON delivery_orders(carrier_id);
CREATE INDEX IF NOT EXISTS idx_edo_status ON delivery_orders(status);

-- 2. Table: bills_of_lading (Vận đơn B/L cho xuất & nhập khẩu)
CREATE TABLE IF NOT EXISTS bills_of_lading (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    bl_number VARCHAR(100) NOT NULL UNIQUE,
    booking_no VARCHAR(80),
    carrier_id UUID REFERENCES carriers(id) ON DELETE SET NULL,
    direction VARCHAR(20) DEFAULT 'export', -- 'export' | 'import'
    shipper_name VARCHAR(255),
    shipper_tax_code VARCHAR(50),
    consignee_name VARCHAR(255),
    consignee_tax_code VARCHAR(50),
    notify_party VARCHAR(255),
    vessel_name VARCHAR(150),
    voyage_number VARCHAR(50),
    pol VARCHAR(100) DEFAULT 'Da Nang Port (VNDAD)',
    pod VARCHAR(100) DEFAULT 'Singapore (SGSIN)',
    shipped_on_board_date TIMESTAMPTZ,
    confirmation_deadline TIMESTAMPTZ,
    freight_status VARCHAR(50) DEFAULT 'FREIGHT PREPAID', -- 'FREIGHT PREPAID' | 'FREIGHT COLLECT'
    status VARCHAR(50) DEFAULT 'ISSUED', -- 'DRAFT', 'CONFIRMED', 'ISSUED', 'SURRENDERED'
    containers JSONB DEFAULT '[]'::jsonb,
    cargo_description TEXT,
    draft_version INT DEFAULT 1,
    qr_data TEXT,
    created_at TIMESTAMPTZ DEFAULT now(),
    updated_at TIMESTAMPTZ DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_bl_bl_number ON bills_of_lading(bl_number);
CREATE INDEX IF NOT EXISTS idx_bl_booking_no ON bills_of_lading(booking_no);
CREATE INDEX IF NOT EXISTS idx_bl_carrier_id ON bills_of_lading(carrier_id);
CREATE INDEX IF NOT EXISTS idx_bl_status ON bills_of_lading(status);

-- 3. Đảm bảo user carrier01 được liên kết với carrier trong carrier_users
DO $$
DECLARE
    v_carrier_id UUID;
    v_user_id UUID;
BEGIN
    SELECT id INTO v_carrier_id FROM carriers LIMIT 1;
    SELECT id INTO v_user_id FROM users WHERE username = 'carrier01' LIMIT 1;

    IF v_carrier_id IS NOT NULL AND v_user_id IS NOT NULL THEN
        INSERT INTO carrier_users (carrier_id, user_id)
        VALUES (v_carrier_id, v_user_id)
        ON CONFLICT DO NOTHING;
    END IF;
END $$;

-- 4. Seed dữ liệu mẫu thực tế cho bills_of_lading
INSERT INTO bills_of_lading (
    bl_number, booking_no, carrier_id, direction, shipper_name, shipper_tax_code,
    consignee_name, consignee_tax_code, notify_party, vessel_name, voyage_number,
    pol, pod, shipped_on_board_date, confirmation_deadline, freight_status, status,
    containers, cargo_description, draft_version, qr_data
)
SELECT 
    'ONE-DAD-260101', 'BKG-TEST-1001', c.id, 'export', 'Công ty Cổ phần Thép Hòa Phát', '0401889988',
    'GLOBAL METALS TRADING PTE LTD', '9988776655', 'SAME AS CONSIGNEE', 'EVER GLORY', '2601N',
    'Cảng Tiên Sa - Đà Nẵng (VNDAD)', 'Singapore Port (SGSIN)', now() - interval '1 day',
    now() + interval '12 hours', 'FREIGHT PREPAID', 'ISSUED',
    '[{"containerNo": "MSKU9082134", "type": "40HC", "sealNo": "ONE889912", "tareWeight": 3800, "grossWeight": 24500, "vgm": 24500}, {"containerNo": "TCKU4829102", "type": "40HC", "sealNo": "ONE889913", "tareWeight": 3820, "grossWeight": 25100, "vgm": 25100}]'::jsonb,
    'Hot Rolled Steel Coils in Containers (Thép cuộn cán nóng đóng container)', 1,
    'NXP-BL|ONE-DAD-260101|EVER GLORY|2026-11-16'
FROM carriers c LIMIT 1
ON CONFLICT (bl_number) DO NOTHING;

INSERT INTO bills_of_lading (
    bl_number, booking_no, carrier_id, direction, shipper_name, shipper_tax_code,
    consignee_name, consignee_tax_code, notify_party, vessel_name, voyage_number,
    pol, pod, shipped_on_board_date, confirmation_deadline, freight_status, status,
    containers, cargo_description, draft_version, qr_data
)
SELECT 
    'CMA-DAD-260102', 'BKG-TEST-1002', c.id, 'export', 'Công ty Cổ phần May Nhà Bè', '0301992233',
    'FASHION RETAIL CORP USA', 'US-88776655', 'LOGISTICS PARTNER CORP', 'CMA CGM CHENNAI', '2604S',
    'Cảng Tiên Sa - Đà Nẵng (VNDAD)', 'Los Angeles (USLAX)', null,
    now() + interval '18 hours', 'FREIGHT PREPAID', 'DRAFT',
    '[{"containerNo": "CMAU7788991", "type": "40HC", "sealNo": "CMA99812", "tareWeight": 3750, "grossWeight": 18200, "vgm": 18200}]'::jsonb,
    'Garments and Apparel Cargo (Hàng dệt may xuất khẩu)', 1,
    'NXP-BL-DRAFT|CMA-DAD-260102|CMA CGM CHENNAI'
FROM carriers c LIMIT 1
ON CONFLICT (bl_number) DO NOTHING;

-- 5. Seed dữ liệu mẫu thực tế cho delivery_orders
INSERT INTO delivery_orders (
    do_number, bl_number, carrier_id, consignee_name, consignee_tax_code,
    vessel_name, voyage_no, valid_until, issued_date, empty_return_depot,
    containers, status, hold_reason, qr_data
)
SELECT 
    'EDO-MSC-2026-001', 'MSC-IMP-99881', c.id, 'Công ty TNHH Nhôm Định Hình Nam Hải', '0402123456',
    'MSC RENEE', '2603W', now() + interval '3 days', now() - interval '1 day',
    'Depot Tân Cảng Đà Nẵng',
    '[{"containerNo": "MEDU9928172", "type": "40HC", "sealNo": "MSC77661", "weight": 26800, "status": "YARD_DISCHARGED"}]'::jsonb,
    'RELEASED', null,
    'NXP-EDO|EDO-MSC-2026-001|MSC-IMP-99881|VALID_3D'
FROM carriers c LIMIT 1
ON CONFLICT (do_number) DO NOTHING;

INSERT INTO delivery_orders (
    do_number, bl_number, carrier_id, consignee_name, consignee_tax_code,
    vessel_name, voyage_no, valid_until, issued_date, empty_return_depot,
    containers, status, hold_reason, qr_data
)
SELECT 
    'EDO-COSCO-2026-002', 'COS-IMP-55662', c.id, 'Công ty Cổ phần Thép Hòa Phát', '0401889988',
    'COSCO SHIPPING PLAZA', '2602E', now() + interval '5 days', now() - interval '2 hours',
    'Depot Cảng Tiên Sa Bãi 3',
    '[{"containerNo": "COSU8877661", "type": "20DC", "sealNo": "COS44332", "weight": 21500, "status": "YARD_DISCHARGED"}]'::jsonb,
    'HELD', 'Chờ xác nhận thanh toán phụ phí DEM/DET từ Hãng tàu COSCO',
    'NXP-EDO|EDO-COSCO-2026-002|COS-IMP-55662|HELD'
FROM carriers c LIMIT 1
ON CONFLICT (do_number) DO NOTHING;
