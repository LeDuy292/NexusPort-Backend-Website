BEGIN;

-- ============================================================
-- Migration: 20261012_006 — Transport Trip Yard Navigation
-- Feature:   NXP-138 Di chuyển đến bãi (Driver Yard Navigation)
-- Author:    NexusPort Team
-- Date:      2026-10-12
-- ============================================================

-- Bổ sung các cột thông tin tên bãi, địa chỉ và tọa độ GPS để hỗ trợ điều hướng
ALTER TABLE transport_trips
  ADD COLUMN IF NOT EXISTS yard_name VARCHAR(150) DEFAULT 'Bãi Container Cảng Tiên Sa',
  ADD COLUMN IF NOT EXISTS yard_address VARCHAR(255) DEFAULT 'Số 01 Yết Kiêu, Phường Thọ Quang, Quận Sơn Trà, TP. Đà Nẵng',
  ADD COLUMN IF NOT EXISTS yard_lat DECIMAL(10, 7) DEFAULT 16.1265000,
  ADD COLUMN IF NOT EXISTS yard_lng DECIMAL(10, 7) DEFAULT 108.2225000;

COMMENT ON COLUMN transport_trips.yard_name IS 'Tên bãi đích cần giao container – NXP-138';
COMMENT ON COLUMN transport_trips.yard_address IS 'Địa chỉ cụ thể của bãi – NXP-138';
COMMENT ON COLUMN transport_trips.yard_lat IS 'Vĩ độ GPS của bãi – NXP-138';
COMMENT ON COLUMN transport_trips.yard_lng IS 'Kinh độ GPS của bãi – NXP-138';

-- Cập nhật phiên bản schema
INSERT INTO nexusport_schema_versions (version, description)
VALUES ('20261012_006', 'Add yard_name, yard_address, yard_lat, yard_lng to transport_trips for NXP-138 Yard Navigation')
ON CONFLICT (version) DO NOTHING;

COMMIT;
