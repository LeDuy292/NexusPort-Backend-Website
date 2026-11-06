BEGIN;

-- NXP-114: versioned road topology, live condition overlays and exact trip progress.
-- Existing operational identities, reservations and physical occupancy are preserved.
CREATE TABLE IF NOT EXISTS smart_yard_maps (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 version varchar(120) NOT NULL UNIQUE,
 name varchar(200) NOT NULL,
 map_data jsonb NOT NULL CHECK(jsonb_typeof(map_data)='object'),
 is_active boolean NOT NULL DEFAULT false,
 source varchar(40) NOT NULL DEFAULT 'configuration',
 updated_by uuid REFERENCES users(id) ON DELETE SET NULL,
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_smart_yard_map_active ON smart_yard_maps(is_active) WHERE is_active;

CREATE TABLE IF NOT EXISTS smart_yard_conditions (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 map_id uuid NOT NULL REFERENCES smart_yard_maps(id) ON DELETE CASCADE,
 target_type varchar(10) NOT NULL CHECK(target_type IN ('node','edge')),
 target_id varchar(120) NOT NULL,
 restricted boolean NOT NULL DEFAULT false,
 congestion numeric(4,3) NOT NULL DEFAULT 0 CHECK(congestion BETWEEN 0 AND 1),
 source varchar(40) NOT NULL DEFAULT 'dispatcher',
 valid_until timestamptz,
 observed_at timestamptz NOT NULL DEFAULT now(),
 updated_by uuid REFERENCES users(id) ON DELETE SET NULL,
 UNIQUE(map_id,target_type,target_id)
);
CREATE INDEX IF NOT EXISTS ix_smart_yard_conditions_map ON smart_yard_conditions(map_id,valid_until);

CREATE TABLE IF NOT EXISTS driver_route_progress (
 gate_transaction_id uuid NOT NULL REFERENCES gate_transactions(id),
 container_id uuid NOT NULL REFERENCES containers(id),
 arrived_at timestamptz,
 current_node_id varchar(120),
 destination_key text,
 updated_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(gate_transaction_id,container_id)
);

-- Explicit links are optional for existing operations. No historical association is guessed.
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS booking_id uuid REFERENCES bookings(id);
ALTER TABLE yard_tasks ADD COLUMN IF NOT EXISTS container_visit_id uuid REFERENCES container_visits(id);
CREATE INDEX IF NOT EXISTS ix_yard_tasks_booking ON yard_tasks(booking_id) WHERE booking_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_yard_tasks_visit ON yard_tasks(container_visit_id) WHERE container_visit_id IS NOT NULL;

-- Canonical shared receipt tables must exist before the Node route read/confirm APIs run.
CREATE TABLE IF NOT EXISTS yard_operation_events (
 id uuid PRIMARY KEY,
 operation_id uuid NOT NULL,
 container_id uuid NOT NULL,
 driver_id uuid NOT NULL,
 operation_status varchar(50) NOT NULL,
 event_type varchar(100) NOT NULL,
 delivery_status varchar(20) NOT NULL,
 occurred_at timestamptz NOT NULL,
 published_at timestamptz,
 delivery_error varchar(1000),
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_yard_operation_events_driver_occurred ON yard_operation_events(driver_id,occurred_at);
CREATE TABLE IF NOT EXISTS container_confirmation_audits (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 booking_id uuid NOT NULL REFERENCES bookings(id),
 container_id uuid NOT NULL REFERENCES containers(id),
 driver_id uuid NOT NULL REFERENCES drivers(id),
 condition varchar(30) NOT NULL,
 notes varchar(1000),
 container_status_after varchar(50) NOT NULL,
 confirmed_at timestamptz NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(),
 CONSTRAINT ux_container_confirmation_booking_container UNIQUE(booking_id,container_id)
);
CREATE INDEX IF NOT EXISTS ix_container_confirmation_audits_driver_id ON container_confirmation_audits(driver_id);

CREATE OR REPLACE FUNCTION smart_route_validate_progress() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM gate_transactions gt JOIN booking_containers bc ON bc.booking_id=gt.booking_id
   WHERE gt.id=NEW.gate_transaction_id AND bc.container_id=NEW.container_id
   AND gt.gate_type::text='gate_in' AND gt.status::text='completed') THEN
  RAISE EXCEPTION 'Route progress requires a completed Gate In for this booking/container.' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_smart_route_validate_progress ON driver_route_progress;
CREATE TRIGGER trg_smart_route_validate_progress BEFORE INSERT OR UPDATE ON driver_route_progress
 FOR EACH ROW EXECUTE FUNCTION smart_route_validate_progress();

-- Schematic approved circulation: clockwise perimeter plus a dedicated service lane.
-- Slot route endpoints are truck handover stops beside stacks, never paths through stacks.
-- Capacity and slot labels come from existing yard data; no container/occupancy is seeded.
DO $$
DECLARE nodes jsonb; edges jsonb; blocks jsonb; slots jsonb; gate record; block record; slot record;
 idx integer:=0; block_count integer; block_y numeric; block_h numeric; service_id text; node_id text;
BEGIN
 IF NOT EXISTS(SELECT 1 FROM smart_yard_maps) THEN
  nodes := '[
   {"id":"north-west","label":"Đường vòng phía Tây Bắc","x":60,"y":150},
   {"id":"north-gate-01","label":"Nhập làn GATE-01","x":280,"y":150},
   {"id":"north-gate-02","label":"Nhập làn GATE-02","x":620,"y":150},
   {"id":"north-service","label":"Lối vào làn giao nhận","x":820,"y":150},
   {"id":"north-east","label":"Đường vòng phía Đông Bắc","x":940,"y":150},
   {"id":"east-s1","label":"Đường vòng tại A-S1","x":940,"y":300},
   {"id":"east-s2","label":"Đường vòng tại A-S2","x":940,"y":420},
   {"id":"south-east","label":"Đường vòng phía Đông Nam","x":940,"y":580},
   {"id":"south-service","label":"Cuối làn giao nhận","x":820,"y":580},
   {"id":"south-exit","label":"Lối ra cổng phía Nam","x":500,"y":580},
   {"id":"south-west","label":"Đường vòng phía Tây Nam","x":60,"y":580},
   {"id":"service-s1","label":"Làn giao nhận A-S1","x":820,"y":300},
   {"id":"service-s2","label":"Làn giao nhận A-S2","x":820,"y":420},
   {"id":"area-a-s1","label":"A-S1 · Điểm giao nhận","location":"A-S1","x":850,"y":300},
   {"id":"area-a-s2","label":"A-S2 · Điểm giao nhận","location":"A-S2","x":850,"y":420}
  ]'::jsonb;
  edges := '[
   {"id":"ring-nw-g1","from":"north-west","to":"north-gate-01","distanceMeters":110,"travelSeconds":40},
   {"id":"ring-g1-g2","from":"north-gate-01","to":"north-gate-02","distanceMeters":170,"travelSeconds":60,"instruction":"Đi thẳng trên đường một chiều phía Bắc."},
   {"id":"ring-g2-service","from":"north-gate-02","to":"north-service","distanceMeters":100,"travelSeconds":36},
   {"id":"ring-service-ne","from":"north-service","to":"north-east","distanceMeters":60,"travelSeconds":22},
   {"id":"ring-ne-s1","from":"north-east","to":"east-s1","distanceMeters":75,"travelSeconds":27},
   {"id":"ring-s1-s2","from":"east-s1","to":"east-s2","distanceMeters":60,"travelSeconds":22},
   {"id":"ring-s2-se","from":"east-s2","to":"south-east","distanceMeters":80,"travelSeconds":29},
   {"id":"ring-se-service","from":"south-east","to":"south-service","distanceMeters":60,"travelSeconds":22},
   {"id":"ring-service-exit","from":"south-service","to":"south-exit","distanceMeters":160,"travelSeconds":58},
   {"id":"ring-exit-sw","from":"south-exit","to":"south-west","distanceMeters":220,"travelSeconds":80},
   {"id":"ring-sw-nw","from":"south-west","to":"north-west","distanceMeters":215,"travelSeconds":78},
   {"id":"service-entry","from":"north-service","to":"service-s1","distanceMeters":75,"travelSeconds":32,"instruction":"Rẽ vào làn giao nhận bên phải Block A, giữ đúng chiều di chuyển."},
   {"id":"service-s1-s2","from":"service-s1","to":"service-s2","distanceMeters":60,"travelSeconds":26},
   {"id":"service-exit","from":"service-s2","to":"south-service","distanceMeters":80,"travelSeconds":34},
   {"id":"dock-s1-west","from":"service-s1","to":"area-a-s1","distanceMeters":15,"travelSeconds":10,"bidirectional":true},
   {"id":"dock-s1-east","from":"east-s1","to":"area-a-s1","distanceMeters":45,"travelSeconds":20,"bidirectional":true},
   {"id":"dock-s2-west","from":"service-s2","to":"area-a-s2","distanceMeters":15,"travelSeconds":10,"bidirectional":true},
   {"id":"dock-s2-east","from":"east-s2","to":"area-a-s2","distanceMeters":45,"travelSeconds":20,"bidirectional":true}
  ]'::jsonb;
  FOR gate IN SELECT code,name FROM gates WHERE is_active ORDER BY code LOOP
   -- Use the configured gate directory. Unknown gates stay unmapped until surveyed.
   IF gate.code IN ('GATE-01','GATE-02','GATE-03') THEN
    node_id:='gate:'||gate.code;
    nodes:=nodes||jsonb_build_array(jsonb_build_object('id',node_id,'label',gate.name,'gateCode',gate.code,
      'location',gate.code,'x',CASE gate.code WHEN 'GATE-01' THEN 280 WHEN 'GATE-02' THEN 620 ELSE 500 END,
      'y',CASE gate.code WHEN 'GATE-03' THEN 640 ELSE 90 END));
    edges:=edges||jsonb_build_array(jsonb_build_object('id','access:'||gate.code,
      'from',CASE gate.code WHEN 'GATE-03' THEN 'south-exit' ELSE node_id END,
      'to',CASE gate.code WHEN 'GATE-01' THEN 'north-gate-01' WHEN 'GATE-02' THEN 'north-gate-02' ELSE node_id END,
      'distanceMeters',30,'travelSeconds',15));
   END IF;
  END LOOP;
  SELECT count(*) INTO block_count FROM yard_blocks;
  blocks:='[]'::jsonb; slots:='[]'::jsonb;
  FOR block IN SELECT yb.code,yb.name,max(ys.bay) AS bays,max(ys.row_no) AS rows,max(ys.tier) AS tiers
    FROM yard_blocks yb JOIN yard_slots ys ON ys.block_id=yb.id GROUP BY yb.id ORDER BY yb.code LOOP
   block_h:=240.0/greatest(block_count,1); block_y:=230+idx*block_h;
   blocks:=blocks||jsonb_build_array(jsonb_build_object('code',block.code,'label',block.name,
    'x',160,'y',block_y,'width',600,'height',block_h-CASE WHEN block_count>1 THEN 10 ELSE 0 END,
    'bays',block.bays,'rows',block.rows,'tiers',block.tiers));
   nodes:=nodes||jsonb_build_array(jsonb_build_object('id','block:'||block.code,'label','Điểm giao nhận Block '||block.code,
    'blockCode',block.code,'x',850,'y',300));
   edges:=edges||jsonb_build_array(jsonb_build_object('id','access:block:'||block.code,'from','area-a-s1','to','block:'||block.code,
    'distanceMeters',4,'travelSeconds',5,'bidirectional',true));
   idx:=idx+1;
  END LOOP;
  FOR slot IN SELECT ys.id,yb.code,ys.bay,ys.row_no,ys.tier,ys.status::text AS status,
    max(ys.row_no) OVER(PARTITION BY yb.id) AS max_rows
    FROM yard_slots ys JOIN yard_blocks yb ON yb.id=ys.block_id ORDER BY yb.code,ys.bay,ys.row_no,ys.tier LOOP
   service_id:=CASE WHEN slot.row_no<=ceil(slot.max_rows/2.0) THEN 'area-a-s1' ELSE 'area-a-s2' END;
   nodes:=nodes||jsonb_build_array(jsonb_build_object('id','slot:'||slot.id,'label','Giao nhận '||slot.code||'-'||slot.bay||'-'||slot.row_no||'-'||slot.tier,
    'slotId',slot.id,'blockCode',slot.code,'location',slot.code||'-'||slot.bay||'-'||slot.row_no||'-'||slot.tier,
    'x',850,'y',CASE WHEN service_id='area-a-s1' THEN 300 ELSE 420 END));
   edges:=edges||jsonb_build_array(jsonb_build_object('id','access:slot:'||slot.id,'from',service_id,'to','slot:'||slot.id,
    'distanceMeters',4,'travelSeconds',5,'bidirectional',true,'instruction','Dừng tại điểm giao nhận; chờ nhân viên bãi xử lý đúng vị trí container.'));
   slots:=slots||jsonb_build_array(jsonb_build_object('id',slot.id,'block',slot.code,'bay',slot.bay,'row',slot.row_no,'tier',slot.tier,'status',slot.status));
  END LOOP;
  INSERT INTO smart_yard_maps(version,name,map_data,is_active)
  VALUES('NXP114-yard-layout-v1','NexusPort · Sơ đồ đường nội bộ',jsonb_build_object('version','NXP114-yard-layout-v1',
    'updatedAt',to_char(now() AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS"Z"'),
    'nodes',nodes,'edges',edges,'layout',jsonb_build_object('blocks',blocks,'slots',slots)),true);
 END IF;
END $$;

COMMENT ON TABLE smart_yard_maps IS 'NXP-114 canonical directed truck road map. Slot graph nodes are handover approaches, layout stores stack geometry.';
COMMENT ON TABLE smart_yard_conditions IS 'Dispatcher or Smart Yard/IoT restrictions and congestion overlays. Expired observations are ignored.';
COMMENT ON TABLE driver_route_progress IS 'Driver arrival scoped to an exact completed Gate In and booking container; never physical gate state.';
INSERT INTO nexusport_schema_versions(version,description)
VALUES('20261017_017','Smart Yard road topology, live condition overlays, exact-trip route progress and confirmation receipts') ON CONFLICT DO NOTHING;
COMMIT;
