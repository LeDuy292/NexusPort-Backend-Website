'use strict';
const fail=(message,statusCode=422)=>Object.assign(new Error(message),{statusCode});
const database=()=>require('../../config/database').sequelize;
const uuid=/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

function validateBlock(input){
 const code=String(input.blockCode||'').trim().toUpperCase();
 const name=String(input.description||code).trim();
 const bays=Number(input.maxBays),rows=Number(input.maxRows),tiers=Number(input.maxTiers);
 if(!/^[A-Z][A-Z0-9_-]{0,49}$/.test(code)||!name||name.length>150)throw fail('Mã khu bãi hoặc mô tả không hợp lệ.');
 if(![bays,rows,tiers].every(n=>Number.isInteger(n)&&n>0)||bays>100||rows>50||tiers>10||bays*rows*tiers>20000)throw fail('Sức chứa không hợp lệ; tối đa 20.000 vị trí mỗi khu bãi.');
 return{code,name,bays,rows,tiers};
}
async function createBlock(input,db=database()){
 const value=validateBlock(input);
 try{return await db.transaction(async transaction=>{
  const [created]=await db.query('INSERT INTO yard_blocks(code,name,max_capacity) VALUES(:code,:name,:capacity) RETURNING id,code,name',{replacements:{code:value.code,name:value.name,capacity:value.bays*value.rows*value.tiers},transaction});
  const block=created[0];
  await db.query(`INSERT INTO yard_slots(block_id,bay,row_no,tier) SELECT :blockId,bay,row_no,tier FROM generate_series(1,:bays) bay CROSS JOIN generate_series(1,:rows) row_no CROSS JOIN generate_series(1,:tiers) tier`,{replacements:{blockId:block.id,...value},transaction});
  return{id:block.id,blockCode:block.code,description:block.name,maxBays:value.bays,maxRows:value.rows,maxTiers:value.tiers};
 });}catch(error){if(error.original?.code==='23505'||error.parent?.code==='23505')throw fail('Mã khu bãi đã tồn tại.',409);throw error;}
}
async function setMaintenance(slotId,maintenance,db=database()){
 if(!uuid.test(slotId)||typeof maintenance!=='boolean')throw fail('Cần mã vị trí hợp lệ và trạng thái bảo trì đúng định dạng.');
 return db.transaction(async transaction=>{
  const [rows]=await db.query('SELECT id,status::text AS status FROM yard_slots WHERE id=:slotId FOR UPDATE',{replacements:{slotId},transaction});
  if(!rows.length)throw fail('Không tìm thấy vị trí.',404);
  if(!['empty','maintenance'].includes(rows[0].status))throw fail('Chỉ được thay đổi vị trí trống hoặc đang bảo trì.',409);
  const [updated]=await db.query(`UPDATE yard_slots SET status=CAST(:status AS yard_slot_status) WHERE id=:slotId AND NOT EXISTS(SELECT 1 FROM container_positions WHERE slot_id=:slotId AND is_current=true) RETURNING id,status::text AS status`,{replacements:{slotId,status:maintenance?'maintenance':'empty'},transaction});
  if(!updated.length)throw fail('Vị trí này đang có container.',409);
  return updated[0];
 });
}
module.exports={validateBlock,createBlock,setMaintenance};
