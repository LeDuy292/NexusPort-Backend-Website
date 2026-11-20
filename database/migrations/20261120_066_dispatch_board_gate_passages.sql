BEGIN;

CREATE INDEX IF NOT EXISTS ix_gate_transactions_completed_booking_passages
  ON gate_transactions (booking_id, gate_type, checked_at DESC, id DESC)
  INCLUDE (driver_id) WHERE status = 'completed';

INSERT INTO nexusport_schema_versions(version, description)
VALUES ('20261120_066', 'Index completed gate passages by booking for the paginated dispatch board')
ON CONFLICT (version) DO NOTHING;

COMMIT;
