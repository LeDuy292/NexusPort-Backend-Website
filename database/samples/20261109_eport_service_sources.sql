-- Development examples explicitly requested for the e-Port service workflow.
-- Run only after migration 038, with:
--   SET nexusport.allow_service_samples = 'development';
-- This file never updates existing users, companies, sources, prices or visits.
BEGIN;

DO $$ BEGIN
 IF current_setting('nexusport.allow_service_samples',true) IS DISTINCT FROM 'development' THEN
  RAISE EXCEPTION 'Explicit development opt-in is required for e-Port sample data.';
 END IF;
 IF NOT EXISTS(SELECT 1 FROM nexusport_schema_versions WHERE version='20261109_038') THEN
  RAISE EXCEPTION 'Apply migration 20261109_038 before seeding service sources.';
 END IF;
END $$;

CREATE OR REPLACE FUNCTION pg_temp.eport_sample_id(label text) RETURNS uuid
LANGUAGE sql IMMUTABLE AS $$
 SELECT ('03810000-'||substr(md5(label),1,4)||'-4000-8000-'||substr(md5(label),5,12))::uuid;
$$;

-- Explicit JSON keys are intersected with the target schema so this development
-- file supports canonical tables and the retained legacy informational columns.
-- Omitted columns retain their real defaults. Existing sample identities must
-- match their explicit ownership; a collision never adopts an unrelated row.
CREATE OR REPLACE FUNCTION pg_temp.eport_sample_insert(target text, data jsonb, identity_keys text[]) RETURNS boolean
LANGUAGE plpgsql AS $$
DECLARE cols text; selected text; previous jsonb; identity_key text; inserted integer;
BEGIN
 IF target NOT IN ('container_types','containers','container_declarations','export_voyages',
                   'export_bookings','export_booking_containers','eport_tariffs','eport_catalog_audit') THEN
  RAISE EXCEPTION 'Unsupported sample target.';
 END IF;
 IF data->>'id' NOT LIKE '03810000-%' THEN RAISE EXCEPTION 'Unexpected sample identity.'; END IF;
 EXECUTE format('SELECT to_jsonb(t) FROM public.%I t WHERE id=$1',target)
  INTO previous USING (data->>'id')::uuid;
 IF previous IS NOT NULL THEN
  FOREACH identity_key IN ARRAY identity_keys LOOP
   IF previous ? identity_key AND previous->identity_key IS DISTINCT FROM data->identity_key THEN
    RAISE EXCEPTION 'Sample identity collision in %.%',target,identity_key;
   END IF;
  END LOOP;
  RETURN false;
 END IF;
 SELECT string_agg(format('%I',c.column_name),',' ORDER BY c.ordinal_position),
        string_agg(format('s.%I',c.column_name),',' ORDER BY c.ordinal_position)
 INTO cols,selected
 FROM information_schema.columns c
 WHERE c.table_schema='public' AND c.table_name=target AND data ? c.column_name;
 EXECUTE format('INSERT INTO public.%I(%s) SELECT %s FROM jsonb_populate_record(NULL::public.%I,$1) s ON CONFLICT(id) DO NOTHING',
                target,cols,selected,target) USING data;
 GET DIAGNOSTICS inserted=ROW_COUNT;
 RETURN inserted=1;
END $$;

-- ISO 6346 check digit for the fictional NXPU development identity prefix.
CREATE OR REPLACE FUNCTION pg_temp.eport_sample_container(serial integer) RETURNS text
LANGUAGE plpgsql IMMUTABLE AS $$
DECLARE base text:='NXPU'||lpad(serial::text,6,'0'); total integer:=0; i integer; v integer;
BEGIN
 IF serial<100000 OR serial>999999 THEN RAISE EXCEPTION 'Invalid sample container serial.'; END IF;
 FOR i IN 1..10 LOOP
  IF i<=4 THEN v:=CASE substr(base,i,1) WHEN 'N' THEN 25 WHEN 'X' THEN 36 WHEN 'P' THEN 27 WHEN 'U' THEN 32 END;
  ELSE v:=substr(base,i,1)::integer; END IF;
  total:=total+v*(2^(i-1))::integer;
 END LOOP;
 RETURN base||((total%11)%10)::text;
END $$;

DO $$
DECLARE
 companies integer; owner record; line record; spec record; price record;
 line_users uuid[]; line_names text[]; line_native uuid[];
 admin_actor uuid; voyage_id uuid; booking_id uuid; physical_id uuid; declaration_id uuid; occurrence_id uuid;
 tariff_id uuid; type_id uuid; container_no text; booking_no text; company_tag text;
 line_user uuid; native_line uuid; line_name text; serial_base integer; i integer; j integer;
 type_code text; iso_code text; size_code text; category_code text; cargo text;
 weight numeric; temperature numeric; power_kw numeric; seal text; inserted boolean;
 horizon timestamptz:=now(); end_date date:=(now()+interval '90 days')::date;
BEGIN
 SELECT count(*) INTO companies FROM (
  SELECT DISTINCT cu.carrier_id FROM carrier_users cu JOIN users u ON u.id=cu.user_id JOIN carriers c ON c.id=cu.carrier_id
  WHERE u.is_active AND u.role IN ('Transport Company','Carrier','Carrier Staff')
   AND (SELECT count(*) FROM carrier_users m WHERE m.user_id=u.id)=1
   AND COALESCE(to_jsonb(c)->>'status','active')='active'
 ) q;
 IF companies NOT BETWEEN 1 AND 10 THEN RAISE EXCEPTION 'Expected 1–10 eligible development companies; choose a narrower seed target.'; END IF;

 SELECT array_agg(q.id ORDER BY q.preference,q.username),array_agg(q.display_name ORDER BY q.preference,q.username),
        array_agg(q.native_id ORDER BY q.preference,q.username)
 INTO line_users,line_names,line_native FROM (
  SELECT u.id,u.username,COALESCE(u.full_name,u.username) AS display_name,
   CASE u.username WHEN 'demo.shipping.bluewave' THEN 1 WHEN 'demo.shipping.eastwind' THEN 2 WHEN 'demo.shipping.harborstar' THEN 3 ELSE 4 END AS preference,
   (SELECT s.id FROM shipping_lines s WHERE s.id=NULLIF(to_jsonb(u)->>'shipping_line_id','')::uuid) AS native_id
  FROM users u WHERE u.is_active AND u.role='Shipping Line'
   AND EXISTS(SELECT 1 FROM shipping_lines s WHERE s.id=NULLIF(to_jsonb(u)->>'shipping_line_id','')::uuid)
 ) q;
 IF COALESCE(array_length(line_users,1),0)<3 THEN RAISE EXCEPTION 'Three active Shipping Line accounts linked to native shipping-line profiles are required; no account will be created or reset.'; END IF;
 SELECT id INTO admin_actor FROM users WHERE is_active AND role IN ('Administrator','Admin') ORDER BY username,id LIMIT 1;
 IF admin_actor IS NULL THEN RAISE EXCEPTION 'An active administrator is required to attribute development tariff configuration.'; END IF;

 FOR i IN 1..3 LOOP
  j:=1+((i-1)%array_length(line_users,1)); line_user:=line_users[j]; line_name:=line_names[j]; native_line:=line_native[j];
  voyage_id:=pg_temp.eport_sample_id('voyage:'||i);
  PERFORM pg_temp.eport_sample_insert('export_voyages',jsonb_build_object(
   'id',voyage_id,'line_user_id',line_user,'vessel_name','DEMO EPORT '||CASE i WHEN 1 THEN 'BLUEWAVE' WHEN 2 THEN 'EASTWIND' ELSE 'HARBORSTAR' END,
   'voyage_number','DEMO-EPORT-00'||i||'E','port_of_loading','NexusPort Demo Terminal',
   'port_of_discharge',CASE i WHEN 1 THEN 'Singapore' WHEN 2 THEN 'Hong Kong' ELSE 'Shanghai' END,
   'receive_from',horizon-interval '1 day','cy_cutoff',horizon+(15+i*15)*interval '1 day',
   'vgm_cutoff',horizon+(14+i*15)*interval '1 day','si_cutoff',horizon+(13+i*15)*interval '1 day',
   'etd',horizon+(17+i*15)*interval '1 day','eta',horizon+(24+i*15)*interval '1 day','status','Scheduled'),
   ARRAY['line_user_id','voyage_number']);
 END LOOP;

 FOR owner IN
  SELECT DISTINCT ON(cu.carrier_id) cu.carrier_id,u.id AS actor,c.company_name
  FROM carrier_users cu JOIN users u ON u.id=cu.user_id JOIN carriers c ON c.id=cu.carrier_id
  WHERE u.is_active AND u.role IN ('Transport Company','Carrier','Carrier Staff')
   AND (SELECT count(*) FROM carrier_users m WHERE m.user_id=u.id)=1
   AND COALESCE(to_jsonb(c)->>'status','active')='active'
  ORDER BY cu.carrier_id,CASE WHEN u.role IN ('Transport Company','Carrier') THEN 0 ELSE 1 END,u.username,u.id
 LOOP
  company_tag:=substr(replace(owner.carrier_id::text,'-',''),1,8);
  serial_base:=900000+((('x'||substr(md5(owner.carrier_id::text),1,6))::bit(24)::integer)%10000)*6;
  FOR i IN 1..3 LOOP
   j:=1+((i-1)%array_length(line_users,1)); line_user:=line_users[j]; native_line:=line_native[j];
   voyage_id:=pg_temp.eport_sample_id('voyage:'||i);
   booking_id:=pg_temp.eport_sample_id('booking:'||owner.carrier_id||':'||i);
   booking_no:='DEMO-EPORT-'||upper(company_tag)||'-00'||i;
   PERFORM pg_temp.eport_sample_insert('export_bookings',jsonb_build_object(
    'id',booking_id,'carrier_id',owner.carrier_id,'line_user_id',line_user,
    'request_id',pg_temp.eport_sample_id('request:'||owner.carrier_id||':'||i),'request_hash',encode(sha256(convert_to('DEMO-EPORT:'||owner.carrier_id||':'||i,'UTF8')),'hex'),
    'voyage_id',voyage_id,'booking_no',booking_no,'booking_number',booking_no,'container_type_code','MIXED','quantity',2,
    'commodity_description','DEMO e-Port: '||CASE i WHEN 1 THEN 'Hàng khô 20/40 feet' WHEN 2 THEN 'Container lạnh 20/40 feet' ELSE 'Hàng nguy hiểm và container 45 feet' END,
    'status','ContainersDeclared','port_status','Accepted','created_by',owner.actor,
    'shipping_line_id',COALESCE(native_line,line_user)::text,'shipper_customer_id',owner.carrier_id::text,
    'shipper_name','DEMO - '||owner.company_name,'port_of_discharge',CASE i WHEN 1 THEN 'Singapore' WHEN 2 THEN 'Hong Kong' ELSE 'Shanghai' END,
    'equipment_size_type',NULL,'required_grade',NULL,'allocation_mode',NULL,'total_qty',2,
    'picked_up_empty_qty',0,'gated_in_full_qty',0,'allocated_seal_prefix','DEMO-SEAL',
    'booking_valid_until',horizon+(15+i*15)*interval '1 day','allow_auto_late_gate_in',false),
    ARRAY['carrier_id','line_user_id','booking_no','created_by']);
  END LOOP;

  FOR j IN 1..6 LOOP
   i:=1+(j-1)/2;
   line_user:=line_users[1+((i-1)%array_length(line_users,1))]; native_line:=line_native[1+((i-1)%array_length(line_users,1))];
   type_code:=CASE j WHEN 1 THEN '20GP' WHEN 2 THEN '40GP' WHEN 3 THEN '20RF' WHEN 4 THEN '40RH' WHEN 5 THEN '20GP' ELSE '45HC' END;
   iso_code:=CASE j WHEN 1 THEN '22G1' WHEN 2 THEN '42G1' WHEN 3 THEN '22R1' WHEN 4 THEN '45R1' WHEN 5 THEN '22G1' ELSE 'L5G1' END;
   size_code:=CASE WHEN j IN(1,3,5) THEN 'ft20' WHEN j IN(2,4) THEN 'ft40' ELSE 'ft45' END;
   category_code:=CASE WHEN j IN(3,4) THEN 'reefer' ELSE 'dry' END;
   cargo:=CASE WHEN j IN(3,4) THEN 'reefer' WHEN j=5 THEN 'dangerous' ELSE 'general' END;
   weight:=CASE j WHEN 1 THEN 21500 WHEN 2 THEN 27800 WHEN 3 THEN 22400 WHEN 4 THEN 28100 WHEN 5 THEN 18900 ELSE 26300 END;
   temperature:=CASE j WHEN 3 THEN -18 WHEN 4 THEN 4 ELSE NULL END;
   power_kw:=CASE WHEN j IN(3,4) THEN 5.5 ELSE NULL END;
   SELECT t.id INTO type_id FROM container_types t WHERE t.code=type_code;
   IF type_id IS NULL THEN
    type_id:=pg_temp.eport_sample_id('type:'||type_code);
    PERFORM pg_temp.eport_sample_insert('container_types',jsonb_build_object('id',type_id,'code',type_code,'size',size_code,'category',category_code,
      'description','DEMO e-Port reference equipment '||type_code),ARRAY['code','size','category']);
   ELSE
    IF NOT EXISTS(SELECT 1 FROM container_types t WHERE t.id=type_id AND t.size::text=size_code AND t.category::text=category_code) THEN
     RAISE EXCEPTION 'Existing equipment type % has incompatible size/category; no changes performed.',type_code;
    END IF;
   END IF;
   physical_id:=pg_temp.eport_sample_id('container:'||owner.carrier_id||':'||j);
   declaration_id:=pg_temp.eport_sample_id('declaration:'||owner.carrier_id||':'||j);
   occurrence_id:=pg_temp.eport_sample_id('occurrence:'||owner.carrier_id||':'||j);
   booking_id:=pg_temp.eport_sample_id('booking:'||owner.carrier_id||':'||i);
   container_no:=pg_temp.eport_sample_container(serial_base+j-1);
   seal:='DEMO-SEAL-'||upper(company_tag)||'-'||j;
   PERFORM pg_temp.eport_sample_insert('containers',jsonb_build_object('id',physical_id,'container_no',container_no,
    'container_type_id',type_id,'status','expected','is_reefer',j IN(3,4),'shipping_line_id',native_line,
    'iso_size_type',iso_code,'equipment_description','DEMO e-Port sample - '||type_code),
    ARRAY['container_no','container_type_id','shipping_line_id']);
   PERFORM pg_temp.eport_sample_insert('container_declarations',jsonb_build_object('id',declaration_id,'container_id',physical_id,
    'declared_by',owner.actor,'carrier_id',owner.carrier_id,'load_status','full','cargo_type',cargo,
    'gross_weight_kg',weight,'required_power_kw',power_kw,'customer_name','DEMO - '||owner.company_name,'seal_number',seal,'data_status',NULL),
    ARRAY['container_id','carrier_id','load_status','cargo_type']);
   PERFORM pg_temp.eport_sample_insert('export_booking_containers',jsonb_build_object('id',occurrence_id,'export_booking_id',booking_id,
    'container_id',physical_id,'declaration_id',declaration_id,'container_no',container_no,'type_code',type_code,'seal_number',seal,
    'vgm_kg',weight,'cargo_type',cargo,'target_temperature',temperature,'un_number',CASE WHEN j=5 THEN '1203' ELSE NULL END,
    'dg_class',CASE WHEN j=5 THEN '3' ELSE NULL END,'customs_cleared',false,'status','Expected','active',true),
    ARRAY['export_booking_id','container_id','declaration_id','container_no']);
  END LOOP;
 END LOOP;

 FOR price IN SELECT * FROM (VALUES
  ('EXPORT_DROPOFF','20FT','DRY',300000::numeric),('EXPORT_DROPOFF','40FT','DRY',500000::numeric),
  ('EXPORT_DROPOFF','20FT','REEFER',300000::numeric),('EXPORT_DROPOFF','40FT','REEFER',500000::numeric),
  ('EXPORT_DROPOFF','20FT','DG',300000::numeric),('EXPORT_DROPOFF','45FT','DRY',600000::numeric),
  ('REEFER_POWER','ALL','ALL',1000::numeric),('YARD_RELOCATION','ALL','ALL',20000::numeric),('STORAGE_EXTENSION','ALL','ALL',7000::numeric)
 ) p(code,size,equipment,amount)
 LOOP
  FOR spec IN SELECT id,unit FROM eport_services WHERE code=price.code AND is_active FOR UPDATE LOOP
   -- Preserve every existing active rate. Add only an uncovered scope/date span.
   IF NOT EXISTS(SELECT 1 FROM eport_tariffs t WHERE t.service_id=spec.id AND t.is_active
       AND (t.size=price.size OR (price.size<>'ALL' AND t.size='ALL'))
       AND (t.equipment=price.equipment OR (price.equipment<>'ALL' AND t.equipment='ALL')) AND t.effective_from<=end_date
       AND COALESCE(t.effective_to,'infinity'::date)>=current_date) THEN
    tariff_id:=pg_temp.eport_sample_id('tariff:'||price.code||':'||price.size||':'||price.equipment);
    inserted:=pg_temp.eport_sample_insert('eport_tariffs',jsonb_build_object('id',tariff_id,'service_id',spec.id,'size',price.size,
     'equipment',price.equipment,'amount',price.amount,'currency','VND','unit',spec.unit,'effective_from',current_date,'effective_to',end_date,
     'is_active',true,'note','DEMO - dữ liệu thử nghiệm e-Port, không phải biểu phí cảng.','created_by',admin_actor),
     ARRAY['service_id','size','equipment','amount','created_by']);
    IF inserted THEN
     PERFORM pg_temp.eport_sample_insert('eport_catalog_audit',jsonb_build_object('id',pg_temp.eport_sample_id('tariff-audit:'||price.code||':'||price.size||':'||price.equipment),
      'entity_id',spec.id,'actor_id',admin_actor,'action','DEMO_TARIFF_CREATED','after_data',jsonb_build_object('tariffId',tariff_id,'amount',price.amount,'sample',true)),
      ARRAY['entity_id','actor_id','action']);
    END IF;
   END IF;
  END LOOP;
 END LOOP;
END $$;

SELECT json_build_object(
 'sampleVoyages',(SELECT count(*) FROM export_voyages WHERE id::text LIKE '03810000-%'),
 'sampleBookings',(SELECT count(*) FROM export_bookings WHERE id::text LIKE '03810000-%'),
 'sampleContainers',(SELECT count(*) FROM containers WHERE id::text LIKE '03810000-%'),
 'sampleDeclarations',(SELECT count(*) FROM container_declarations WHERE id::text LIKE '03810000-%'),
 'samplePublishedContainers',(SELECT count(*) FROM export_booking_containers WHERE id::text LIKE '03810000-%'),
 'sampleTariffs',(SELECT count(*) FROM eport_tariffs WHERE id::text LIKE '03810000-%')
) AS development_sample_summary;
COMMIT;
