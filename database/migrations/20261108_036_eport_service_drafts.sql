BEGIN;

-- Part 2 only: private company drafts. No reservations, billing, gate permissions
-- or changes to existing source Booking/container records are created here.
CREATE TABLE IF NOT EXISTS eport_service_drafts (
 id uuid PRIMARY KEY,
 carrier_id uuid NOT NULL REFERENCES carriers(id),
 created_by uuid NOT NULL REFERENCES users(id),
 number varchar(40) NOT NULL UNIQUE,
 flow varchar(6) NOT NULL CHECK(flow IN ('EXPORT','IMPORT')),
 main_service_id uuid REFERENCES eport_services(id),
 status varchar(10) NOT NULL DEFAULT 'DRAFT' CHECK(status='DRAFT'),
 version integer NOT NULL DEFAULT 1 CHECK(version>0),
 content jsonb NOT NULL CHECK(jsonb_typeof(content)='object'),
 source_snapshot jsonb NOT NULL DEFAULT '{}'::jsonb CHECK(jsonb_typeof(source_snapshot)='object'),
 container_snapshots jsonb NOT NULL DEFAULT '[]'::jsonb CHECK(jsonb_typeof(container_snapshots)='array'),
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_eport_service_drafts_company_updated
 ON eport_service_drafts(carrier_id,updated_at DESC,id);

INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261108_036','Private company-scoped service registration drafts with versioned source/container snapshots')
ON CONFLICT(version) DO NOTHING;
COMMIT;
