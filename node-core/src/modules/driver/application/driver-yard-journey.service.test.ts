import { DriverRouteService } from './driver-route.service';
import { SmartRouteService } from './smart-route.service';
import { DriverRouteRepository, DriverRouteRow, routeDestinationKey } from '../infrastructure/driver-route.repository';
import { yardMapSchema } from '../domain/smart-route';

const map = yardMapSchema.parse({ version: 'journey-1', nodes: [
  {id:'in',label:'Cổng vào',x:0,y:0,gateCode:'GATE-02'},
  {id:'service',label:'Điểm giao nhận A-S1',x:100,y:0,location:'A-S1'},
  {id:'service2',label:'Điểm giao nhận A-S2',x:100,y:100,location:'A-S2'},
  {id:'out',label:'Cổng ra',x:0,y:100,gateCode:'GATE-03'},
], edges:[
  {id:'in-service',from:'in',to:'service',distanceMeters:100,travelSeconds:30},
  {id:'s1-s2',from:'service',to:'service2',distanceMeters:100,travelSeconds:30},
  {id:'service-out',from:'service',to:'out',distanceMeters:100,travelSeconds:30},
  {id:'service2-out',from:'service2',to:'out',distanceMeters:100,travelSeconds:30},
] });
const row: DriverRouteRow = {
  containerId:'11111111-1111-4111-8111-111111111111',containerNumber:'TEST1234567',
  bookingId:'22222222-2222-4222-8222-222222222222',bookingStatus:'checked_in',bookingType:'pickup',
  gateTransactionId:'33333333-3333-4333-8333-333333333333',gateStatus:'completed',gateCode:'GATE-02',
  slotId:null,zone:null,blockCode:null,bay:null,row:null,tier:null,instructionId:null,fromGate:null,routeDetail:null,mapData:null,
  operationId:'44444444-4444-4444-8444-444444444444',targetLocation:'A-S1',exitGateCode:'GATE-03',
  gateInAt:'2026-10-17T10:00:00Z',
};
const navigation = new SmartRouteService({ load: async () => ({map}) });
const serviceFor = (value: DriverRouteRow) => {
 const repository = {findForDriverAndContainer:jest.fn().mockResolvedValue(value),arrive:jest.fn(),confirm:jest.fn()} as unknown as DriverRouteRepository;
 return {repository,service:new DriverRouteService(repository,navigation)};
};

describe('Gate In to yard handover to physical Gate Out',()=>{
 it('starts at the real entry and enables arrival only on an available route',async()=>{
  const {service}=serviceFor(row);const result=await service.get('driver',row.containerId);
  expect(result.navigation?.waypoints.map(n=>n.id)).toEqual(['in','service']);
  expect(result.journey).toMatchObject({phase:'to_yard',canArrive:true,canConfirmContainer:false});
 });
 it('arrival is scoped to booking, container, destination and persists the destination node',async()=>{
  const {service,repository}=serviceFor(row);
  await service.arrive('driver',row.containerId,{bookingId:row.bookingId,action:'arrive'});
  expect(repository.arrive).toHaveBeenCalledWith('driver',row.containerId,row.bookingId,'service',routeDestinationKey(row));
  await expect(service.arrive('driver',row.containerId,{bookingId:row.bookingId,action:'arrive',currentNodeId:'out'})).rejects.toMatchObject({statusCode:422});
 });
 it('waits for actual yard completion before container confirmation',async()=>{
  const atYard={...row,arrivedAt:'2026-10-17T10:10:00Z',currentNodeId:'service',destinationKey:routeDestinationKey(row)};
  expect((await serviceFor(atYard).service.get('driver',row.containerId)).journey).toMatchObject({phase:'at_yard',canConfirmContainer:false});
  expect((await serviceFor({...atYard,yardCompletedAt:'2026-10-17T10:11:00Z'}).service.get('driver',row.containerId)).journey?.canConfirmContainer).toBe(true);
 });
 it('a changed destination resets arrival and reroutes from the last reported position',async()=>{
  const changed={...row,targetLocation:'A-S2',arrivedAt:'2026-10-17T10:10:00Z',currentNodeId:'service',destinationKey:routeDestinationKey(row)};
  const route=await serviceFor(changed).service.get('driver',row.containerId);
  expect(route.journey).toMatchObject({phase:'to_yard',arrivedAt:null,canConfirmContainer:false});
  expect(route.navigation?.waypoints.map(n=>n.id)).toEqual(['service','service2']);
 });
 it('after all container confirmations routes to exit; exit status comes only from the gate record',async()=>{
  const confirmed={...row,bookingStatus:'completed',containerConfirmedAt:'2026-10-17T10:12:00Z',currentNodeId:'service'};
  const route=await serviceFor(confirmed).service.get('driver',row.containerId);
  expect(route.journey?.phase).toBe('ready_for_gate_out');
  expect(route.navigation?.waypoints.map(n=>n.id)).toEqual(['service','out']);
  expect(route.target).toMatchObject({location:'GATE-03',source:'gate'});
  expect((await serviceFor({...confirmed,gateOutAt:'2026-10-17T10:20:00Z'}).service.get('driver',row.containerId)).journey?.phase).toBe('exited');
 });
 it('does not direct a truck to the exit while another container is unconfirmed',async()=>{
  const route=await serviceFor({...row,containerConfirmedAt:'2026-10-17T10:12:00Z',arrivedAt:'2026-10-17T10:10:00Z',destinationKey:routeDestinationKey(row)}).service.get('driver',row.containerId);
  expect(route.journey?.phase).toBe('at_yard');expect(route.target?.location).toBe('A-S1');expect(route.journey?.canConfirmContainer).toBe(false);
 });
 it('rejects driver supplied gate mutations and invalid confirmation conditions',async()=>{
  const {service,repository}=serviceFor(row);
  await expect(service.arrive('driver',row.containerId,{bookingId:row.bookingId,action:'gate-out'})).rejects.toMatchObject({statusCode:422});
  await expect(service.confirm('driver',row.containerId,{bookingId:row.bookingId,condition:'APPROVED'})).rejects.toMatchObject({statusCode:422});
  expect(repository.confirm).not.toHaveBeenCalled();
 });
});
