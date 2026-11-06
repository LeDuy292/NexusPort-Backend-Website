-- ====================================================================
-- Migration: 20261022_021_booking_operations_and_documents.sql
-- Description: Multi-operation booking model (Export, Import, EmptyPickup, EmptyReturn),
--              combined trips, and conditional document management
-- ====================================================================

-- 1. Thêm cột mở rộng cho bảng bookings để tương thích đa tác nghiệp & chuyến xe
ALTER TABLE bookings 
  ADD COLUMN IF NOT EXISTS is_combined BOOLEAN NOT NULL DEFAULT FALSE,
  ADD COLUMN IF NOT EXISTS trip_status VARCHAR(50) NOT NULL DEFAULT 'Scheduled',
  ADD COLUMN IF NOT EXISTS customer_id UUID NULL,
  ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

-- 2. Bảng booking_operations: Quản lý từng tác nghiệp trong một Booking
CREATE TABLE IF NOT EXISTS booking_operations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id UUID NOT NULL REFERENCES bookings(id) ON DELETE CASCADE,
    operation_type VARCHAR(50) NOT NULL, -- 'Export', 'Import', 'EmptyPickup', 'EmptyReturn'
    sequence_number INT NOT NULL DEFAULT 1, -- 1 = Chặng vào (Gate In), 2 = Chặng ra (Gate Out)
    expected_gate_direction VARCHAR(20) NOT NULL, -- 'GateIn', 'GateOut'
    status VARCHAR(50) NOT NULL DEFAULT 'PendingVerification', -- 'PendingVerification', 'Approved', 'OnHold', 'InProgress', 'Completed', 'Rejected', 'Cancelled'
    
    container_id UUID NULL REFERENCES containers(id) ON DELETE SET NULL,
    container_number VARCHAR(20) NOT NULL,
    container_iso_size VARCHAR(10) NULL DEFAULT '20GP',
    cargo_status VARCHAR(20) NOT NULL DEFAULT 'Loaded', -- 'Loaded', 'Empty'
    cargo_type VARCHAR(50) NOT NULL DEFAULT 'General', -- 'General', 'Reefer', 'DangerousGoods', 'Oversized', 'Other'
    
    -- Các chứng từ / tham chiếu nghiệp vụ chuyên biệt
    carrier_booking_number VARCHAR(100) NULL, -- Booking hãng tàu (cho Export / Empty Pickup)
    bill_of_lading_number VARCHAR(100) NULL,  -- Vận đơn B/L (cho Import)
    delivery_order_number VARCHAR(100) NULL,  -- Lệnh giao hàng eDO (cho Import)
    customs_declaration_number VARCHAR(100) NULL, -- Tờ khai hải quan
    seal_number VARCHAR(50) NULL,
    gross_weight_kg NUMERIC(12,2) NULL,
    
    -- Xử lý đặc biệt
    temperature_setpoint NUMERIC(5,2) NULL,   -- Cho Reefer
    temperature_unit VARCHAR(5) NULL DEFAULT 'C',
    un_number VARCHAR(20) NULL,               -- Cho Hàng nguy hiểm
    dg_class VARCHAR(20) NULL,                -- Lớp hàng nguy hiểm
    special_handling_requirements TEXT NULL,
    
    -- Trạng thái thẩm định
    verification_status VARCHAR(50) NOT NULL DEFAULT 'Pending', -- 'Pending', 'Verified', 'Rejected', 'Exempt'
    verified_by UUID NULL,
    verified_at TIMESTAMPTZ NULL,
    verification_notes TEXT NULL,
    
    -- EIR và Nghiệm thu thực tế
    eir_number VARCHAR(100) NULL,
    eir_issued_at TIMESTAMPTZ NULL,
    container_condition VARCHAR(100) NULL DEFAULT 'Good',
    
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_booking_operations_booking_id ON booking_operations(booking_id);
CREATE INDEX IF NOT EXISTS idx_booking_operations_container_no ON booking_operations(container_number);
CREATE INDEX IF NOT EXISTS idx_booking_operations_type_status ON booking_operations(operation_type, status);

-- 3. Bảng booking_documents: Quản lý chứng từ đính kèm theo điều kiện
CREATE TABLE IF NOT EXISTS booking_documents (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id UUID NOT NULL REFERENCES bookings(id) ON DELETE CASCADE,
    booking_operation_id UUID NULL REFERENCES booking_operations(id) ON DELETE CASCADE,
    document_type VARCHAR(80) NOT NULL, -- 'CarrierBookingReference', 'BillOfLading', 'DeliveryOrder', 'CustomsDeclaration', 'EmptyContainerReleaseOrder', 'EmptyReturnAuthorization', 'DangerousGoodsDeclaration', 'ReeferTemperatureInstructions', 'VgmCertificate', 'Other'
    document_number VARCHAR(100) NULL,
    file_url VARCHAR(500) NULL,
    file_name VARCHAR(255) NULL,
    file_size_bytes BIGINT NULL,
    issuer VARCHAR(100) NULL,
    
    -- Trạng thái vòng đời tài liệu: 'Missing', 'Uploaded', 'UnderVerification', 'Verified', 'Rejected', 'Expired'
    verification_status VARCHAR(50) NOT NULL DEFAULT 'Uploaded',
    verified_by UUID NULL,
    verified_at TIMESTAMPTZ NULL,
    rejection_reason TEXT NULL,
    expiry_date TIMESTAMPTZ NULL,
    
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_booking_documents_operation_id ON booking_documents(booking_operation_id);
CREATE INDEX IF NOT EXISTS idx_booking_documents_booking_id ON booking_documents(booking_id);

-- 4. Bổ sung cột booking_operation_id cho gate_transactions để liên kết chính xác
ALTER TABLE gate_transactions
  ADD COLUMN IF NOT EXISTS booking_operation_id UUID NULL REFERENCES booking_operations(id) ON DELETE SET NULL,
  ADD COLUMN IF NOT EXISTS container_number VARCHAR(20) NULL,
  ADD COLUMN IF NOT EXISTS recognition_result JSONB NULL,
  ADD COLUMN IF NOT EXISTS override_by UUID NULL,
  ADD COLUMN IF NOT EXISTS override_reason TEXT NULL,
  ADD COLUMN IF NOT EXISTS eir_number VARCHAR(100) NULL;

-- 5. Ghi nhận phiên bản migration
INSERT INTO nexusport_schema_versions (version, description, applied_at)
VALUES ('20261022_021', 'Add booking_operations, booking_documents tables and multi-operation gate links', NOW())
ON CONFLICT (version) DO NOTHING;
