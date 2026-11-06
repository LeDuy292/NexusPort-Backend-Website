-- ============================================================================
-- NEXUSPORT MIGRATION #005: REEFER POWER MANAGEMENT MODULE
-- Date: 2026-10-12
-- Description: Nâng cấp phân hệ quản lý cấp điện container lạnh
--   - Tách biệt usage_status và technical_status trên reefer_plugs
--   - Thêm quy trình 2 bước: Gán ổ cắm → Xác nhận cấp điện
--   - Tạo bảng reefer_power_requests, reefer_temperature_records, reefer_alerts
--   - Partial Unique Indexes chống race condition
-- ============================================================================

BEGIN;

-- ────────────────────────────────────────────────────
-- 1. ENUM TYPES
-- ────────────────────────────────────────────────────

DO $$ BEGIN
  CREATE TYPE reefer_request_status AS ENUM (
    'PENDING_ASSIGNMENT', 'ASSIGNED', 'IN_PROGRESS', 'COMPLETED', 'CANCELLED', 'REJECTED'
  );
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
  CREATE TYPE socket_usage_status AS ENUM (
    'AVAILABLE', 'RESERVED', 'OCCUPIED'
  );
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
  CREATE TYPE socket_technical_status AS ENUM (
    'HEALTHY', 'FAULTY', 'UNDER_MAINTENANCE', 'DECOMMISSIONED'
  );
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
  CREATE TYPE power_session_status AS ENUM (
    'AWAITING_POWER', 'ACTIVE', 'SUSPENDED', 'COMPLETED', 'TRANSFERRED', 'CANCELLED'
  );
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
  CREATE TYPE actual_power_status AS ENUM (
    'UNKNOWN', 'POWERED_ON', 'POWER_LOST', 'POWERED_OFF'
  );
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
  CREATE TYPE temp_data_source AS ENUM (
    'MANUAL_INSPECTION', 'IOT_TELEMATICS', 'PLC_GATEWAY'
  );
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
  CREATE TYPE alert_severity AS ENUM (
    'INFO', 'WARNING', 'CRITICAL'
  );
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
  CREATE TYPE reefer_alert_status AS ENUM (
    'TRIGGERED', 'ACKNOWLEDGED', 'IN_PROGRESS', 'RESOLVED', 'DISMISSED'
  );
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;


-- ────────────────────────────────────────────────────
-- 2. TABLE: reefer_power_requests
-- ────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.reefer_power_requests (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    request_no            VARCHAR(60) NOT NULL,
    container_id          UUID NOT NULL REFERENCES public.containers(id) ON DELETE RESTRICT,
    target_temperature    NUMERIC(5, 2) NOT NULL,
    temperature_unit      VARCHAR(5) NOT NULL DEFAULT 'C' CHECK (temperature_unit IN ('C', 'F')),
    temp_tolerance        NUMERIC(4, 2) NOT NULL DEFAULT 2.00,
    cargo_description     TEXT,
    special_instructions  TEXT,
    status                reefer_request_status NOT NULL DEFAULT 'PENDING_ASSIGNMENT',
    requested_by          UUID REFERENCES public.users(id),
    cancelled_reason      TEXT,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_reefer_req_no ON public.reefer_power_requests(request_no);
CREATE INDEX IF NOT EXISTS idx_reefer_req_container_status ON public.reefer_power_requests(container_id, status);
CREATE INDEX IF NOT EXISTS idx_reefer_req_created ON public.reefer_power_requests(created_at DESC);


-- ────────────────────────────────────────────────────
-- 3. ALTER TABLE: reefer_plugs
-- ────────────────────────────────────────────────────

ALTER TABLE public.reefer_plugs
    ADD COLUMN IF NOT EXISTS usage_status       socket_usage_status NOT NULL DEFAULT 'AVAILABLE',
    ADD COLUMN IF NOT EXISTS technical_status    socket_technical_status NOT NULL DEFAULT 'HEALTHY',
    ADD COLUMN IF NOT EXISTS voltage_type        VARCHAR(30) DEFAULT '380V-3Ph-32A',
    ADD COLUMN IF NOT EXISTS max_power_kw        NUMERIC(6, 2),
    ADD COLUMN IF NOT EXISTS last_maintained_at  TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS created_at          TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    ADD COLUMN IF NOT EXISTS updated_at          TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP;

CREATE INDEX IF NOT EXISTS idx_reefer_plugs_status ON public.reefer_plugs(usage_status, technical_status);


-- ────────────────────────────────────────────────────
-- 4. ALTER TABLE: reefer_power_sessions
-- ────────────────────────────────────────────────────

ALTER TABLE public.reefer_power_sessions
    ADD COLUMN IF NOT EXISTS request_id           UUID REFERENCES public.reefer_power_requests(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS session_status        power_session_status NOT NULL DEFAULT 'AWAITING_POWER',
    ADD COLUMN IF NOT EXISTS actual_power_status   actual_power_status NOT NULL DEFAULT 'UNKNOWN',
    ADD COLUMN IF NOT EXISTS assigned_at           TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    ADD COLUMN IF NOT EXISTS power_confirmed_by    UUID REFERENCES public.users(id),
    ADD COLUMN IF NOT EXISTS termination_reason    VARCHAR(100),
    ADD COLUMN IF NOT EXISTS parent_session_id     UUID REFERENCES public.reefer_power_sessions(id),
    ADD COLUMN IF NOT EXISTS billable_hours        NUMERIC(8, 2) DEFAULT 0.00,
    ADD COLUMN IF NOT EXISTS created_at            TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP,
    ADD COLUMN IF NOT EXISTS updated_at            TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP;

-- Cho phép NULL khi chưa cấp điện (giai đoạn mới chỉ gán ổ cắm)
ALTER TABLE public.reefer_power_sessions ALTER COLUMN started_at DROP NOT NULL;
ALTER TABLE public.reefer_power_sessions ALTER COLUMN started_at DROP DEFAULT;

-- Partial Unique Index – Một ổ cắm chỉ có 1 phiên active/awaiting
CREATE UNIQUE INDEX IF NOT EXISTS uq_active_session_per_plug
ON public.reefer_power_sessions (plug_id)
WHERE session_status IN ('AWAITING_POWER', 'ACTIVE');

-- Partial Unique Index – Một container chỉ có 1 phiên active/awaiting
CREATE UNIQUE INDEX IF NOT EXISTS uq_active_session_per_container
ON public.reefer_power_sessions (container_id)
WHERE session_status IN ('AWAITING_POWER', 'ACTIVE');


-- ────────────────────────────────────────────────────
-- 5. TABLE: reefer_temperature_records
-- ────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.reefer_temperature_records (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    session_id            UUID NOT NULL REFERENCES public.reefer_power_sessions(id) ON DELETE CASCADE,
    supply_temperature    NUMERIC(5, 2),
    return_temperature    NUMERIC(5, 2) NOT NULL,
    temperature_unit      VARCHAR(5) NOT NULL DEFAULT 'C' CHECK (temperature_unit IN ('C', 'F')),
    ambient_temperature   NUMERIC(5, 2),
    data_source           temp_data_source NOT NULL DEFAULT 'MANUAL_INSPECTION',
    is_abnormal           BOOLEAN NOT NULL DEFAULT FALSE,
    recorded_by           UUID REFERENCES public.users(id),
    device_id             VARCHAR(100),
    notes                 TEXT,
    recorded_at           TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_temp_session_recorded ON public.reefer_temperature_records(session_id, recorded_at DESC);


-- ────────────────────────────────────────────────────
-- 6. TABLE: reefer_alerts
-- ────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS public.reefer_alerts (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    alert_code        VARCHAR(50) NOT NULL,
    session_id        UUID REFERENCES public.reefer_power_sessions(id) ON DELETE CASCADE,
    plug_id           UUID REFERENCES public.reefer_plugs(id) ON DELETE SET NULL,
    container_id      UUID REFERENCES public.containers(id) ON DELETE SET NULL,
    alert_type        VARCHAR(60) NOT NULL,
    severity          alert_severity NOT NULL DEFAULT 'WARNING',
    status            reefer_alert_status NOT NULL DEFAULT 'TRIGGERED',
    message           TEXT NOT NULL,
    payload           JSONB,
    acknowledged_by   UUID REFERENCES public.users(id),
    acknowledged_at   TIMESTAMPTZ,
    resolved_by       UUID REFERENCES public.users(id),
    resolved_at       TIMESTAMPTZ,
    resolution_notes  TEXT,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_alerts_active ON public.reefer_alerts(status, severity)
WHERE status IN ('TRIGGERED', 'ACKNOWLEDGED', 'IN_PROGRESS');

CREATE INDEX IF NOT EXISTS idx_alerts_session ON public.reefer_alerts(session_id);


-- ────────────────────────────────────────────────────
-- 7. SCHEMA VERSION RECORD
-- ────────────────────────────────────────────────────

INSERT INTO public.nexusport_schema_versions (version, description, applied_at)
VALUES ('005', 'Reefer Power Management module – requests, enhanced plugs/sessions, temperature records, alerts', CURRENT_TIMESTAMP)
ON CONFLICT (version) DO UPDATE 
SET description = EXCLUDED.description, applied_at = CURRENT_TIMESTAMP;

COMMIT;
