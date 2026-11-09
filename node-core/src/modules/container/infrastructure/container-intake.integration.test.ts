import { randomUUID } from 'crypto';
import pool, { query, getClient } from '../../../infrastructure/database/db';
import * as database from '../../../infrastructure/database/db';
import { ContainerIntakeService } from '../application/container-intake.service';
import { ContainerIntakeRepository } from './container-intake.repository';
import { ContainerJourneyRepository } from './container-journey.repository';
import { ShippingLineDirectoryRepository } from './shipping-line-directory.repository';
import { ContainerService } from '../application/container.service';
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
    const publicSearch=await journeys.search({containerNumber:'EMCU'},true);
    expect(publicSearch.items[0]).not.toHaveProperty('customerName');expect(publicSearch.items[0]).not.toHaveProperty('status');
    await expect(journeys.search({},true)).rejects.toThrow('Nhập');
    await expect(journeys.search({containerNumber:'EMCU',customerName:'Private'},true)).rejects.toThrow('chỉ được tìm');
  });
  it('deduplicates identical unbooked input and rejects a conflicting equipment type',async()=>{
    expect((await service.createManual(row,'transport_company',userA)).declarationId).toBe(declarationA);
    expect((await service.createManual(row,'transport_company',userA)).status).toBe('duplicate');
    await expect(service.createManual({...row,containerTypeCode:'20RF'},'transport_company',userA)).rejects.toThrow('loại khác');
  });
  it('links opposite directions, rejects overlapping stays, freezes history and snapshots exact cargo',async()=>{
    const c=await getClient();await c.query('BEGIN');
    const searchQuery = jest.spyOn(database, 'query').mockImplementation((sql, params) => c.query(sql, params));
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
      const filters = { containerNumber: row.containerNumber };
      const staff = { id: userA, role: 'Dispatcher' } as const;
      const carrier = { id: userB, role: 'Carrier' } as const;
      const berth = { id: userA, role: 'Berth Staff' } as const;
      const driver = { id: randomUUID(), role: 'Driver' } as const;
      const lineUser = { id: randomUUID(), role: 'Shipping Line' } as const;
      const lineId = randomUUID();
      const driverId = randomUUID();
      const vesselId = randomUUID();
      const callId = randomUUID();
      await c.query("INSERT INTO shipping_lines(id,full_name,short_name) VALUES($1,'Test line',$2)", [lineId, lineId]);
      await c.query("UPDATE containers SET shipping_line_id=$1 WHERE id=$2", [lineId, containerId]);
      await c.query(`INSERT INTO users(id,username,email,password,role,is_active,created_at,updated_at)
        VALUES($1,$1::uuid::text,$2,'test-only','Shipping Line',true,now(),now()),
          ($3,$3::uuid::text,$4,'test-only','Driver',true,now(),now())`,
      [lineUser.id, lineUser.id+'@example.invalid', driver.id, driver.id+'@example.invalid']);
      await c.query('INSERT INTO shipping_line_accounts(user_id,shipping_line_id) VALUES($1,$2)', [lineUser.id, lineId]);
      await c.query("INSERT INTO drivers(id,carrier_id,user_id,full_name,license_number) VALUES($1,$2,$3,'Test driver',$4)",
        [driverId, carrierA, driver.id, driverId.slice(0, 12)]);
      await c.query('UPDATE bookings SET driver_id=$1 WHERE id=$2', [driverId, inbound.id]);

      const planned = await journeys.searchYardVisits({ ...filters, visitStatus: 'planned',
        dateFrom: '2030-01-01T00:00Z', dateTo: '2030-01-02T00:00Z' }, staff);
      expect(planned.items).toEqual([expect.objectContaining({ id: inbound.visit, startedAt: null, completedAt: null, receipts: null,
        bookings: [expect.objectContaining({ id: inbound.id, bookingType: 'dropoff' })] })]);
      expect(new Date(planned.items[0].bookings[0].appointmentStart).toISOString()).toBe('2030-01-01T08:00:00.000Z');
      expect((await journeys.searchYardVisits(filters, carrier)).items).toEqual([]);
      expect((await journeys.searchYardVisits(filters, berth)).items).toEqual([]);
      expect((await journeys.searchYardVisits(filters, { ...driver, id: userB })).items).toEqual([]);
      expect((await journeys.searchYardVisits(filters, { ...lineUser, id: userB })).items).toEqual([]);
      expect((await journeys.listOwnIdentities(lineUser.id, { ...filters, page: 1, limit: 20 })).total).toBe(1);
      expect((await journeys.listOwnIdentities(lineUser.id, { containerNumber: 'ZZZU', page: 1, limit: 20 })).total).toBe(0);
      await expect(journeys.searchYardVisits({ ...filters, customerName: 'Private A' }, lineUser)).rejects.toThrow('không được lọc');

      await c.query("INSERT INTO shipping_line_prefixes(prefix,shipping_line_id) VALUES('CSQU',$1)", [lineId]);
      expect(await new ShippingLineDirectoryRepository().own(lineUser.id)).toMatchObject({ id: lineId, prefixes: ['CSQU'] });
      const registration = { containerNumber: 'CSQU3054383', isoSizeType: '42R1', tareWeightKg: 4200, maxGrossMassKg: 34000 };
      const containers = new ContainerService();
      await expect(containers.registerIdentity(registration, userB)).rejects.toThrow('tiền tố BIC');
      await expect(containers.registerIdentity(registration, lineUser.id)).resolves.toMatchObject({ containerTypeCode: '40RF' });

      await c.query("INSERT INTO vessels(id,name) VALUES($1,'Test vessel')", [vesselId]);
      await c.query("INSERT INTO vessel_calls(id,vessel_id,call_code,eta) VALUES($1,$2,$3,'2030-01-01T08:00Z')", [callId, vesselId, callId.slice(0, 15)]);
      await c.query('UPDATE container_visits SET vessel_call_id=$1 WHERE id=$2', [callId, inbound.visit]);
      await c.query(`INSERT INTO eir_transactions(reference_no,container_visit_id,activity,eir_status,check_in_at)
        VALUES($1,$2,'out','draft','2030-01-02T08:00Z')`, [randomUUID(), inbound.visit]);
      expect((await journeys.searchYardVisits(filters, berth)).items[0]).toMatchObject({ id: inbound.visit, customerName: null, receipts: null });
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
      expect((await journeys.searchYardVisits(filters, staff)).items.find(item => item.id === inbound.visit))
        .toMatchObject({ customerName: 'Private A', receipts: [expect.objectContaining({ id: receipt.id, activity: 'in' })] });
      expect((await journeys.searchYardVisits(filters, carrier)).items.find(item => item.id === inbound.visit))
        .toMatchObject({ customerName: null, receipts: null, bookings: [expect.objectContaining({ id: outbound.id, bookingType: 'pickup' })] });
      expect((await journeys.searchYardVisits(filters, driver)).items.find(item => item.id === inbound.visit))
        .toMatchObject({ customerName: null, bookings: [expect.objectContaining({ id: inbound.id })],
          receipts: [expect.objectContaining({ id: receipt.id, customerName: null })] });
      expect((await journeys.searchYardVisits(filters, lineUser)).items.find(item => item.id === inbound.visit))
        .toMatchObject({ customerName: null, receipts: [expect.objectContaining({ id: receipt.id, sealNumber: null })],
          bookings: [expect.objectContaining({ id: inbound.id, bookingCode: null }), expect.objectContaining({ id: outbound.id, bookingCode: null })] });
      await rejected(()=>c.query("UPDATE eir_transactions SET customer_name='Overwrite' WHERE id=$1",[receipt.id]),'bất biến');
      await rejected(()=>c.query("UPDATE eir_seals SET seal_number='Overwrite' WHERE eir_transaction_id=$1",[receipt.id]),'đã phát hành');
      await c.query("UPDATE bookings SET status='checked_in' WHERE id=$1",[outbound.id]);
      const gate=randomUUID();await c.query('INSERT INTO gates(id,code,name) VALUES($1,$2,\'Test gate\')',[gate,'TEST-'+gate]);
      await c.query("INSERT INTO gate_transactions(booking_id,gate_id,gate_type,status,checked_at,completed_at) VALUES($1,$2,'gate_out','completed','2030-01-02T08:00Z','2030-01-02T08:05Z')",[outbound.id,gate]);
      expect((await c.query('SELECT visit_status FROM container_visits WHERE id=$1',[inbound.visit])).rows[0].visit_status).toBe('completed');
      const exitReceipt = (await c.query("SELECT id,customer_name FROM eir_transactions WHERE container_visit_id=$1 AND activity='out' AND eir_status='issued'", [inbound.visit])).rows[0];
      expect(exitReceipt.customer_name).toBe('Private B');
      expect((await journeys.searchYardVisits(filters, carrier)).items.find(item => item.id === inbound.visit))
        .toMatchObject({ visitStatus: 'completed', receipts: [expect.objectContaining({ id: exitReceipt.id, activity: 'out' })] });
      await rejected(()=>c.query("UPDATE container_visits SET customer_name='Overwrite' WHERE id=$1",[inbound.visit]),'bất biến');
      await c.query('SET CONSTRAINTS ALL IMMEDIATE');
    }finally{searchQuery.mockRestore();await c.query('ROLLBACK');c.release();}
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
