'use strict';
const {validateBlock,createBlock,setMaintenance}=require('../src/modules/yard/yard-layout.commands');
const id='11111111-1111-4111-8111-111111111111';
function mockDb(results){const db={query:jest.fn()};results.forEach(r=>db.query.mockResolvedValueOnce([r]));db.transaction=async fn=>fn('tx');return db;}
describe('JavaScript yard layout commands',()=>{
 it('validates capacity before creating inventory',()=>{
  expect(()=>validateBlock({blockCode:'B',maxBays:0,maxRows:5,maxTiers:4})).toThrow();
  expect(()=>validateBlock({blockCode:'B',maxBays:100,maxRows:50,maxTiers:10})).toThrow();
  expect(validateBlock({blockCode:' b ',maxBays:17,maxRows:9,maxTiers:4})).toMatchObject({code:'B',bays:17,rows:9,tiers:4});
 });
 it('creates the directory and positions in one transaction',async()=>{
  const db=mockDb([[{id,code:'B',name:'Shipping line'}],[]]);
  expect(await createBlock({blockCode:'B',description:'Shipping line',maxBays:17,maxRows:9,maxTiers:4},db)).toMatchObject({id,blockCode:'B'});
  expect(db.query.mock.calls.every(call=>call[1].transaction==='tx')).toBe(true);
 });
 it('rejects occupied and reserved positions',async()=>{
  for(const status of ['occupied','reserved'])await expect(setMaintenance(id,true,mockDb([[{id,status}]]))).rejects.toMatchObject({statusCode:409});
 });
 it('sets an explicit state idempotently and also checks physical occupancy',async()=>{
  const db=mockDb([[{id,status:'empty'}],[{id,status:'maintenance'}]]);
  expect(await setMaintenance(id,true,db)).toMatchObject({status:'maintenance'});
  expect(db.query.mock.calls[1][0]).toContain('is_current=true');
  expect(db.query.mock.calls[1][1].replacements.status).toBe('maintenance');
  await expect(setMaintenance(id,true,mockDb([[{id,status:'empty'}],[]]))).rejects.toMatchObject({statusCode:409});
 });
});
