BEGIN;

-- Workflow and settlement remain separate from the immutable registration/fee
-- snapshot introduced by 037. No source Booking, visit or gate row is modified.
CREATE TABLE IF NOT EXISTS eport_service_order_states (
 draft_id uuid PRIMARY KEY REFERENCES eport_service_drafts(id),
 status varchar(24) NOT NULL CHECK(status IN ('DRAFT','CONFIRMED','PENDING_APPROVAL','RECEIVED','APPROVED','REJECTED','CANCELLED')),
 approval_status varchar(16) NOT NULL CHECK(approval_status IN ('NOT_REQUIRED','PENDING','APPROVED','REJECTED')),
 payment_status varchar(16) NOT NULL CHECK(payment_status IN ('UNPAID','NOT_REQUIRED','PENDING','PAID','FAILED')),
 version integer NOT NULL DEFAULT 1 CHECK(version>0),
 updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_eport_service_order_state_filters
 ON eport_service_order_states(status,payment_status,updated_at DESC,draft_id);

CREATE TABLE IF NOT EXISTS eport_service_order_events (
 id uuid PRIMARY KEY,
 draft_id uuid NOT NULL REFERENCES eport_service_drafts(id),
 actor_id uuid REFERENCES users(id),
 action varchar(40) NOT NULL,
 from_status varchar(24),
 to_status varchar(24),
 reason varchar(1000) NOT NULL DEFAULT '',
 request_id uuid,
 fingerprint varchar(64),
 data jsonb NOT NULL DEFAULT '{}'::jsonb CHECK(jsonb_typeof(data)='object'),
 created_at timestamptz NOT NULL DEFAULT now(),
 CHECK((request_id IS NULL AND fingerprint IS NULL) OR (request_id IS NOT NULL AND length(fingerprint)=64))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_eport_service_order_event_request
 ON eport_service_order_events(draft_id,request_id) WHERE request_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_eport_service_order_event_history
 ON eport_service_order_events(draft_id,created_at,id);

CREATE SEQUENCE IF NOT EXISTS eport_service_payos_order_code_seq START WITH 1000000000 MAXVALUE 9007199254740991;
CREATE TABLE IF NOT EXISTS eport_service_payments (
 id uuid PRIMARY KEY,
 draft_id uuid NOT NULL REFERENCES eport_service_registrations(draft_id),
 request_id uuid NOT NULL,
 number varchar(40) NOT NULL UNIQUE,
 method varchar(24) NOT NULL DEFAULT 'PAYOS' CHECK(method='PAYOS'),
 order_code bigint NOT NULL DEFAULT nextval('eport_service_payos_order_code_seq') UNIQUE CHECK(order_code>0 AND order_code<=9007199254740991),
 payment_link_id varchar(100) UNIQUE,
 checkout_url text,
 qr_code text,
 gateway_status varchar(32),
 gateway_error varchar(1000) NOT NULL DEFAULT '',
 status varchar(16) NOT NULL CHECK(status IN ('PENDING','SUBMITTED','SUCCEEDED','FAILED','CANCELLED')),
 amount numeric(24,0) NOT NULL CHECK(amount>0 AND amount<=9007199254740991),
 currency char(3) NOT NULL DEFAULT 'VND' CHECK(currency='VND'),
 customer_reference varchar(200),
 bank_reference varchar(200),
 reason varchar(1000) NOT NULL DEFAULT '',
 created_by uuid NOT NULL REFERENCES users(id),
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(draft_id,request_id),
 CHECK(bank_reference IS NULL OR length(trim(bank_reference))>0),
 CHECK(status<>'SUCCEEDED' OR bank_reference IS NOT NULL)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_eport_service_payment_active
 ON eport_service_payments(draft_id) WHERE status IN ('PENDING','SUBMITTED');
CREATE UNIQUE INDEX IF NOT EXISTS ux_eport_service_payment_success
 ON eport_service_payments(draft_id) WHERE status='SUCCEEDED';
CREATE UNIQUE INDEX IF NOT EXISTS ux_eport_service_payment_bank_reference
 ON eport_service_payments(bank_reference) WHERE bank_reference IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_eport_service_payment_history
 ON eport_service_payments(draft_id,created_at,id);

CREATE OR REPLACE FUNCTION guard_eport_service_payment_identity() RETURNS trigger
LANGUAGE plpgsql AS $$ BEGIN
 IF TG_OP='DELETE' THEN
  RAISE EXCEPTION 'Service payment history cannot be deleted.' USING ERRCODE='23514';
 END IF;
 IF TG_OP='UPDATE' THEN
  IF ROW(NEW.id,NEW.draft_id,NEW.request_id,NEW.number,NEW.method,NEW.order_code,NEW.amount,NEW.currency,NEW.created_by,NEW.created_at)
   IS DISTINCT FROM ROW(OLD.id,OLD.draft_id,OLD.request_id,OLD.number,OLD.method,OLD.order_code,OLD.amount,OLD.currency,OLD.created_by,OLD.created_at) THEN
   RAISE EXCEPTION 'Service payment identity and confirmed amount are immutable.' USING ERRCODE='23514';
  END IF;
  IF OLD.status IN ('SUCCEEDED','FAILED','CANCELLED') AND NEW IS DISTINCT FROM OLD THEN
   RAISE EXCEPTION 'Settled service payment history is immutable.' USING ERRCODE='23514';
  END IF;
  IF OLD.payment_link_id IS NOT NULL AND NEW.payment_link_id IS DISTINCT FROM OLD.payment_link_id THEN
   RAISE EXCEPTION 'PayOS payment link identity is immutable.' USING ERRCODE='23514';
  END IF;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM eport_service_registrations r WHERE r.draft_id=NEW.draft_id AND r.total=NEW.amount AND r.currency=NEW.currency) THEN
  RAISE EXCEPTION 'Service payment must match the confirmed fee snapshot.' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid='eport_service_payments'::regclass AND tgname='eport_service_payment_identity') THEN
  CREATE TRIGGER eport_service_payment_identity BEFORE INSERT OR UPDATE OR DELETE ON eport_service_payments
   FOR EACH ROW EXECUTE FUNCTION guard_eport_service_payment_identity();
 END IF;
END $$;

CREATE OR REPLACE FUNCTION guard_eport_service_order_event_immutable() RETURNS trigger
LANGUAGE plpgsql AS $$ BEGIN
 RAISE EXCEPTION 'Service order history is append-only.' USING ERRCODE='23514';
END $$;
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid='eport_service_order_events'::regclass AND tgname='eport_service_order_event_immutable') THEN
  CREATE TRIGGER eport_service_order_event_immutable BEFORE UPDATE OR DELETE ON eport_service_order_events
   FOR EACH ROW EXECUTE FUNCTION guard_eport_service_order_event_immutable();
 END IF;
END $$;

-- Preserve existing business rows. Only the new workflow tables receive an
-- explicit baseline, rather than inventing prior approval/payment events.
INSERT INTO eport_service_order_states(draft_id,status,approval_status,payment_status,version,updated_at)
 SELECT d.id,COALESCE(r.status,'DRAFT'),CASE WHEN r.status='PENDING_APPROVAL' THEN 'PENDING' ELSE 'NOT_REQUIRED' END,
 COALESCE(r.payment_status,'NOT_REQUIRED'),d.version,COALESCE(r.confirmed_at,d.updated_at)
 FROM eport_service_drafts d LEFT JOIN eport_service_registrations r ON r.draft_id=d.id
 ON CONFLICT(draft_id) DO NOTHING;
INSERT INTO eport_service_order_events(id,draft_id,action,to_status,data)
 SELECT gen_random_uuid(),o.draft_id,'BASELINE_RECORDED',o.status,jsonb_build_object('baseline',true)
 FROM eport_service_order_states o WHERE NOT EXISTS(SELECT 1 FROM eport_service_order_events e WHERE e.draft_id=o.draft_id);

CREATE OR REPLACE FUNCTION track_eport_service_draft_workflow() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE current_status text; event_actor uuid;
BEGIN
 event_actor := NULLIF(current_setting('nexusport.service_actor',true),'')::uuid;
 IF TG_OP='INSERT' THEN
  INSERT INTO eport_service_order_states(draft_id,status,approval_status,payment_status)
   VALUES(NEW.id,'DRAFT','NOT_REQUIRED','NOT_REQUIRED');
  INSERT INTO eport_service_order_events(id,draft_id,actor_id,action,to_status,data)
   VALUES(gen_random_uuid(),NEW.id,COALESCE(event_actor,NEW.created_by),'DRAFT_CREATED','DRAFT',jsonb_build_object('draftVersion',NEW.version));
 ELSE
  SELECT status INTO current_status FROM eport_service_order_states WHERE draft_id=NEW.id FOR UPDATE;
  IF current_status IS DISTINCT FROM 'DRAFT' OR EXISTS(SELECT 1 FROM eport_service_registrations WHERE draft_id=NEW.id) THEN
   RAISE EXCEPTION 'Only active service drafts may be edited.' USING ERRCODE='23514';
  END IF;
  UPDATE eport_service_order_states SET version=version+1,updated_at=now() WHERE draft_id=NEW.id;
  INSERT INTO eport_service_order_events(id,draft_id,actor_id,action,from_status,to_status,data)
   VALUES(gen_random_uuid(),NEW.id,event_actor,'DRAFT_UPDATED','DRAFT','DRAFT',jsonb_build_object('draftVersion',NEW.version));
 END IF;
 RETURN NEW;
END $$;
CREATE OR REPLACE FUNCTION track_eport_service_registration_workflow() RETURNS trigger
LANGUAGE plpgsql AS $$ BEGIN
 UPDATE eport_service_order_states SET status=NEW.status,
  approval_status=CASE WHEN NEW.status='PENDING_APPROVAL' THEN 'PENDING' ELSE 'NOT_REQUIRED' END,
  payment_status=NEW.payment_status,version=version+1,updated_at=now()
  WHERE draft_id=NEW.draft_id AND status='DRAFT';
 IF NOT FOUND THEN
  RAISE EXCEPTION 'Service registration requires an active draft.' USING ERRCODE='23514';
 END IF;
 INSERT INTO eport_service_order_events(id,draft_id,actor_id,action,from_status,to_status,data)
  VALUES(gen_random_uuid(),NEW.draft_id,NEW.confirmed_by,'REGISTERED','DRAFT',NEW.status,
   jsonb_build_object('registrationNumber',NEW.number,'total',NEW.total,'currency',NEW.currency));
 RETURN NEW;
END $$;
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid='eport_service_drafts'::regclass AND tgname='eport_service_draft_workflow') THEN
  CREATE TRIGGER eport_service_draft_workflow AFTER INSERT OR UPDATE ON eport_service_drafts
   FOR EACH ROW EXECUTE FUNCTION track_eport_service_draft_workflow();
 END IF;
 IF NOT EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid='eport_service_registrations'::regclass AND tgname='eport_service_registration_workflow') THEN
  CREATE TRIGGER eport_service_registration_workflow AFTER INSERT ON eport_service_registrations
   FOR EACH ROW EXECUTE FUNCTION track_eport_service_registration_workflow();
 END IF;
END $$;

INSERT INTO nexusport_schema_versions(version,description)
 VALUES('20261109_040','Audited company service order management and signed PayOS payment settlement; immutable confirmed fee snapshots retained')
 ON CONFLICT(version) DO NOTHING;
COMMIT;
