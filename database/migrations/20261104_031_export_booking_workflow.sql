BEGIN;
-- Additive export workflow. No seed data, backfill or mutation of historical bookings.
CREATE TABLE IF NOT EXISTS export_voyages (
 id uuid PRIMARY KEY, line_user_id uuid NOT NULL REFERENCES users(id),
 vessel_name varchar(200) NOT NULL, voyage_number varchar(100) NOT NULL,
 port_of_loading varchar(200) NOT NULL, port_of_discharge varchar(200) NOT NULL,
 etd timestamptz NOT NULL, eta timestamptz NOT NULL, receive_from timestamptz NOT NULL,
 cy_cutoff timestamptz NOT NULL, vgm_cutoff timestamptz NOT NULL, si_cutoff timestamptz NOT NULL,
 status varchar(30) NOT NULL DEFAULT 'Scheduled' CHECK(status IN ('Scheduled','Closed','Loading','Departed')),
 version integer NOT NULL DEFAULT 1, departed_at timestamptz,
 created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(line_user_id,voyage_number),
 CHECK(receive_from<cy_cutoff AND cy_cutoff<etd AND vgm_cutoff<=etd AND si_cutoff<=etd AND eta>etd)
);
CREATE TABLE IF NOT EXISTS export_bookings (
 id uuid PRIMARY KEY, request_id uuid NOT NULL, request_hash text NOT NULL,
 carrier_id uuid NOT NULL REFERENCES carriers(id), line_user_id uuid NOT NULL REFERENCES users(id),
 voyage_id uuid NOT NULL REFERENCES export_voyages(id), booking_no varchar(80) NOT NULL UNIQUE,
 container_type_code varchar(30) NOT NULL, quantity integer NOT NULL CHECK(quantity BETWEEN 1 AND 50),
 commodity_description varchar(2000) NOT NULL,
 status varchar(30) NOT NULL DEFAULT 'Pending' CHECK(status IN ('Pending','Approved','ContainersDeclared','GateIn','Loaded','BlIssued','Rejected','Cancelled')),
 version integer NOT NULL DEFAULT 1, port_status varchar(30) NOT NULL DEFAULT 'Pending',
 si jsonb, si_status varchar(20) NOT NULL DEFAULT 'None', draft jsonb, draft_version integer NOT NULL DEFAULT 0,
 freight_paid boolean NOT NULL DEFAULT false, bl_number varchar(100),
 created_by uuid NOT NULL REFERENCES users(id), created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(carrier_id,request_id)
);
CREATE TABLE IF NOT EXISTS export_booking_containers (
 id uuid PRIMARY KEY, export_booking_id uuid NOT NULL REFERENCES export_bookings(id),
 container_id uuid NOT NULL REFERENCES containers(id), declaration_id uuid NOT NULL REFERENCES container_declarations(id),
 container_no varchar(20) NOT NULL, type_code varchar(30) NOT NULL, seal_number varchar(100) NOT NULL,
 vgm_kg numeric(14,3) NOT NULL CHECK(vgm_kg>0), cargo_type text NOT NULL,
 target_temperature numeric, un_number varchar(30), dg_class varchar(30), customs_document_url text,
 customs_cleared boolean NOT NULL DEFAULT false,
 status varchar(30) NOT NULL DEFAULT 'Expected' CHECK(status IN ('Expected','GateIn','Loaded','Excluded','Rolled')),
 active boolean NOT NULL DEFAULT true, gate_in_at timestamptz, loaded_at timestamptz, exclusion_reason text,
 UNIQUE(export_booking_id,container_id), UNIQUE(export_booking_id,declaration_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_export_active_container ON export_booking_containers(container_id) WHERE active;
CREATE TABLE IF NOT EXISTS export_gate_links (
 gate_booking_id uuid PRIMARY KEY REFERENCES bookings(id),
 export_booking_id uuid NOT NULL REFERENCES export_bookings(id),
 export_container_id uuid NOT NULL REFERENCES export_booking_containers(id), created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_export_gate_container ON export_gate_links(export_container_id);
CREATE TABLE IF NOT EXISTS export_history (
 id uuid PRIMARY KEY, export_booking_id uuid REFERENCES export_bookings(id), voyage_id uuid REFERENCES export_voyages(id),
 event text NOT NULL, actor_id uuid REFERENCES users(id), description text NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS export_outbox (
 id uuid PRIMARY KEY, export_booking_id uuid REFERENCES export_bookings(id), voyage_id uuid REFERENCES export_voyages(id),
 version integer NOT NULL, event text NOT NULL, payload jsonb NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(), delivered_at timestamptz
);
CREATE TABLE IF NOT EXISTS export_inbox (
 event_id uuid PRIMARY KEY, export_booking_id uuid REFERENCES export_bookings(id), voyage_id uuid REFERENCES export_voyages(id),
 version integer NOT NULL, event text NOT NULL, payload_hash text NOT NULL, received_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS export_manifests (
 voyage_id uuid PRIMARY KEY REFERENCES export_voyages(id), snapshot jsonb NOT NULL,
 version integer NOT NULL DEFAULT 1, status text NOT NULL DEFAULT 'Draft' CHECK(status IN ('Draft','Sent')),
 created_at timestamptz NOT NULL DEFAULT now(), sent_at timestamptz
);
CREATE INDEX IF NOT EXISTS ix_export_bookings_carrier ON export_bookings(carrier_id,created_at DESC);
CREATE INDEX IF NOT EXISTS ix_export_bookings_line ON export_bookings(line_user_id,created_at DESC);

-- Only new explicitly linked export journeys are guarded; historical gate flows are unchanged.
ALTER TABLE gate_transactions ADD COLUMN IF NOT EXISTS actual_seal_number varchar(100);
ALTER TABLE gate_barrier_commands ADD COLUMN IF NOT EXISTS actual_seal_number varchar(100);
CREATE OR REPLACE FUNCTION guard_export_gate_passage() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE l export_gate_links; b export_bookings; c export_booking_containers; v export_voyages; occurred timestamptz; event_uuid uuid;
BEGIN
 IF NEW.gate_type<>'gate_in' OR NEW.status<>'completed' THEN RETURN NEW; END IF;
 IF TG_OP='UPDATE' AND OLD.status='completed' AND OLD.gate_type='gate_in' THEN RETURN NEW; END IF;
 SELECT * INTO l FROM export_gate_links WHERE gate_booking_id=NEW.booking_id;
 IF NOT FOUND THEN RETURN NEW; END IF;
 SELECT * INTO b FROM export_bookings WHERE id=l.export_booking_id FOR UPDATE;
 SELECT * INTO c FROM export_booking_containers WHERE id=l.export_container_id FOR UPDATE;
 SELECT * INTO v FROM export_voyages WHERE id=b.voyage_id FOR UPDATE;
 occurred:=COALESCE(NEW.completed_at,now());
 IF b.status NOT IN ('ContainersDeclared','GateIn') OR b.port_status<>'Accepted' OR c.status<>'Expected' OR NOT c.active
 OR v.status<>'Scheduled' OR occurred<v.receive_from OR occurred>v.cy_cutoff
 OR c.vgm_kg<=0 OR NOT c.customs_cleared THEN
  RAISE EXCEPTION 'Export Gate In requires accepted container, valid VGM, customs release and open CY receiving window.' USING ERRCODE='23514';
 END IF;
 IF COALESCE(NEW.container_number,'')<>c.container_no THEN
  RAISE EXCEPTION 'Actual Gate In container does not match export declaration.' USING ERRCODE='23514';
 END IF;
 IF upper(trim(COALESCE(NEW.actual_seal_number,'')))<>upper(trim(c.seal_number)) THEN
  RAISE EXCEPTION 'Observed export seal must match the accepted shipping declaration.' USING ERRCODE='23514';
 END IF;
 UPDATE export_booking_containers SET status='GateIn',gate_in_at=occurred WHERE id=c.id;
 UPDATE export_bookings SET status='GateIn',version=version+1,updated_at=now() WHERE id=b.id RETURNING * INTO b;
 event_uuid:=gen_random_uuid();
 INSERT INTO export_outbox(id,export_booking_id,voyage_id,version,event,payload,delivered_at)
 VALUES(event_uuid,b.id,b.voyage_id,b.version,'GateIn',jsonb_build_object('gateTransactionId',NEW.id,'exportContainerId',c.id,'occurredAt',occurred),now());
 INSERT INTO export_inbox(event_id,export_booking_id,voyage_id,version,event,payload_hash)
 VALUES(event_uuid,b.id,b.voyage_id,b.version,'GateIn',md5(NEW.id::text));
 INSERT INTO export_history(id,export_booking_id,voyage_id,event,description)
 VALUES(gen_random_uuid(),b.id,b.voyage_id,'GateIn','Physical gate passage recorded by existing port workflow.');
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_export_gate_passage ON gate_transactions;
CREATE TRIGGER trg_export_gate_passage BEFORE INSERT OR UPDATE ON gate_transactions
 FOR EACH ROW EXECUTE FUNCTION guard_export_gate_passage();
INSERT INTO nexusport_schema_versions(version,description)
 VALUES('20261104_031','Tenant scoped export bookings, line approval, container VGM, SI drafts, voyage and manifest lifecycle')
 ON CONFLICT(version) DO NOTHING;
COMMIT;
