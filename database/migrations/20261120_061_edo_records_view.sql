BEGIN;

-- e-D/O chỉ còn một nơi lưu là delivery_orders. edo_records (C# Preparation chỉ đọc) trở thành view suy ra từ đó;
-- edo_orders (rỗng, không code nào dùng) bị bỏ.

-- Giữ lại 3 container demo của edo_records dưới dạng e-D/O thật trong delivery_orders.
INSERT INTO delivery_orders (id, do_number, bl_number, consignee_name, status, hold_reason, valid_until, issued_date, free_time_days, containers)
SELECT gen_random_uuid(), 'EDO-DEMO-' || r.container_no, 'BL-DEMO-' || r.container_no, 'Demo consignee',
       CASE WHEN r.hold_flag = 'None' THEN 'RELEASED' ELSE 'HELD' END,
       CASE WHEN r.hold_flag = 'None' THEN NULL ELSE r.hold_flag END,
       r.expires_at, r.created_at,
       GREATEST(0, EXTRACT(day FROM (r.free_time_until - r.created_at))::int),
       jsonb_build_array(jsonb_build_object('containerNo', r.container_no, 'sealNo', r.seal_no))
  FROM edo_records r
 WHERE EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = 'edo_records')
   AND NOT EXISTS (SELECT 1 FROM delivery_orders d WHERE d.do_number = 'EDO-DEMO-' || r.container_no);

DROP TABLE IF EXISTS edo_records;
DROP TABLE IF EXISTS edo_orders;

CREATE OR REPLACE VIEW edo_records AS
SELECT DISTINCT ON (upper(c.value ->> 'containerNo'))
       md5(d.id::text || upper(c.value ->> 'containerNo'))::uuid                       AS id,
       upper(c.value ->> 'containerNo')                                              AS container_no,
       COALESCE(c.value ->> 'sealNo', '')                                            AS seal_no,
       CASE WHEN d.status = 'CANCELLED' THEN 'Cancelled'
            WHEN d.valid_until < now() THEN 'Expired' ELSE 'Valid' END               AS release_status,
       CASE WHEN d.status = 'HELD' THEN
              CASE WHEN d.hold_reason ILIKE 'customs%' THEN 'CustomsHold'
                   WHEN d.hold_reason ILIKE 'port%' THEN 'PortHold' ELSE 'LineHold' END
            ELSE 'None' END                                                          AS hold_flag,
       COALESCE(d.valid_until, now())                                                AS expires_at,
       CASE WHEN d.free_time_days IS NULL THEN NULL
            ELSE COALESCE(d.issued_date, d.created_at) + make_interval(days => d.free_time_days) END AS free_time_until,
       COALESCE(d.created_at, now())                                                 AS created_at
  FROM delivery_orders d
  CROSS JOIN LATERAL jsonb_array_elements(COALESCE(d.containers, '[]'::jsonb)) AS c(value)
 WHERE d.status IN ('RELEASED', 'HELD', 'CANCELLED') AND COALESCE(c.value ->> 'containerNo', '') <> ''
 ORDER BY upper(c.value ->> 'containerNo'), COALESCE(d.issued_date, d.created_at) DESC;

INSERT INTO nexusport_schema_versions(version, description)
SELECT '20261120_061', 'edo_records thành view từ delivery_orders; bỏ edo_orders'
WHERE NOT EXISTS (SELECT 1 FROM nexusport_schema_versions WHERE version = '20261120_061');

COMMIT;
