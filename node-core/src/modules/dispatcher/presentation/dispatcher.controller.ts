import { Request, Response } from 'express';
import { Router } from 'express';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { DispatcherRepository } from '../infrastructure/dispatcher.repository';
import { ReadyBookingRepository } from '../infrastructure/ready-booking.repository';
import { DestinationService } from '../application/destination.service';
import { ForbiddenError, UnauthorizedError } from '../../../shared/errors/app-error';
import { sendSuccess, sendCreated } from '../../../shared/utils/response';

interface DispatcherRequest extends Request {
  dispatcher?: { id: string | null; name: string };
}

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

const authenticateDispatcher = (req: DispatcherRequest, _res: Response, next: import('express').NextFunction): void => {
  const token = req.headers.authorization?.match(/^Bearer\s+(.+)$/i)?.[1];
  if (!token) return next(new UnauthorizedError('Dispatcher cần đăng nhập để xem Booking Ready.'));
  try {
    const payload = jwt.verify(token, process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!',
      { algorithms: ['HS256'] }) as JwtPayload;
    const role = String(payload.role ?? payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] ?? '').toLowerCase();
    if (!['dispatcher', 'administrator'].includes(role)) {
      return next(new ForbiddenError('Chỉ Dispatcher được xem Booking Ready.'));
    }
    const rawId = payload.sub ?? payload.id ?? payload['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier'];
    const rawName = payload.name ?? payload.username ?? payload['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name'];
    req.dispatcher = {
      id: typeof rawId === 'string' && uuidPattern.test(rawId) ? rawId : null,
      name: typeof rawName === 'string' && rawName.trim() ? rawName : role,
    };
    next();
  } catch (error) {
    if (error instanceof ForbiddenError) return next(error);
    next(new UnauthorizedError('Token không hợp lệ hoặc đã hết hạn.'));
  }
};

export class DispatcherController {
  private repository = new DispatcherRepository();
  private readyBookingRepository = new ReadyBookingRepository();
  private destinationService = new DestinationService();

  getReadyBookings = async (_req: Request, res: Response, next: import('express').NextFunction): Promise<void> => {
    try {
      sendSuccess(res, await this.readyBookingRepository.findAll());
    } catch (error) {
      next(error);
    }
  };

  getYardDestinations = async (req: DispatcherRequest, res: Response, next: import('express').NextFunction): Promise<void> => {
    try {
      sendSuccess(res, await this.destinationService.list(typeof req.query.status === 'string' ? req.query.status : undefined));
    } catch (error) {
      next(error);
    }
  };

  getOperationDestination = async (req: DispatcherRequest, res: Response, next: import('express').NextFunction): Promise<void> => {
    try {
      const assignment = await this.destinationService.get(req.params.operationId);
      if (!assignment) {
        res.status(404).json({ success: false, message: 'Operation chưa có destination.' });
        return;
      }
      sendSuccess(res, assignment);
    } catch (error) {
      next(error);
    }
  };

  assignOperationDestination = async (req: DispatcherRequest, res: Response, next: import('express').NextFunction): Promise<void> => {
    try {
      sendSuccess(res, await this.destinationService.assign(
        req.params.operationId,
        req.body,
        req.dispatcher ?? { id: null, name: 'dispatcher' },
      ));
    } catch (error) {
      next(error);
    }
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

export const createDispatcherRouter = (): Router => {
  const router = Router();
  const controller = new DispatcherController();

  router.get('/bookings/ready', authenticateDispatcher, controller.getReadyBookings);
  router.get('/yard/destinations', authenticateDispatcher, controller.getYardDestinations);
  router.get('/operations/:operationId/destination', authenticateDispatcher, controller.getOperationDestination);
  router.put('/operations/:operationId/destination', authenticateDispatcher, controller.assignOperationDestination);
  router.get('/', controller.getAll);
  router.get('/:id', controller.getById);
  router.post('/', controller.create);

  return router;
};
