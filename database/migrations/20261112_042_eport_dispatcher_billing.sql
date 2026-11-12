BEGIN;
-- NULL means no payable amount has been returned yet. Existing totals are retained.
ALTER TABLE eport_service_registrations ALTER COLUMN total DROP NOT NULL;
CREATE TABLE IF NOT EXISTS eport_service_bills (
 id uuid PRIMARY KEY,
 draft_id uuid NOT NULL UNIQUE REFERENCES eport_service_registrations(draft_id),
 request_id uuid NOT NULL,
 actor_id uuid NOT NULL REFERENCES users(id),
 external_reference varchar(200) NOT NULL,
 total numeric(24,0) NOT NULL CHECK(total>=0 AND total<=9007199254740991),
 currency char(3) NOT NULL DEFAULT 'VND' CHECK(currency='VND'),
 fees jsonb NOT NULL DEFAULT '[]'::jsonb CHECK(jsonb_typeof(fees)='array'),
 note varchar(1000) NOT NULL DEFAULT '',
 fingerprint varchar(64) NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS eport_service_mail_queue (
 id uuid PRIMARY KEY,
 draft_id uuid NOT NULL REFERENCES eport_service_registrations(draft_id),
 kind varchar(24) NOT NULL CHECK(kind IN ('PAYMENT_NOTICE','PAYMENT_RECEIPT')),
 recipient varchar(254) NOT NULL,
 payload jsonb NOT NULL CHECK(jsonb_typeof(payload)='object'),
 status varchar(16) NOT NULL DEFAULT 'PENDING' CHECK(status IN ('PENDING','SENDING','SENT','FAILED')),
 attempts integer NOT NULL DEFAULT 0 CHECK(attempts>=0),
 available_at timestamptz NOT NULL DEFAULT now(),
 lease_id uuid,
 last_error varchar(500),
 created_at timestamptz NOT NULL DEFAULT now(),
 sent_at timestamptz,
 UNIQUE(draft_id,kind)
);
CREATE INDEX IF NOT EXISTS ix_eport_mail_ready ON eport_service_mail_queue(status,available_at);
CREATE OR REPLACE FUNCTION guard_eport_dispatcher_submission() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
 IF NEW.snapshot->>'pricingMode' IS DISTINCT FROM 'DISPATCHER' OR NEW.total IS NOT NULL OR NEW.status<>'PENDING_APPROVAL'
   OR NEW.payment_status<>'UNPAID' OR jsonb_array_length(COALESCE(NEW.snapshot->'fees','[]'::jsonb))<>0
 THEN RAISE EXCEPTION 'New service submissions must await Dispatcher pricing.' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS eport_dispatcher_submission ON eport_service_registrations;
CREATE TRIGGER eport_dispatcher_submission BEFORE INSERT ON eport_service_registrations FOR EACH ROW EXECUTE FUNCTION guard_eport_dispatcher_submission();
CREATE OR REPLACE FUNCTION guard_eport_bill_immutable() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
 RAISE EXCEPTION 'Published service bills are immutable' USING ERRCODE='23514';
END $$;
DROP TRIGGER IF EXISTS eport_bill_immutable ON eport_service_bills;
CREATE TRIGGER eport_bill_immutable BEFORE UPDATE OR DELETE ON eport_service_bills FOR EACH ROW EXECUTE FUNCTION guard_eport_bill_immutable();

-- Retain all identity/terminal guards from 040. New registrations settle against
-- the dispatcher's published bill; legacy registrations keep their original fee snapshot.
CREATE OR REPLACE FUNCTION guard_eport_service_payment_identity() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Service payment history cannot be deleted.' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' THEN
  IF ROW(NEW.id,NEW.draft_id,NEW.request_id,NEW.number,NEW.method,NEW.order_code,NEW.amount,NEW.currency,NEW.created_by,NEW.created_at)
   IS DISTINCT FROM ROW(OLD.id,OLD.draft_id,OLD.request_id,OLD.number,OLD.method,OLD.order_code,OLD.amount,OLD.currency,OLD.created_by,OLD.created_at)
   THEN RAISE EXCEPTION 'Service payment identity and confirmed amount are immutable.' USING ERRCODE='23514'; END IF;
  IF OLD.status IN ('SUCCEEDED','FAILED','CANCELLED') AND NEW IS DISTINCT FROM OLD THEN RAISE EXCEPTION 'Settled service payment history is immutable.' USING ERRCODE='23514'; END IF;
  IF OLD.payment_link_id IS NOT NULL AND NEW.payment_link_id IS DISTINCT FROM OLD.payment_link_id THEN RAISE EXCEPTION 'PayOS payment link identity is immutable.' USING ERRCODE='23514'; END IF;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM eport_service_registrations r JOIN eport_service_order_states o ON o.draft_id=r.draft_id
  LEFT JOIN eport_service_bills b ON b.draft_id=r.draft_id
  WHERE r.draft_id=NEW.draft_id AND r.currency=NEW.currency
   AND CASE WHEN r.snapshot->>'pricingMode'='DISPATCHER' THEN b.total=NEW.amount AND o.approval_status='APPROVED' ELSE r.total=NEW.amount END)
 THEN RAISE EXCEPTION 'Payment must match an authorized published service bill.' USING ERRCODE='23514'; END IF;
 RETURN NEW;
END $$;
INSERT INTO nexusport_schema_versions(version,description) VALUES('20261112_042','Dispatcher publishes immutable service bills; durable payment notice and receipt email queue') ON CONFLICT(version) DO NOTHING;
COMMIT;
