import { randomUUID } from 'crypto';
import pool, { query, getClient } from '../../../infrastructure/database/db';
import { ContainerIntakeService } from '../application/container-intake.service';
import { ContainerIntakeRepository } from './container-intake.repository';
import { ContainerJourneyRepository } from './container-journey.repository';
const enabled = process.env.NXP038_INTEGRATION === '1' && process.env.DATABASE_URL === 'postgresql://postgres@127.0.0.1:55438/nxp038_test';
(enabled ? describe : describe.skip)('Shared container identity and private journeys in PostgreSQL', () => {
  const userA=randomUUID(),userB=randomUUID(),carrierA=randomUUID(),carrierB=randomUUID();
  const service=new ContainerIntakeService(), repository=new ContainerIntakeRepository(), journeys=new ContainerJourneyRepository();
  const row={containerNumber:'EMCU8361795',containerTypeCode:'40RF',cargoType:'reefer',requiredPowerKw:12.5,grossWeightKg:10000,customerName:'Private A',sealNumber:'REAL-SEAL-A'};
  let containerId:string,declarationA:string,declarationB:string;
  beforeAll(async()=>{
    for(const [u,c] of [[userA,carrierA],[userB,carrierB]]){
      await query("INSERT INTO users(id,username,email,password,role,is_active,created_at,updated_at) VALUES($1,$2,$3,'test-only','Transport Company',true,now(),now())",[u,'journey-'+u,u+'@example.invalid']);
      await query('INSERT INTO carriers(id,company_name) VALUES($1,$2)',[c,'Test '+c]);
      await query('INSERT INTO carrier_users(carrier_id,user_id) VALUES($1,$2)',[c,u]);
    }
  });
  afterAll(async()=>{
    await query('DELETE FROM booking_containers WHERE booking_id IN (SELECT id FROM bookings WHERE carrier_id=ANY($1::uuid[]))',[[carrierA,carrierB]]);
    await query('DELETE FROM bookings WHERE carrier_id=ANY($1::uuid[])',[[carrierA,carrierB]]);
    await query('DELETE FROM container_visit_access WHERE carrier_id=ANY($1::uuid[])',[[carrierA,carrierB]]);
    await query('DELETE FROM container_visits WHERE declared_by=ANY($1::uuid[])',[[userA,userB]]);
    await query('DELETE FROM container_declarations WHERE declared_by=ANY($1::uuid[]) OR carrier_id=ANY($2::uuid[])',[[userA,userB],[carrierA,carrierB]]);
    if(containerId)await query('DELETE FROM containers WHERE id=$1',[containerId]);
    await query('DELETE FROM carrier_users WHERE user_id=ANY($1::uuid[])',[[userA,userB]]);
    await query('DELETE FROM carriers WHERE id=ANY($1::uuid[])',[[carrierA,carrierB]]);
    await query('DELETE FROM users WHERE id=ANY($1::uuid[])',[[userA,userB]]);
    await pool.end();
  });
  it('shares identity while storing cargo outside the master and outside a yard stay',async()=>{
    const result=await service.createManual(row,'transport_company',userA);
    containerId=result.containerId!;declarationA=result.declarationId!;
    expect(result.status).toBe('created_master');expect(result.visitId).toBeUndefined();
    expect((await query('SELECT seal_no,gross_weight_kg,required_power_kw,carrier_id,status FROM containers WHERE id=$1',[containerId])).rows[0]).toEqual({seal_no:null,gross_weight_kg:null,required_power_kw:null,carrier_id:null,status:'expected'});
    expect((await query('SELECT count(*)::int AS n FROM container_visits WHERE container_id=$1',[containerId])).rows[0].n).toBe(0);
    expect(await repository.listTransportDeclarations(userA,row.containerNumber)).toEqual([expect.objectContaining({id:declarationA,customerName:'Private A',requiredPowerKw:12.5})]);
  });
  it('keeps another company declaration independent and returns only public identity in lookup/search',async()=>{
    const result=await service.createManual({...row,customerName:'Private B',sealNumber:'REAL-SEAL-B'},'transport_company',userB);
    declarationB=result.declarationId!;expect(result.containerId).toBe(containerId);
    expect((await repository.listTransportDeclarations(userB,row.containerNumber)).map(d=>d.customerName)).toEqual(['Private B']);
    const lookup=await journeys.lookup(row.containerNumber);
    expect(Object.keys(lookup).sort()).toEqual(['id','containerNumber','containerTypeId','containerTypeCode','containerTypeDescription'].sort());
    const publicSearch=await journeys.search({containerNumber:'EMCU'},userB);
    expect(publicSearch.items[0]).not.toHaveProperty('customerName');expect(publicSearch.items[0]).not.toHaveProperty('status');
    await expect(journeys.search({},userA)).rejects.toThrow('Nhập');
    await expect(journeys.search({containerNumber:'EMCU',customerName:'Private'},userB)).rejects.toThrow('chỉ được tìm');
  });
  it('deduplicates identical unbooked input and rejects a conflicting equipment type',async()=>{
    expect((await service.createManual(row,'transport_company',userA)).declarationId).toBe(declarationA);
    expect((await service.createManual(row,'transport_company',userA)).status).toBe('duplicate');
    await expect(service.createManual({...row,containerTypeCode:'20RF'},'transport_company',userA)).rejects.toThrow('loại khác');
  });
  it('links opposite directions, rejects overlapping stays, freezes history and snapshots exact cargo',async()=>{
    const c=await getClient();await c.query('BEGIN');
    const rejected=async(work:()=>Promise<unknown>,text:string)=>{
      await c.query('SAVEPOINT invalid_case');
      try{await expect(work()).rejects.toThrow(text);}finally{await c.query('ROLLBACK TO SAVEPOINT invalid_case');await c.query('RELEASE SAVEPOINT invalid_case');}
    };
    const book=async(type:string,carrier:string,declaration:string,start:string,end:string,visit?:string)=>{
      const id=randomUUID();
      await c.query('INSERT INTO bookings(id,carrier_id,booking_code,booking_type,status,appointment_start,appointment_end) VALUES($1,$2,$3,$4,\'pending\',$5,$6)',[id,carrier,'TEST-'+id,type,start,end]);
      const link=(await c.query('INSERT INTO booking_containers(booking_id,container_id,declaration_id,container_visit_id) VALUES($1,$2,$3,$4) RETURNING container_visit_id',[id,containerId,declaration,visit??null])).rows[0];
      return{id,visit:link.container_visit_id};
    };
    try{
      const inbound=await book('dropoff',carrierA,declarationA,'2030-01-01T08:00Z','2030-01-01T09:00Z');
      await rejected(()=>c.query('UPDATE container_declarations SET customer_name=\'Overwrite\' WHERE id=$1',[declarationA]),'Bản khai');
      await rejected(()=>book('dropoff',carrierB,declarationB,'2030-01-04T08:00Z','2030-01-04T09:00Z'),'chồng');
      await rejected(()=>book('pickup',carrierB,declarationB,'2030-01-02T08:00Z','2030-01-02T09:00Z',inbound.visit),'cấp quyền');
      await c.query('INSERT INTO container_visit_access(container_visit_id,carrier_id) VALUES($1,$2)',[inbound.visit,carrierB]);
      await rejected(()=>book('pickup',carrierB,declarationB,'2030-01-01T07:00Z','2030-01-01T08:00Z',inbound.visit),'sau lượt nhập');
      const outbound=await book('pickup',carrierB,declarationB,'2030-01-02T08:00Z','2030-01-02T09:00Z',inbound.visit);
      const next=await book('dropoff',carrierB,declarationB,'2030-01-04T08:00Z','2030-01-04T09:00Z');
      await c.query('SET CONSTRAINTS ALL IMMEDIATE');await c.query('SET CONSTRAINTS ALL DEFERRED');
      await rejected(async()=>{await c.query('UPDATE bookings SET appointment_start=\'2030-01-01T08:00Z\' WHERE id=$1',[outbound.id]);await c.query('SET CONSTRAINTS ALL IMMEDIATE');},'lịch');
      await c.query("UPDATE bookings SET status='checked_in' WHERE id=$1",[inbound.id]);
      expect((await c.query('SELECT visit_status FROM container_visits WHERE id=$1',[inbound.visit])).rows[0].visit_status).toBe('active');
      await rejected(()=>c.query("UPDATE bookings SET status='checked_in' WHERE id=$1",[next.id]),'đang lưu bãi');
      const receipt=(await c.query("SELECT id,customer_name FROM eir_transactions WHERE container_visit_id=$1 AND activity='in'",[inbound.visit])).rows[0];
      expect(receipt.customer_name).toBe('Private A');
      await rejected(()=>c.query("UPDATE eir_transactions SET customer_name='Overwrite' WHERE id=$1",[receipt.id]),'bất biến');
      await rejected(()=>c.query("UPDATE eir_seals SET seal_number='Overwrite' WHERE eir_transaction_id=$1",[receipt.id]),'đã phát hành');
      await c.query("UPDATE bookings SET status='checked_in' WHERE id=$1",[outbound.id]);
      const gate=randomUUID();await c.query('INSERT INTO gates(id,code,name) VALUES($1,$2,\'Test gate\')',[gate,'TEST-'+gate]);
      await c.query("INSERT INTO gate_transactions(booking_id,gate_id,gate_type,status,checked_at,completed_at) VALUES($1,$2,'gate_out','completed','2030-01-02T08:00Z','2030-01-02T08:05Z')",[outbound.id,gate]);
      expect((await c.query('SELECT visit_status FROM container_visits WHERE id=$1',[inbound.visit])).rows[0].visit_status).toBe('completed');
      expect((await c.query("SELECT customer_name FROM eir_transactions WHERE container_visit_id=$1 AND activity='out'",[inbound.visit])).rows[0].customer_name).toBe('Private B');
      await rejected(()=>c.query("UPDATE container_visits SET customer_name='Overwrite' WHERE id=$1",[inbound.visit]),'bất biến');
      await c.query('SET CONSTRAINTS ALL IMMEDIATE');
    }finally{await c.query('ROLLBACK');c.release();}
  });
  it('serializes competing reservations from separate database connections',async()=>{
    const a=await getClient(),b=await getClient();
    const first=randomUUID(),second=randomUUID();
    try{
      await a.query('BEGIN');await b.query('BEGIN');
      for(const [c,id,carrier] of [[a,first,carrierA],[b,second,carrierB]] as const)
        await c.query("INSERT INTO bookings(id,carrier_id,booking_code,booking_type,appointment_start,appointment_end) VALUES($1,$2,$3,'dropoff','2035-01-01T08:00Z','2035-01-01T09:00Z')",[id,carrier,'CONCURRENT-'+id]);
      await a.query('INSERT INTO booking_containers(booking_id,container_id,declaration_id) VALUES($1,$2,$3)',[first,containerId,declarationA]);
      const competing=b.query('INSERT INTO booking_containers(booking_id,container_id,declaration_id) VALUES($1,$2,$3)',[second,containerId,declarationB]).then(()=>null,e=>e);
      await a.query('COMMIT');
      expect((await competing)?.message).toMatch(/chồng/);
    }finally{await a.query('ROLLBACK');await b.query('ROLLBACK');a.release();b.release();}
  });

  it('links declaration details to both booking directions without mixing another stay or company',async()=>{
    const inbound=(await query('SELECT bc.booking_id,bc.container_visit_id FROM booking_containers bc WHERE bc.declaration_id=$1',[declarationA])).rows[0];
    const pickup=randomUUID(),later=randomUUID();
    const c=await getClient();
    try {
      await c.query('BEGIN');
      await c.query("INSERT INTO bookings(id,carrier_id,booking_code,booking_type,appointment_start,appointment_end) VALUES($1,$2,$3,'pickup','2035-01-02T08:00Z','2035-01-02T09:00Z')",[pickup,carrierA,'DETAIL-'+pickup]);
      const code=(await c.query('SELECT pickup_code FROM container_visits WHERE id=$1',[inbound.container_visit_id])).rows[0].pickup_code;
      const snapshot=(await c.query('SELECT * FROM yard_prepare_pickup($1,$2,$3)',[containerId,code,carrierA])).rows[0];
      await c.query('INSERT INTO booking_containers(booking_id,container_id,declaration_id,container_visit_id) VALUES($1,$2,$3,$4)',[pickup,containerId,snapshot.DeclarationId,inbound.container_visit_id]);
      await c.query("INSERT INTO bookings(id,carrier_id,booking_code,booking_type,appointment_start,appointment_end) VALUES($1,$2,$3,'dropoff','2035-01-03T08:00Z','2035-01-03T09:00Z')",[later,carrierB,'DETAIL-'+later]);
      await c.query('INSERT INTO booking_containers(booking_id,container_id,declaration_id) VALUES($1,$2,$3)',[later,containerId,declarationB]);
      await c.query('COMMIT');
      const ownA=(await repository.listTransportDeclarations(userA,row.containerNumber)).find(d=>d.id===declarationA)!;
      expect(ownA.bookings.map(b=>b.id)).toEqual([inbound.booking_id,pickup]);
      expect(ownA.bookings.map(b=>b.bookingType)).toEqual(['dropoff','pickup']);
      expect(ownA.bookings.map(b=>b.isDeclarationBooking)).toEqual([true,false]);
      const pickupSnapshot=(await repository.listTransportDeclarations(userA,row.containerNumber)).find(d=>d.id===snapshot.DeclarationId)!;
      expect(pickupSnapshot.bookings.map(b=>b.isDeclarationBooking)).toEqual([false,true]);
      expect(pickupSnapshot.bookings.filter(b=>b.isDeclarationBooking).map(b=>b.bookingType)).toEqual(['pickup']);
      const ownB=(await repository.listTransportDeclarations(userB,row.containerNumber)).find(d=>d.id===declarationB)!;
      expect(ownB.bookings.map(b=>b.id)).toEqual([later]);
    }finally{await c.query('ROLLBACK');c.release();}
  });
});
