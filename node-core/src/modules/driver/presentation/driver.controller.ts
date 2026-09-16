import { Request, Response } from 'express';
import { Router, NextFunction } from 'express';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { DriverRepository } from '../infrastructure/driver.repository';
import { DriverRouteService } from '../application/driver-route.service';
import { ForbiddenError, UnauthorizedError, ValidationError } from '../../../shared/errors/app-error';
import { sendSuccess, sendCreated } from '../../../shared/utils/response';

interface DriverRequest extends Request { driverId?: string }
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

const authenticateDriver = (req: DriverRequest, _res: Response, next: NextFunction) => {
  const token = req.headers.authorization?.match(/^Bearer\s+(.+)$/i)?.[1];
  if (!token) return next(new UnauthorizedError('Driver cần đăng nhập để xem route.'));
  try {
    const payload = jwt.verify(token, process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!', { algorithms: ['HS256'] }) as JwtPayload;
    const role = String(payload.role ?? payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] ?? '').toLowerCase();
    if (role !== 'driver') return next(new ForbiddenError('Chỉ Driver được xem route của mình.'));
    const driverId = payload.driverId ?? payload.driver_id ?? payload.sub ?? payload.id ?? payload['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier'];
    if (typeof driverId !== 'string' || !uuidPattern.test(driverId)) return next(new UnauthorizedError('Token không chứa Driver ID hợp lệ.'));
    req.driverId = driverId;
    next();
  } catch (error) {
    if (error instanceof ForbiddenError || error instanceof UnauthorizedError) return next(error);
    return next(new UnauthorizedError('Token không hợp lệ hoặc đã hết hạn.'));
  }
};

export class DriverController {
  private repository = new DriverRepository();
  private routeService = new DriverRouteService();

  getMyRoutes = async (req: DriverRequest, res: Response, next: NextFunction): Promise<void> => {
    try { sendSuccess(res, await this.routeService.list(req.driverId!)); }
    catch (error) { next(error); }
  };

  getMyContainerRoute = async (req: DriverRequest, res: Response, next: NextFunction): Promise<void> => {
    try {
      if (!uuidPattern.test(req.params.containerId)) throw new ValidationError('Container ID không hợp lệ.');
      sendSuccess(res, await this.routeService.get(req.driverId!, req.params.containerId));
    } catch (error) { next(error); }
  };

  getAll = async (req: Request, res: Response): Promise<void> => {
    const items = await this.repository.findAll();
    sendSuccess(res, items);
  };

  getById = async (req: Request, res: Response): Promise<void> => {
    const item = await this.repository.findById(req.params.id);
    if (!item) {
      res.status(404).json({ success: false, message: 'Not found' });
      return;
    }
    sendSuccess(res, item);
  };

  create = async (req: Request, res: Response): Promise<void> => {
    const item = await this.repository.create({
      name: req.body.name || 'Default Name',
      status: 'Active',
      description: req.body.description
    });
    sendCreated(res, item);
  };
}

export const createDriverRouter = (): Router => {
  const router = Router();
  const controller = new DriverController();

  router.get('/me/routes', authenticateDriver, controller.getMyRoutes);
  router.get('/me/routes/:containerId', authenticateDriver, controller.getMyContainerRoute);
  router.get('/', controller.getAll);
  router.get('/:id', controller.getById);
  router.post('/', controller.create);

  return router;
};
