CREATE TABLE IF NOT EXISTS container_confirmation_audits (
    id uuid PRIMARY KEY,
    booking_id uuid NOT NULL REFERENCES bookings(id),
    container_id uuid NOT NULL REFERENCES "Containers"(id),
    driver_id uuid NOT NULL REFERENCES drivers(id),
    condition varchar(30) NOT NULL,
    notes varchar(1000),
    container_status_after varchar(50) NOT NULL,
    confirmed_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_container_confirmation_booking_container UNIQUE (booking_id, container_id)
);

CREATE INDEX IF NOT EXISTS ix_container_confirmation_audits_driver_id
    ON container_confirmation_audits (driver_id);
