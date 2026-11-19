ALTER TABLE eport_service_payments
 ADD COLUMN IF NOT EXISTS transfer_amount numeric(24,0)
 CHECK (transfer_amount IS NULL OR transfer_amount > 0 AND transfer_amount <= 9007199254740991);

CREATE OR REPLACE FUNCTION guard_eport_service_payment_identity() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
 IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Service payment history cannot be deleted.' USING ERRCODE='23514'; END IF;
 IF TG_OP='UPDATE' THEN
  IF ROW(NEW.id,NEW.draft_id,NEW.request_id,NEW.number,NEW.method,NEW.order_code,NEW.amount,NEW.currency,NEW.created_by,NEW.created_at)
   IS DISTINCT FROM ROW(OLD.id,OLD.draft_id,OLD.request_id,OLD.number,OLD.method,OLD.order_code,OLD.amount,OLD.currency,OLD.created_by,OLD.created_at)
   THEN RAISE EXCEPTION 'Service payment identity and confirmed amount are immutable.' USING ERRCODE='23514'; END IF;
  IF NEW.transfer_amount IS DISTINCT FROM OLD.transfer_amount AND NOT (OLD.transfer_amount IS NULL AND OLD.status='PENDING' AND NEW.transfer_amount>0)
   THEN RAISE EXCEPTION 'PayOS transfer amount is immutable.' USING ERRCODE='23514'; END IF;
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

INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261119_043','Separate immutable service bill total from PayOS transfer amount')
ON CONFLICT(version) DO NOTHING;
