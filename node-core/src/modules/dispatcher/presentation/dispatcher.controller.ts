import { Request, Response } from 'express';
import { Router } from 'express';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { DispatcherRepository } from '../infrastructure/dispatcher.repository';
import { ReadyBookingRepository } from '../infrastructure/ready-booking.repository';
import { ForbiddenError, UnauthorizedError } from '../../../shared/errors/app-error';
import { sendSuccess, sendCreated } from '../../../shared/utils/response';

const authenticateDispatcher = (req: Request, _res: Response, next: import('express').NextFunction): void => {
  const token = req.headers.authorization?.match(/^Bearer\s+(.+)$/i)?.[1];
  if (!token) return next(new UnauthorizedError('Dispatcher cần đăng nhập để xem Booking Ready.'));
  try {
    const payload = jwt.verify(token, process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!',
      { algorithms: ['HS256'] }) as JwtPayload;
    const role = String(payload.role ?? payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] ?? '').toLowerCase();
    if (!['dispatcher', 'administrator'].includes(role)) {
      return next(new ForbiddenError('Chỉ Dispatcher được xem Booking Ready.'));
    }
    next();
  } catch (error) {
    if (error instanceof ForbiddenError) return next(error);
    next(new UnauthorizedError('Token không hợp lệ hoặc đã hết hạn.'));
  }
};

export class DispatcherController {
  private repository = new DispatcherRepository();
  private readyBookingRepository = new ReadyBookingRepository();

  getReadyBookings = async (_req: Request, res: Response, next: import('express').NextFunction): Promise<void> => {
    try {
      sendSuccess(res, await this.readyBookingRepository.findAll());
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
  router.get('/', controller.getAll);
  router.get('/:id', controller.getById);
  router.post('/', controller.create);

  return router;
};
