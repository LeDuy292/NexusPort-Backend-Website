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

// Health Check
router.get('/health', healthController.check);

// 11 Domain Module Routes
router.use('/identity', createIdentityRouter());
router.use('/bookings', createBookingRouter());
router.use('/vessels', createVesselRouter());
router.use('/berths', createBerthRouter());
router.use('/containers', createContainerRouter());
router.use('/yard', createYardRouter());
router.use('/gate', createGateRouter());
router.use('/dispatcher', createDispatcherRouter());
router.use('/vehicles', createVehicleRouter());
router.use('/drivers', createDriverRouter());
router.use('/equipment', createEquipmentRouter());

export default router;
