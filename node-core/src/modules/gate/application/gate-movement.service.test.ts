jest.mock('../../../infrastructure/database/db',()=>({getClient:jest.fn()}));
jest.mock('../../../infrastructure/clients/rabbitmq',()=>({RabbitMQClient:{getInstance:()=>({publish:jest.fn().mockResolvedValue(undefined)})}}));
jest.mock('../../../shared/utils/logger',()=>({logger:{warn:jest.fn()}}));
import { getClient } from '../../../infrastructure/database/db';
import { GateMovementService } from './gate-movement.service';

// The connection factory is mocked before importing the service. No database is opened.
describe('Combined booking physical exit',()=>{
  const bookingId='11111111-1111-4111-8111-111111111111',gateId='22222222-2222-4222-8222-222222222222';
  let counts:{total:number;confirmed:number;pickups:number;active:number},previous:boolean;
  const query=jest.fn(),release=jest.fn();
  beforeEach(()=>{
    counts={total:2,confirmed:2,pickups:1,active:1};previous=false;
    (getClient as jest.Mock).mockResolvedValue({query,release});
    query.mockImplementation(async(sql:string)=>{
      if(sql.includes('FOR UPDATE'))return {rows:[{id:bookingId,status:'completed',booking_type:'dropoff',driver_id:'driver'}]};
      if(sql.includes("gate_type='gate_out'"))return {rows:previous?[{id:'existing-passage'}]:[]};
      if(sql.includes('FROM gates'))return {rows:[{id:gateId}]};
      if(sql.includes("gate_type='gate_in'"))return {rows:[{id:'entry'}]};
      if(sql.includes('count(*)::int AS total'))return {rows:[counts]};
      if(sql.includes('INSERT INTO gate_transactions'))return {rows:[{id:'new-passage'}]};
      if(sql.includes('RETURNING p.slot_id')||sql.includes('RETURNING r.slot_id'))return {rows:[{slot_id:'outgoing-slot'}]};
      return {rows:[]};
    });
  });
  it('retains one shared trip until both container handoffs are confirmed',async()=>{
    counts.confirmed=1;
    await expect(new GateMovementService().completeOut('actor',{bookingId,gateId})).rejects.toThrow();
    expect(query.mock.calls.some(([sql])=>sql.includes('INSERT INTO gate_transactions'))).toBe(false);
    expect(query).toHaveBeenCalledWith('ROLLBACK');expect(release).toHaveBeenCalled();
  });
  it('requires only the outgoing stay to be active, regardless of parent Dropoff direction',async()=>{
    await expect(new GateMovementService().completeOut('actor',{bookingId,gateId})).resolves.toEqual({id:'new-passage'});
    expect(query.mock.calls.some(([sql])=>sql.includes('UPDATE container_positions'))).toBe(true);
    expect(query).toHaveBeenCalledWith('COMMIT');
  });
  it('refuses an outgoing stay that has already closed',async()=>{
    counts.active=0;
    await expect(new GateMovementService().completeOut('actor',{bookingId,gateId})).rejects.toThrow();
    expect(query.mock.calls.some(([sql])=>sql.includes('INSERT INTO gate_transactions'))).toBe(false);
  });
  it('reuses an existing physical exit without issuing another passage',async()=>{
    previous=true;
    await expect(new GateMovementService().completeOut('actor',{bookingId,gateId})).resolves.toEqual({id:'existing-passage'});
    expect(query.mock.calls.some(([sql])=>sql.includes('INSERT INTO gate_transactions'))).toBe(false);
  });
  it('does not release incoming yard positions for a single Dropoff',async()=>{
    counts={total:1,confirmed:1,pickups:0,active:0};
    await new GateMovementService().completeOut('actor',{bookingId,gateId});
    expect(query.mock.calls.some(([sql])=>sql.includes('UPDATE container_positions'))).toBe(false);
  });
});
