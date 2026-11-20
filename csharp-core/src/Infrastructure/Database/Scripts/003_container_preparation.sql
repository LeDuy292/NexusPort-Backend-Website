-- ==============================================================================
-- Script 003 (TỐI ƯU HÓA DB): Nghiệp vụ Chuẩn bị Container (Chiều Nhập 5A & Chiều Xuất 5B)
-- Mục đích: Tái sử dụng & tích hợp tối đa vào các bảng hiện hữu (yard_tasks, yard_slots, 
--          container_positions, bookings, export_bookings) thay vì sinh bảng trùng lặp.
-- ==============================================================================

-- 1. Cấu hình thời gian & thông số chuẩn bị (Bảng tham số chuẩn 1 dòng)
CREATE TABLE IF NOT EXISTS preparation_configs (
    id UUID PRIMARY KEY,
    lead_time_minutes INT NOT NULL DEFAULT 90,
    max_rehandle_per_task INT NOT NULL DEFAULT 6,
    staging_block_id UUID NULL,
    reschedule_grace_minutes INT NOT NULL DEFAULT 15,
    delay_check_interval_seconds INT NOT NULL DEFAULT 60,
    estimated_minutes_per_move INT NOT NULL DEFAULT 8,
    weight_due_near INT NOT NULL DEFAULT 50,
    weight_reefer INT NOT NULL DEFAULT 30,
    weight_expiring_free_time INT NOT NULL DEFAULT 20,
    -- Cấu hình Chiều Xuất (5B - Outbound Marshalling)
    outbound_lead_time_minutes INT NOT NULL DEFAULT 150,
    reefer_max_unplug_minutes INT NOT NULL DEFAULT 45,
    closing_time_grace_minutes INT NOT NULL DEFAULT 30,
    marshalling_mode VARCHAR(30) NOT NULL DEFAULT 'InBayHousekeeping',
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Seed cấu hình mặc định tối ưu nếu chưa có
INSERT INTO preparation_configs (
    id, lead_time_minutes, max_rehandle_per_task, staging_block_id,
    reschedule_grace_minutes, delay_check_interval_seconds, estimated_minutes_per_move,
    weight_due_near, weight_reefer, weight_expiring_free_time,
    outbound_lead_time_minutes, reefer_max_unplug_minutes, closing_time_grace_minutes, marshalling_mode,
    created_at
)
VALUES (
    '00000000-0000-0000-0000-000000000001', 90, 6, NULL,
    15, 60, 8,
    50, 30, 20,
    150, 45, 30, 'InBayHousekeeping',
    CURRENT_TIMESTAMP
)
ON CONFLICT (id) DO UPDATE SET
    outbound_lead_time_minutes = EXCLUDED.outbound_lead_time_minutes,
    reefer_max_unplug_minutes = EXCLUDED.reefer_max_unplug_minutes,
    closing_time_grace_minutes = EXCLUDED.closing_time_grace_minutes,
    marshalling_mode = EXCLUDED.marshalling_mode;

-- 2. Lệnh chuẩn bị container (PreparationTask) - Tích hợp chặt chẽ với bookings & export_bookings
CREATE TABLE IF NOT EXISTS preparation_tasks (
    id UUID PRIMARY KEY,
    direction VARCHAR(30) NOT NULL DEFAULT 'Inbound', -- Inbound (5A - Giao xe khách) | Outbound (5B - Xếp tàu)
    service_order_id UUID NULL,
    slot_booking_id UUID NULL,                         -- Khóa logic trỏ tới bookings(id) hoặc export_bookings(id)
    container_no VARCHAR(50) NOT NULL,
    target_ready_at TIMESTAMPTZ NOT NULL,
    start_by TIMESTAMPTZ NOT NULL,
    priority INT NOT NULL DEFAULT 0,
    status VARCHAR(30) NOT NULL DEFAULT 'Pending',
    planned_rehandle_count INT NOT NULL DEFAULT 0,
    actual_rehandle_count INT NOT NULL DEFAULT 0,
    -- Thông tin liên kết đặc thù Chiều Xuất (5B - Tái sử dụng metadata từ export_voyages / export_booking_containers)
    voyage_id UUID NULL,
    vessel_name VARCHAR(255) NULL,
    voyage_code VARCHAR(100) NULL,
    stowage_position VARCHAR(100) NULL, -- Bay-Row-Tier trên tàu
    loading_sequence INT NULL,          -- Thứ tự cẩu bờ bốc hàng lên tàu
    quay_crane_code VARCHAR(50) NULL,   -- Cẩu bờ phụ trách (QC-01, QC-02)
    pod VARCHAR(100) NULL,              -- Cảng dỡ hàng (Port of Discharge)
    vgm_weight DOUBLE PRECISION NULL,   -- Khối lượng toàn bộ VGM (SOLAS)
    is_vgm_verified BOOLEAN NOT NULL DEFAULT FALSE,
    is_customs_cleared BOOLEAN NOT NULL DEFAULT FALSE,
    has_hold_flag BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    ready_at TIMESTAMPTZ NULL,
    cancel_reason VARCHAR(500) NULL,
    note VARCHAR(1000) NULL,
    row_version BIGINT NOT NULL DEFAULT 1
);

-- Chỉ mục tối ưu truy vấn nghiệp vụ điều độ TOS
CREATE UNIQUE INDEX IF NOT EXISTS uq_preparation_tasks_active_inbound 
ON preparation_tasks (slot_booking_id, container_no) 
WHERE direction = 'Inbound' AND status NOT IN ('Cancelled', 'Failed');

CREATE UNIQUE INDEX IF NOT EXISTS uq_preparation_tasks_active_outbound 
ON preparation_tasks (container_no, voyage_code) 
WHERE direction = 'Outbound' AND status NOT IN ('Cancelled', 'Failed');

CREATE INDEX IF NOT EXISTS idx_preparation_tasks_dir_stat ON preparation_tasks (direction, status, target_ready_at);
CREATE INDEX IF NOT EXISTS idx_preparation_tasks_container ON preparation_tasks (container_no);
CREATE INDEX IF NOT EXISTS idx_preparation_tasks_loading_seq ON preparation_tasks (voyage_code, loading_sequence);

-- 3. Lệnh làm hàng / đảo chuyển (WorkOrder) - Đồng bộ trực tiếp với yard_tasks & yard_slots
CREATE TABLE IF NOT EXISTS preparation_work_orders (
    id UUID PRIMARY KEY,
    preparation_task_id UUID NOT NULL REFERENCES preparation_tasks(id) ON DELETE CASCADE,
    yard_task_id UUID NULL,             -- Liên kết trực tiếp tới bảng canonical yard_tasks của điều độ
    type VARCHAR(30) NOT NULL DEFAULT 'Rehandle', -- Rehandle (Đảo chuyển) | Stage (Bãi đệm) | LoadApron (Chân cẩu)
    sequence INT NOT NULL DEFAULT 1,
    container_no VARCHAR(50) NOT NULL,
    from_slot_id UUID NOT NULL,         -- Tái sử dụng yard_slots(id)
    to_slot_id UUID NOT NULL,           -- Tái sử dụng yard_slots(id)
    equipment_id UUID NULL,             -- Liên kết thiết bị Equipments
    assignee_id UUID NULL,              -- Tài xế/Người vận hành Users/Drivers
    priority INT NOT NULL DEFAULT 0,
    due_at TIMESTAMPTZ NOT NULL,
    status VARCHAR(30) NOT NULL DEFAULT 'Pending',
    started_at TIMESTAMPTZ NULL,
    completed_at TIMESTAMPTZ NULL,
    fail_reason VARCHAR(500) NULL,
    charge_party VARCHAR(30) NOT NULL DEFAULT 'Port', -- Port (Miễn phí đảo chuẩn cảng) | Carrier/Shipper (Thu phí phụ)
    selection_reason VARCHAR(500) NULL,
    row_version BIGINT NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_work_orders_task_id ON preparation_work_orders (preparation_task_id);
CREATE INDEX IF NOT EXISTS idx_work_orders_status ON preparation_work_orders (status);
CREATE INDEX IF NOT EXISTS idx_work_orders_yard_task ON preparation_work_orders (yard_task_id);
CREATE INDEX IF NOT EXISTS idx_work_orders_slots ON preparation_work_orders (from_slot_id, to_slot_id);

-- 4. Khóa giữ chỗ ô bãi tạm thời (SlotReservation) - Đồng bộ yard_slots.status = 'reserved'
CREATE TABLE IF NOT EXISTS slot_reservations (
    id UUID PRIMARY KEY,
    slot_id UUID NOT NULL,              -- Tham chiếu trực tiếp yard_slots(id)
    work_order_id UUID NOT NULL,
    reserved_until TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_slot_reservations_slot ON slot_reservations (slot_id);
CREATE INDEX IF NOT EXISTS idx_slot_reservations_until ON slot_reservations (reserved_until);

-- 5. Lịch sử di chuyển container (ContainerMovement) - Tương thích với container_positions
CREATE TABLE IF NOT EXISTS container_movements (
    id UUID PRIMARY KEY,
    container_no VARCHAR(50) NOT NULL,
    from_slot_id UUID NOT NULL,
    to_slot_id UUID NOT NULL,
    moved_by UUID NULL,
    equipment_id UUID NULL,
    moved_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    work_order_id UUID NULL,
    note VARCHAR(500) NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_container_movements_no ON container_movements (container_no, moved_at DESC);

-- 6. e-D/O (EDoRecord): edo_records là view từ delivery_orders, tạo bởi migration 20261120_061 (không còn là bảng).

-- 7. Danh mục ngoại lệ bãi (ExceptionItem)
CREATE TABLE IF NOT EXISTS exception_items (
    id UUID PRIMARY KEY,
    category VARCHAR(50) NOT NULL DEFAULT 'Preparation',
    entity_id UUID NULL,
    container_no VARCHAR(50) NULL,
    reason VARCHAR(1000) NOT NULL,
    severity VARCHAR(20) NOT NULL DEFAULT 'Medium',
    status VARCHAR(20) NOT NULL DEFAULT 'Open',
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    resolved_at TIMESTAMPTZ NULL,
    notes VARCHAR(1000) NULL
);

CREATE INDEX IF NOT EXISTS idx_exception_items_category ON exception_items (category);
CREATE INDEX IF NOT EXISTS idx_exception_items_status ON exception_items (status);

-- 8. Outbox thông điệp sự kiện bền vững (PreparationOutboxMessage)
CREATE TABLE IF NOT EXISTS preparation_outbox_messages (
    id UUID PRIMARY KEY,
    event_id VARCHAR(64) NOT NULL,
    event_type VARCHAR(100) NOT NULL,
    payload TEXT NOT NULL,
    version INT NOT NULL DEFAULT 1,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    published_at TIMESTAMPTZ NULL,
    retry_count INT NOT NULL DEFAULT 0,
    error VARCHAR(1000) NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_outbox_pending ON preparation_outbox_messages (published_at, retry_count);

-- ==============================================================================
-- Dữ liệu mẫu (Seed Data) chuẩn hóa cho e-D/O
-- ==============================================================================

