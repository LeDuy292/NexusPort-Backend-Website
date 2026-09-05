import { Router } from 'express';
import { HealthController } from '../controllers/health.controller';
import { createIdentityRouter } from '../../modules/identity/presentation/identity.controller';
import { createBookingRouter } from '../../modules/booking/presentation/booking.controller';
import { createVesselRouter } from '../../modules/vessel/presentation/vessel.controller';
import { createBerthRouter } from '../../modules/berth/presentation/berth.controller';
import { createContainerRouter } from '../../modules/container/presentation/container.controller';
import { createYardRouter } from '../../modules/yard/presentation/yard.controller';
import { createGateRouter } from '../../modules/gate/presentation/gate.controller';
import { createDispatcherRouter } from '../../modules/dispatcher/presentation/dispatcher.controller';
import { createVehicleRouter } from '../../modules/vehicle/presentation/vehicle.controller';
import { createDriverRouter } from '../../modules/driver/presentation/driver.controller';
import { createEquipmentRouter } from '../../modules/equipment/presentation/equipment.controller';

const router = Router();
const healthController = new HealthController();

export interface NodeCoreRouteModule {
  prefix: string;
  tag: string;
  secured: boolean;
  router: Router;
}

export const nodeCoreRouteModules: NodeCoreRouteModule[] = [
  { prefix: '/identity', tag: 'Identity', secured: false, router: createIdentityRouter() },
  { prefix: '/bookings', tag: 'Bookings', secured: false, router: createBookingRouter() },
  { prefix: '/vessels', tag: 'Vessels', secured: false, router: createVesselRouter() },
  { prefix: '/berths', tag: 'Berths', secured: false, router: createBerthRouter() },
  { prefix: '/containers', tag: 'Containers', secured: true, router: createContainerRouter() },
  { prefix: '/yard', tag: 'Yard', secured: false, router: createYardRouter() },
  { prefix: '/gate', tag: 'Gate', secured: false, router: createGateRouter() },
  { prefix: '/dispatcher', tag: 'Dispatcher', secured: false, router: createDispatcherRouter() },
  { prefix: '/vehicles', tag: 'Vehicles', secured: false, router: createVehicleRouter() },
  { prefix: '/drivers', tag: 'Drivers', secured: false, router: createDriverRouter() },
  { prefix: '/equipment', tag: 'Equipment', secured: false, router: createEquipmentRouter() },
];

// Health Check
router.get('/health', healthController.check);

// 11 Domain Module Routes
for (const module of nodeCoreRouteModules) router.use(module.prefix, module.router);

export default router;
