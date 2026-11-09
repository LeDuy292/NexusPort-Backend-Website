-- A pickup code is a transferable capability for one stay, not a new stay ID.
BEGIN;
CREATE OR REPLACE FUNCTION yard_new_pickup_code() RETURNS text LANGUAGE sql VOLATILE AS $$
 SELECT 'YARD-' || upper(substr(h,1,4)||'-'||substr(h,5,4)||'-'||substr(h,9,4))
 FROM (SELECT replace(gen_random_uuid()::text,'-','') AS h) s
$$;
ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS pickup_code text NOT NULL DEFAULT yard_new_pickup_code();
CREATE UNIQUE INDEX IF NOT EXISTS ux_visit_pickup_code ON container_visits(pickup_code);
COMMENT ON COLUMN container_visits.pickup_code IS 'Private transferable pickup capability. Never project through shared container lookup. Historical visit_reference stays unchanged.';

CREATE OR REPLACE FUNCTION yard_prepare_pickup(p_container uuid,p_code text,p_carrier uuid)
RETURNS TABLE("DeclarationId" uuid,"ContainerVisitId" uuid) LANGUAGE plpgsql AS $$
DECLARE v container_visits%ROWTYPE; new_declaration uuid;
BEGIN
 -- Same lock order as reservation validation; all writes roll back with booking.
 PERFORM 1 FROM containers WHERE id=p_container FOR UPDATE;
 SELECT * INTO v FROM container_visits WHERE container_id=p_container
  AND pickup_code=upper(trim(p_code)) AND visit_status IN ('planned','active') FOR UPDATE;
 IF NOT FOUND OR NOT EXISTS(SELECT 1 FROM booking_containers bc JOIN bookings b ON b.id=bc.booking_id
  WHERE bc.container_visit_id=v.id AND b.booking_type::text='dropoff'
   AND b.status::text NOT IN ('canceled','rejected','expired')) THEN
  RAISE EXCEPTION 'Mã lượt bãi không hợp lệ với container hoặc lượt đã kết thúc.' USING ERRCODE='23514';
 END IF;
 -- Preserve the inbound cargo snapshot; never ask the receiving company to
 -- guess/redeclare another company's cargo or overwrite the physical master.
 INSERT INTO container_declarations(container_id,carrier_id,load_status,cargo_type,
  gross_weight_kg,required_power_kw,customer_name,seal_number)
 SELECT p_container,p_carrier,d.load_status,d.cargo_type,d.gross_weight_kg,
  d.required_power_kw,d.customer_name,d.seal_number FROM container_declarations d WHERE d.id=v.declaration_id
 RETURNING id INTO new_declaration;
 IF new_declaration IS NULL THEN
  RAISE EXCEPTION 'Lượt bãi thiếu thông tin giao nhận gốc.' USING ERRCODE='23514';
 END IF;
 INSERT INTO container_visit_access(container_visit_id,carrier_id) VALUES(v.id,p_carrier) ON CONFLICT DO NOTHING;
 RETURN QUERY SELECT new_declaration,v.id;
END $$;
INSERT INTO nexusport_schema_versions(version,description) VALUES('20261017_012','Transferable pickup codes per yard stay') ON CONFLICT DO NOTHING;
COMMIT;
