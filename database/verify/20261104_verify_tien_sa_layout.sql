BEGIN READ ONLY;
SELECT version,description FROM nexusport_schema_versions WHERE version IN ('005','20261104_030');
SELECT version,is_active FROM smart_yard_maps ORDER BY created_at;
SELECT b.zone,count(DISTINCT b.id) AS blocks,count(s.id) AS slots,
 count(s.id) FILTER(WHERE s.status='occupied') AS occupied,
 count(s.id) FILTER(WHERE s.status='reserved') AS reserved,
 count(s.id) FILTER(WHERE s.has_reefer_plug) AS powered_positions
 FROM yard_blocks b LEFT JOIN yard_slots s ON s.block_id=b.id WHERE b.is_active GROUP BY b.zone ORDER BY b.zone;
SELECT count(*) AS orphan_current_positions FROM container_positions p
 LEFT JOIN yard_slots s ON s.id=p.slot_id LEFT JOIN yard_blocks b ON b.id=s.block_id
 WHERE p.is_current AND (s.id IS NULL OR b.id IS NULL OR NOT b.is_active);
SELECT count(*) AS overlapping_footprints FROM yard_slots a JOIN yard_slots b
 ON a.id<b.id AND a.block_id=b.block_id AND a.row_no=b.row_no AND a.tier=b.tier
 WHERE a.module_start<b.module_start+b.module_span AND b.module_start<a.module_start+a.module_span;
SELECT s.session_status,count(*) AS sessions,count(*) FILTER(WHERE b.zone='D' AND b.is_active) AS linked_to_d
 FROM reefer_power_sessions s JOIN reefer_plugs p ON p.id=s.plug_id JOIN yard_slots ys ON ys.id=p.slot_id JOIN yard_blocks b ON b.id=ys.block_id
 WHERE s.session_status IN ('ACTIVE','AWAITING_POWER','SUSPENDED') GROUP BY s.session_status;
ROLLBACK;
