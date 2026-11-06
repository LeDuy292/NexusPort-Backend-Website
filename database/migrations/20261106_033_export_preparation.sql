BEGIN;

ALTER TABLE export_bookings ADD COLUMN IF NOT EXISTS preparation jsonb;

CREATE TABLE IF NOT EXISTS export_booking_drafts (
 id uuid PRIMARY KEY,
 carrier_id uuid NOT NULL REFERENCES carriers(id),
 created_by uuid NOT NULL REFERENCES users(id),
 updated_by uuid NOT NULL REFERENCES users(id),
 content jsonb NOT NULL CHECK(jsonb_typeof(content)='object'),
 version integer NOT NULL DEFAULT 1 CHECK(version>0),
 last_request_id uuid NOT NULL,
 content_hash text NOT NULL,
 submitted_booking_id uuid REFERENCES export_bookings(id),
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_export_drafts_company ON export_booking_drafts(carrier_id,updated_at DESC);

CREATE TABLE IF NOT EXISTS export_source_reservations (
 export_booking_id uuid NOT NULL REFERENCES export_bookings(id),
 container_id uuid NOT NULL REFERENCES containers(id),
 declaration_id uuid NOT NULL,
 active boolean NOT NULL DEFAULT true,
 created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(export_booking_id,container_id),
 FOREIGN KEY(declaration_id,container_id) REFERENCES container_declarations(id,container_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_export_prepared_container ON export_source_reservations(container_id) WHERE active;

CREATE TABLE IF NOT EXISTS export_document_files (
 id uuid PRIMARY KEY,
 carrier_id uuid NOT NULL REFERENCES carriers(id),
 uploaded_by uuid NOT NULL REFERENCES users(id),
 object_key text NOT NULL UNIQUE,
 original_name varchar(500) NOT NULL,
 content_type varchar(150) NOT NULL,
 size_bytes bigint NOT NULL CHECK(size_bytes>0 AND size_bytes<=26214400),
 sha256 char(64) NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_export_document_company ON export_document_files(carrier_id,created_at DESC);

INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261106_033','Private export drafts, preparation reservations and S3 document identities')
ON CONFLICT(version) DO NOTHING;

COMMIT;
