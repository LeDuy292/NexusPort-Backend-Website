BEGIN;

-- ============================================================
-- Migration: 20261009_005 — Transport Trips & History
-- Feature:   NXP-137 Driver Transport Trip workflow
-- Author:    NexusPort Team
-- Date:      2026-10-09
-- ============================================================

-- Bảng chính: lưu thông tin và trạng thái chuyến vận chuyển
CREATE TABLE IF NOT EXISTS transport_trips (
  id                      UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
  trip_code               VARCHAR(50)   NOT NULL UNIQUE,

  -- Liên kết người thực hiện
  driver_id               UUID          NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  assigned_by             UUID          REFERENCES users(id) ON DELETE SET NULL,

  -- Thông tin booking (booking nằm ở service .NET, lưu bản chụp)
  booking_id              UUID,                           -- soft ref đến bookings (nullable)
  booking_code            VARCHAR(50),
  booking_type            VARCHAR(20),                    -- 'Pickup' | 'Dropoff'

  -- Thông tin xe và container
  truck_id                UUID          REFERENCES trucks(id) ON DELETE SET NULL,
  truck_plate             VARCHAR(20),
  container_no            VARCHAR(20),
  container_type          VARCHAR(50),
  cargo_type              VARCHAR(50),

  -- Thông tin địa điểm
  gate_in_code            VARCHAR(20),
  gate_out_code           VARCHAR(20),
  yard_block              VARCHAR(20),
  yard_slot               VARCHAR(20),
  yard_zone               VARCHAR(50),
  yard_instructions       TEXT,

  -- Thông tin giao nhận container (điền khi deliver-container)
  seal_no                 VARCHAR(50),
  container_condition     VARCHAR(100),
  recipient_name          VARCHAR(100),

  -- Trạng thái chuyến
  status                  VARCHAR(50)   NOT NULL DEFAULT 'Assigned'
    CONSTRAINT transport_trips_status_check CHECK (
      status IN (
        'Assigned', 'Acknowledged', 'Vehicle Picked Up', 'Container Picked Up',
        'Gate In', 'In Transit', 'Arrived at Yard', 'Container Delivered',
        'Gate Out Completed', 'Vehicle Returned', 'Completed', 'Cancelled'
      )
    ),

  -- Timestamps từng bước (NULL = chưa thực hiện bước đó)
  acknowledged_at         TIMESTAMPTZ,
  vehicle_picked_up_at    TIMESTAMPTZ,
  container_picked_up_at  TIMESTAMPTZ,
  gate_in_at              TIMESTAMPTZ,
  in_transit_at           TIMESTAMPTZ,
  arrived_at_yard_at      TIMESTAMPTZ,
  container_delivered_at  TIMESTAMPTZ,
  gate_out_at             TIMESTAMPTZ,
  vehicle_returned_at     TIMESTAMPTZ,
  completed_at            TIMESTAMPTZ,

  -- Lịch hẹn
  appointment_start       TIMESTAMPTZ,
  appointment_end         TIMESTAMPTZ,

  note                    TEXT,

  created_at              TIMESTAMPTZ   NOT NULL DEFAULT NOW(),
  updated_at              TIMESTAMPTZ   NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE transport_trips IS 'Chuyến vận chuyển container của Driver – NXP-137';
COMMENT ON COLUMN transport_trips.status IS 'Trạng thái chuyến theo thứ tự: Assigned→Acknowledged→Vehicle Picked Up→Container Picked Up→In Transit→Arrived at Yard→Container Delivered→Gate Out Completed→Completed';
COMMENT ON COLUMN transport_trips.booking_id IS 'Soft reference – Booking quản lý bởi .NET service, không dùng FK cứng';

-- ============================================================
-- Bảng lịch sử: audit trail mọi lần đổi trạng thái
-- ============================================================
CREATE TABLE IF NOT EXISTS transport_trip_history (
  id            UUID          PRIMARY KEY DEFAULT gen_random_uuid(),
  trip_id       UUID          NOT NULL REFERENCES transport_trips(id) ON DELETE CASCADE,
  from_status   VARCHAR(50),                -- NULL khi mới tạo (Assigned)
  to_status     VARCHAR(50)   NOT NULL,
  changed_by    UUID          REFERENCES users(id) ON DELETE SET NULL,
  note          TEXT,
  changed_at    TIMESTAMPTZ   NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE transport_trip_history IS 'Lịch sử thay đổi trạng thái chuyến vận chuyển – NXP-137';

-- ============================================================
-- Indexes
-- ============================================================

-- Driver xem chuyến của mình (query phổ biến nhất)
CREATE INDEX IF NOT EXISTS idx_transport_trips_driver_id
  ON transport_trips (driver_id, status, appointment_start);

-- Admin/Dispatcher lọc theo status
CREATE INDEX IF NOT EXISTS idx_transport_trips_status
  ON transport_trips (status);

-- Tra cứu theo trip_code
CREATE INDEX IF NOT EXISTS idx_transport_trips_trip_code
  ON transport_trips (trip_code);

-- Lịch sử theo chuyến
CREATE INDEX IF NOT EXISTS idx_transport_trip_history_trip_id
  ON transport_trip_history (trip_id, changed_at);

-- ============================================================
-- Auto-update updated_at trigger
-- ============================================================
CREATE OR REPLACE FUNCTION set_transport_trip_updated_at()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
  NEW.updated_at = NOW();
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_transport_trips_updated_at ON transport_trips;
CREATE TRIGGER trg_transport_trips_updated_at
  BEFORE UPDATE ON transport_trips
  FOR EACH ROW EXECUTE FUNCTION set_transport_trip_updated_at();

-- ============================================================
-- Schema version
-- ============================================================
INSERT INTO nexusport_schema_versions (version, description)
VALUES ('20261009_005', 'Add transport_trips and transport_trip_history tables for NXP-137 Driver Transport Trip workflow')
ON CONFLICT (version) DO NOTHING;

COMMIT;
