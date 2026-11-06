'use strict';
// Read-only by default. --apply publishes a reversible map revision, never slots.
const {Client}=require('../../node-core/node_modules/pg');
const fs=require('node:fs');
const path=require('node:path');
const {buildExcelYardMap,EXCEL_MAP_VERSION}=require('../../node-core/dist/shared/yard/build-yard-map');

async function main(){
 const apply=process.argv.includes('--apply');
 const expected=process.argv.find(a=>a.startsWith('--expected-version='))?.split('=').slice(1).join('=');
 if(apply&&!expected)throw new Error('--apply requires --expected-version from a reviewed dry run.');
 const connectionString=process.env.DATABASE_URL;if(!connectionString)throw new Error('DATABASE_URL is required.');
 const ssl=process.env.DB_SSL==='true'||process.env.DB_TARGET==='aiven'||connectionString.includes('sslmode=require');
 const client=new Client({connectionString,connectionTimeoutMillis:15000,...(ssl?{ssl:{rejectUnauthorized:false}}:{})});
 await client.connect();
 try{
  await client.query(apply?'BEGIN':'BEGIN READ ONLY');
  if(apply)await client.query("SELECT pg_advisory_xact_lock(114,25)");
  const active=(await client.query(`SELECT id,version,map_data FROM smart_yard_maps WHERE is_active=true${apply?' FOR UPDATE':''}`)).rows;
  if(active.length!==1)throw new Error('Exactly one active map is required.');
  let previous=active[0];
  if(previous.version===EXCEL_MAP_VERSION){console.log('Map revision already active: '+EXCEL_MAP_VERSION);await client.query('ROLLBACK');return;}
  if(apply&&previous.version!==expected)throw new Error('Active map changed; run a new dry run before publishing.');
  const slots=(await client.query(`SELECT ys.id,yb.code AS block,ys.bay,ys.row_no AS row,ys.tier,ys.status::text AS status FROM yard_slots ys JOIN yard_blocks yb ON yb.id=ys.block_id ORDER BY yb.code,ys.bay,ys.row_no,ys.tier`)).rows;
  const candidate=buildExcelYardMap(slots);
  const currentNodes=new Set(candidate.nodes.map(n=>n.id));
  const progress=(await client.query('SELECT DISTINCT current_node_id FROM driver_route_progress WHERE current_node_id IS NOT NULL')).rows;
  const unmappedProgress=progress.filter(p=>!currentNodes.has(p.current_node_id)).map(p=>p.current_node_id);
  const conditions=(await client.query('SELECT * FROM smart_yard_conditions WHERE map_id=$1 AND (valid_until IS NULL OR valid_until>now())',[previous.id])).rows;
  const ids={node:currentNodes,edge:new Set(candidate.edges.map(e=>e.id))};
  const unmatchedConditions=conditions.filter(c=>!ids[c.target_type]?.has(c.target_id));
  const gateMigration='20261025_022';
  const renameRequired=!(await client.query('SELECT 1 FROM nexusport_schema_versions WHERE version=$1',[gateMigration])).rowCount;
  const report={currentVersion:previous.version,nextVersion:EXCEL_MAP_VERSION,gateMigrationRequired:renameRequired?gateMigration:null,exitGate:'GATE-02',areas:candidate.referenceLayout.areas.length,slotsPreserved:slots.length,unmappedBlocks:[...new Set(slots.filter(s=>!candidate.nodes.some(n=>n.slotId===s.id)).map(s=>s.block))],unmappedProgress,unmatchedConditions:unmatchedConditions.length,changes:'Rename the existing exit to GATE-02 and publish the yard map; preserve gate/slot UUIDs, capacity, container positions, bookings and gate transactions.'};
  console.log(JSON.stringify(report,null,2));
  const out=process.argv.find(a=>a.startsWith('--out='))?.slice(6);
  if(out)fs.writeFileSync(out,JSON.stringify(candidate,null,2)+'\n');
  if(!apply){await client.query('ROLLBACK');return;}
  if(unmatchedConditions.length||unmappedProgress.length)throw new Error('Active road restrictions or recorded driver positions need explicit mapping before publishing.');
  if(await client.query('SELECT 1 FROM smart_yard_maps WHERE version=$1',[EXCEL_MAP_VERSION]).then(r=>r.rowCount))throw new Error('This map version already exists; review it before reactivation.');
  if(renameRequired){
    const sql=fs.readFileSync(path.join(__dirname,'../migrations/20261025_022_exit_gate_02.sql'),'utf8').replace(/^BEGIN;\s*/,'').replace(/COMMIT;\s*$/,'');
    await client.query(sql);
    previous=(await client.query('SELECT id,version,map_data FROM smart_yard_maps WHERE is_active=true FOR UPDATE')).rows[0];
  }
  await client.query('UPDATE smart_yard_maps SET is_active=false WHERE id=$1',[previous.id]);
  const next=(await client.query('INSERT INTO smart_yard_maps(version,name,map_data,is_active) VALUES($1,$2,$3,true) RETURNING id',[EXCEL_MAP_VERSION,'NexusPort · Excel yard layout',JSON.stringify(candidate)])).rows[0];
  for(const c of conditions)await client.query(`INSERT INTO smart_yard_conditions(map_id,target_type,target_id,restricted,congestion,source,valid_until,observed_at,updated_by) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)`,[next.id,c.target_type,c.target_id,c.restricted,c.congestion,c.source,c.valid_until,c.observed_at,c.updated_by]);
  const check=await client.query('SELECT version FROM smart_yard_maps WHERE is_active=true');
  if(check.rowCount!==1||check.rows[0].version!==EXCEL_MAP_VERSION)throw new Error('Map activation verification failed.');
  await client.query('COMMIT');console.log('Published: '+EXCEL_MAP_VERSION+'; previous map retained: '+previous.version);
 }catch(error){await client.query('ROLLBACK');throw error;}finally{await client.end();}
}
main().catch(error=>{console.error(error.message);process.exitCode=1;});
