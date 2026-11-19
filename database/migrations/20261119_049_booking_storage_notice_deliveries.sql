BEGIN;

CREATE TABLE IF NOT EXISTS booking_storage_notice_deliveries (
  booking_id uuid NOT NULL REFERENCES bookings(id) ON DELETE CASCADE,
  container_visit_id uuid NOT NULL REFERENCES container_visits(id) ON DELETE CASCADE,
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  notice_key varchar(32) NOT NULL CHECK (notice_key IN ('EXPIRING','OVERDUE','NO_RESPONSE_1D','ESCALATED_3D')),
  booking_date date NOT NULL,
  sent_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (container_visit_id, user_id, notice_key, booking_date)
);

CREATE INDEX IF NOT EXISTS ix_booking_storage_notice_deliveries_booking
  ON booking_storage_notice_deliveries (booking_id, sent_at DESC);

INSERT INTO nexusport_schema_versions(version,description)
VALUES ('20261119_049','Daily booking-based yard-storage notices with recipient-level deduplication')
ON CONFLICT(version) DO NOTHING;

COMMIT;
