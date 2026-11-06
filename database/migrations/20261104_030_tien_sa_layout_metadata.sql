BEGIN;
ALTER TABLE yard_blocks
 ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT true,
 ADD COLUMN IF NOT EXISTS layout_version text,
 ADD COLUMN IF NOT EXISTS bay_modules integer,
 ADD COLUMN IF NOT EXISTS row_count integer,
 ADD COLUMN IF NOT EXISTS tier_count integer;
ALTER TABLE yard_slots
 ADD COLUMN IF NOT EXISTS container_length_ft smallint,
 ADD COLUMN IF NOT EXISTS module_start integer,
 ADD COLUMN IF NOT EXISTS module_span integer;
CREATE INDEX IF NOT EXISTS ix_yard_slots_module_footprint ON yard_slots(block_id,row_no,tier,module_start);

-- Configured slots are disjoint physical envelopes, rather than overlapping
-- 20ft and 40ft alternatives. Forty-five-foot slots reserve extra end clearance.
CREATE OR REPLACE FUNCTION guard_tien_sa_slot_footprint() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE b yard_blocks;
BEGIN
 IF NEW.module_start IS NULL AND NEW.module_span IS NULL AND NEW.container_length_ft IS NULL THEN RETURN NEW; END IF;
 SELECT * INTO b FROM yard_blocks WHERE id=NEW.block_id;
 IF NEW.module_start IS NULL OR NEW.module_span IS NULL OR NEW.container_length_ft IS NULL
  OR NEW.module_start<1 OR NEW.module_span<1 OR b.bay_modules IS NULL
  OR NEW.module_start+NEW.module_span-1>b.bay_modules
  OR NEW.row_no<1 OR NEW.row_no>b.row_count OR NEW.tier<1 OR NEW.tier>b.tier_count
  OR NOT ((NEW.container_length_ft=20 AND NEW.module_span=1 AND NEW.bay=2*NEW.module_start-1)
    OR (NEW.container_length_ft=40 AND NEW.module_span=2 AND NEW.bay=2*NEW.module_start)
    OR (NEW.container_length_ft=45 AND NEW.module_span=4 AND NEW.bay=2*NEW.module_start+2))
 THEN RAISE EXCEPTION 'Invalid Tiên Sa slot footprint' USING ERRCODE='23514'; END IF;
 PERFORM pg_advisory_xact_lock(hashtext(NEW.block_id::text||':'||NEW.row_no||':'||NEW.tier));
 IF EXISTS(SELECT 1 FROM yard_slots s WHERE s.id<>NEW.id AND s.block_id=NEW.block_id
  AND s.row_no=NEW.row_no AND s.tier=NEW.tier AND s.module_start IS NOT NULL
  AND s.module_start<NEW.module_start+NEW.module_span AND NEW.module_start<s.module_start+s.module_span)
 THEN RAISE EXCEPTION 'Overlapping Tiên Sa slot footprints' USING ERRCODE='23P01'; END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_tien_sa_slot_footprint ON yard_slots;
CREATE TRIGGER trg_tien_sa_slot_footprint BEFORE INSERT OR UPDATE OF block_id,bay,row_no,tier,module_start,module_span,container_length_ft
 ON yard_slots FOR EACH ROW EXECUTE FUNCTION guard_tien_sa_slot_footprint();
INSERT INTO nexusport_schema_versions(version,description)
 VALUES('20261104_030','Tiên Sa layout version, active block directory and non-overlapping physical slot envelopes') ON CONFLICT DO NOTHING;
COMMIT;
