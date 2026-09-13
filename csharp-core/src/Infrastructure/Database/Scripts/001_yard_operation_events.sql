-- NXP-061: durable event/outbox record for yard operation completion.
-- Safe to run repeatedly.
CREATE TABLE IF NOT EXISTS yard_operation_events (
    id uuid PRIMARY KEY,
    operation_id uuid NOT NULL,
    container_id uuid NOT NULL,
    driver_id uuid NOT NULL,
    operation_status varchar(50) NOT NULL,
    event_type varchar(100) NOT NULL,
    delivery_status varchar(20) NOT NULL,
    occurred_at timestamptz NOT NULL,
    published_at timestamptz NULL,
    delivery_error varchar(1000) NULL,
    created_at timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_yard_operation_events_driver_occurred
    ON yard_operation_events (driver_id, occurred_at);
