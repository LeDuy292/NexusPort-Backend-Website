-- Explicitly requested Development-only input data. No existing row is updated.
BEGIN;
DO $$ BEGIN
 IF current_setting('nexusport.allow_service_samples',true) IS DISTINCT FROM 'development' THEN
  RAISE EXCEPTION 'Explicit Development opt-in required';
 END IF;
 IF NOT EXISTS(SELECT 1 FROM nexusport_schema_versions WHERE version='20261110_041') THEN
  RAISE EXCEPTION 'Migration041 must be installed first';
 END IF;
 IF COALESCE(NULLIF(current_setting('nexusport.import_demo_username',true),''),'dauboquay') NOT IN ('dauboquay','carrier01') THEN
  RAISE EXCEPTION 'Unknown demo profile; no company access changed';
 END IF;
END $$;
CREATE OR REPLACE FUNCTION pg_temp.import_demo_username() RETURNS text LANGUAGE sql STABLE AS $$
 SELECT COALESCE(NULLIF(current_setting('nexusport.import_demo_username',true),''),'dauboquay')
$$;
CREATE OR REPLACE FUNCTION pg_temp.import_demo_prefix() RETURNS text LANGUAGE sql STABLE AS $$
 SELECT CASE pg_temp.import_demo_username() WHEN 'carrier01' THEN 'e9111101' ELSE 'e9111100' END
$$;
CREATE OR REPLACE FUNCTION pg_temp.import_demo_id(n integer) RETURNS uuid LANGUAGE sql STABLE AS $$
 SELECT (pg_temp.import_demo_prefix()||'-0000-4000-8000-'||lpad(n::text,12,'0'))::uuid
$$;
CREATE OR REPLACE FUNCTION pg_temp.import_demo_insert(target text,data jsonb,identity_keys text[]) RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE cols text; selected text; previous jsonb; key text; inserted integer;
BEGIN
 IF target NOT IN ('containers','container_declarations','container_visits','eport_tariffs','eport_catalog_audit') OR data->>'id' NOT LIKE pg_temp.import_demo_prefix()||'-%' THEN RAISE EXCEPTION 'Invalid demo target/identity'; END IF;
 EXECUTE format('SELECT to_jsonb(t) FROM public.%I t WHERE id=$1',target) INTO previous USING (data->>'id')::uuid;
 IF previous IS NOT NULL THEN
  FOREACH key IN ARRAY identity_keys LOOP
   IF previous->key IS DISTINCT FROM data->key THEN RAISE EXCEPTION 'Demo identity collision %.%',target,key; END IF;
  END LOOP;
  RETURN false;
 END IF;
 SELECT string_agg(format('%I',c.column_name),',' ORDER BY c.ordinal_position),string_agg(format('s.%I',c.column_name),',' ORDER BY c.ordinal_position)
 INTO cols,selected FROM information_schema.columns c WHERE c.table_schema='public' AND c.table_name=target AND data ? c.column_name;
 EXECUTE format('INSERT INTO public.%I(%s) SELECT %s FROM jsonb_populate_record(NULL::public.%I,$1) s ON CONFLICT(id) DO NOTHING',target,cols,selected,target) USING data;
 GET DIAGNOSTICS inserted=ROW_COUNT; RETURN inserted=1;
END $$;
CREATE OR REPLACE FUNCTION pg_temp.import_demo_number(serial integer) RETURNS text LANGUAGE plpgsql IMMUTABLE AS $$
DECLARE base text:='DEMU'||lpad(serial::text,6,'0'); total integer:=0; v integer; i integer;
BEGIN
 FOR i IN 1..10 LOOP
  IF i<=4 THEN v:=CASE substr(base,i,1) WHEN 'D' THEN 14 WHEN 'E' THEN 15 WHEN 'M' THEN 24 WHEN 'U' THEN 32 END;
  ELSE v:=substr(base,i,1)::integer; END IF;
  total:=total+v*(2^(i-1))::integer;
 END LOOP;
 RETURN base||((total%11)%10)::text;
END $$;
DO $$
DECLARE owner_id uuid; company_id uuid; company_name text; admin_id uuid; type_id uuid; size_code text; equipment text;
 i integer; physical uuid; declaration uuid; visit uuid; number text; cargo text; power numeric; rate record; service record; inserted boolean;
 profile_suffix text:=CASE WHEN pg_temp.import_demo_username()='carrier01' THEN '-CARRIER01' ELSE '' END;
BEGIN
 SELECT id INTO owner_id FROM users WHERE to_jsonb(users)->>'username'=pg_temp.import_demo_username() AND is_active AND role IN ('Transport Company','Carrier','Carrier Staff');
 IF owner_id IS NULL OR (SELECT count(*) FROM carrier_users WHERE user_id=owner_id)<>1 THEN RAISE EXCEPTION 'Exactly one active company required for demo profile %',pg_temp.import_demo_username(); END IF;
 SELECT cu.carrier_id,c.company_name INTO company_id,company_name FROM carrier_users cu JOIN carriers c ON c.id=cu.carrier_id WHERE cu.user_id=owner_id;
 SELECT id INTO admin_id FROM users WHERE is_active AND role IN ('Administrator','Admin') ORDER BY id LIMIT 1;
 IF admin_id IS NULL THEN RAISE EXCEPTION 'Existing admin attribution required; no accounts created'; END IF;
 PERFORM pg_advisory_xact_lock(hashtext('nexusport-demo-import-20261111'));
 FOR i IN 1..4 LOOP
  size_code:=CASE WHEN i=3 THEN '40FT' ELSE '20FT' END;
  equipment:=CASE WHEN i=4 THEN 'REEFER' ELSE 'DRY' END;
  SELECT t.id INTO type_id FROM container_types t
   WHERE lower(t.size::text) IN (lower(size_code),CASE WHEN size_code='40FT' THEN 'ft40' ELSE 'ft20' END)
    AND lower(t.category::text)=CASE WHEN equipment='REEFER' THEN 'reefer' ELSE 'dry' END
   ORDER BY CASE WHEN t.code IN ('20GP','40GP','20RF') THEN 0 ELSE 1 END,t.id LIMIT 1;
  IF type_id IS NULL THEN RAISE EXCEPTION 'Existing container type missing for % %',size_code,equipment; END IF;
  physical:=pg_temp.import_demo_id(100+i);declaration:=pg_temp.import_demo_id(200+i);visit:=pg_temp.import_demo_id(300+i);
  number:=pg_temp.import_demo_number((CASE WHEN profile_suffix='' THEN 611100 ELSE 611200 END)+i);cargo:=CASE WHEN i=4 THEN 'reefer' ELSE 'general' END;power:=CASE WHEN i=4 THEN 5 ELSE NULL END;
  PERFORM pg_temp.import_demo_insert('containers',jsonb_build_object('id',physical,'container_no',number,'container_type_id',type_id,
   'status','in_yard','is_reefer',i=4,'tare_weight_kg',CASE WHEN i=3 THEN 3800 ELSE 2300 END,'max_gross_mass_kg',30480,
   'iso_size_type',CASE WHEN i=4 THEN '22R1' WHEN i=3 THEN '42G1' ELSE '22G1' END),ARRAY['container_no','container_type_id']);
  PERFORM pg_temp.import_demo_insert('container_declarations',jsonb_build_object('id',declaration,'container_id',physical,
   'carrier_id',company_id,'declared_by',admin_id,'load_status','full','cargo_type',cargo,'gross_weight_kg',CASE WHEN i=3 THEN 27000 ELSE 22000 END,
   'required_power_kw',power,'customer_name','DEMO IMPORT - '||company_name,'seal_number','DEMO-IMP-0'||i),ARRAY['container_id','carrier_id','load_status','cargo_type']);
  PERFORM pg_temp.import_demo_insert('container_visits',jsonb_build_object('id',visit,'visit_reference','DEMO-SVC-IMPORT'||profile_suffix||'-0'||i,
   'container_id',physical,'declaration_id',declaration,'visit_status','active','load_status','full','cargo_type',cargo,
   'source_type','port_vessel','movement_type','vessel_discharge','data_status','verified','source_reference','DEMO-EPORT-IMPORT-20261111'||profile_suffix,
   'bl_booking_no','DEMO-BL-IMPORT-20261111'||profile_suffix,'customer_name','DEMO IMPORT - '||company_name,'declared_by',admin_id,
   'seal_number','DEMO-IMP-0'||i,'gross_weight_kg',CASE WHEN i=3 THEN 27000 ELSE 22000 END,'required_power_kw',power,
   'started_at',now()-interval '1 day','expected_arrival_at',now()-interval '2 days','expected_available_at',now()-interval '12 hours',
   'valid_to',now()+interval '30 days'),ARRAY['container_id','declaration_id','source_type','movement_type','source_reference']);
  INSERT INTO container_visit_access(container_visit_id,carrier_id,granted_by) VALUES(visit,company_id,admin_id) ON CONFLICT DO NOTHING;
 END LOOP;
 -- Only add missing Development example tariffs; never rewrite an existing rate.
 FOR rate IN SELECT * FROM (VALUES (1,'20FT','DRY',300000),(2,'40FT','DRY',500000),(3,'20FT','REEFER',300000)) p(n,size,equipment,amount) LOOP
  SELECT id,unit INTO service FROM eport_services WHERE code='IMPORT_PICKUP' AND is_active FOR UPDATE;
  IF service.id IS NULL THEN RAISE EXCEPTION 'IMPORT_PICKUP must already be active'; END IF;
  IF NOT EXISTS(SELECT 1 FROM eport_tariffs t WHERE t.service_id=service.id AND t.is_active
    AND t.size IN ('ALL',rate.size) AND t.equipment IN ('ALL',rate.equipment)
    AND t.effective_from<=current_date+30 AND COALESCE(t.effective_to,'infinity'::date)>=current_date) THEN
   inserted:=pg_temp.import_demo_insert('eport_tariffs',jsonb_build_object('id',pg_temp.import_demo_id(400+rate.n),
    'service_id',service.id,'size',rate.size,'equipment',rate.equipment,'amount',rate.amount,'unit',service.unit,'currency','VND',
    'effective_from',current_date,'effective_to',current_date+30,'is_active',true,'created_by',admin_id,
    'note','DEMO IMPORT - phí thử nghiệm theo yêu cầu, không phải biểu phí cảng chính thức.'),ARRAY['service_id','size','equipment','amount']);
   IF inserted THEN
    PERFORM pg_temp.import_demo_insert('eport_catalog_audit',jsonb_build_object('id',pg_temp.import_demo_id(500+rate.n),
     'entity_id',service.id,'actor_id',admin_id,'action','DEMO_TARIFF_CREATED','after_data',jsonb_build_object('tariffId',pg_temp.import_demo_id(400+rate.n),'sample',true,'amount',rate.amount)),ARRAY['entity_id','actor_id','action']);
   END IF;
  END IF;
 END LOOP;
END $$;
SELECT jsonb_build_object('sample','DEMO-EPORT-IMPORT-20261111','targetUsername',pg_temp.import_demo_username(),
 'containers',(SELECT jsonb_agg(jsonb_build_object('number',c.container_no,'type',t.code,'seal',v.seal_number,'visitId',v.id,'validUntil',v.valid_to) ORDER BY c.container_no)
 FROM container_visits v JOIN containers c ON c.id=v.container_id JOIN container_types t ON t.id=c.container_type_id WHERE v.id::text LIKE pg_temp.import_demo_prefix()||'-%'),
 'existingRowsUpdated',false,'customsClearanceGranted',false)::text;
COMMIT;
