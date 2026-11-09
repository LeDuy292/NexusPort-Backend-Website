import { Request, Response } from 'express';
import { Router } from 'express';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { DispatcherRepository } from '../infrastructure/dispatcher.repository';
import { ReadyBookingRepository } from '../infrastructure/ready-booking.repository';
import { DestinationService } from '../application/destination.service';
import { ForbiddenError, UnauthorizedError } from '../../../shared/errors/app-error';
import { sendSuccess, sendCreated } from '../../../shared/utils/response';
import { DriverRouteService } from '../../driver/application/driver-route.service';
import { ConfiguredYardMapProvider } from '../../driver/infrastructure/yard-map.provider';
import { query } from '../../../infrastructure/database/db';
import { z } from 'zod';
import { ValidationError, ConflictError } from '../../../shared/errors/app-error';

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
  getSmartRoutes = async (_req: Request, res: Response, next: import('express').NextFunction): Promise<void> => {
    try { res.setHeader('Cache-Control', 'no-store'); sendSuccess(res, await new DriverRouteService().monitor()); }
    catch (error) { next(error); }
  };

  updateRouteCondition = async (req: DispatcherRequest, res: Response, next: import('express').NextFunction): Promise<void> => {
    try {
      const parsed = z.object({ targetType: z.enum(['node', 'edge']), targetId: z.string().trim().min(1).max(120),
        restricted: z.boolean(), congestion: z.number().min(0).max(1).default(0),
        validUntil: z.string().datetime().optional() }).strict().safeParse(req.body);
      if (!parsed.success) throw new ValidationError('Điều kiện đường đi không hợp lệ.');
      const { targetType, targetId, restricted, congestion, validUntil } = parsed.data;
      if (validUntil && Date.parse(validUntil) <= Date.now()) throw new ValidationError('Thời hạn điều kiện phải ở tương lai.');
      if (process.env.SMART_ROUTE_MAP_PATH) throw new ConflictError('Bản đồ đang được quản lý bởi nguồn ngoài. Cập nhật điều kiện tại nguồn Smart Yard.');
      const snapshot = await new ConfiguredYardMapProvider().load(null);
      const collection = targetType === 'node' ? snapshot.map?.nodes : snapshot.map?.edges;
      if (!collection?.some(item => item.id === targetId)) throw new ValidationError('Đường/khu vực không có trong bản đồ hiện tại.');
      const result = await query(`INSERT INTO smart_yard_conditions(map_id,target_type,target_id,restricted,congestion,source,valid_until,observed_at,updated_by)
        SELECT id,$1,$2,$3,$4,'dispatcher',COALESCE($5::timestamptz,now()+interval '30 minutes'),now(),(SELECT id FROM users WHERE id=$6)
        FROM smart_yard_maps WHERE is_active AND version=$7 ON CONFLICT(map_id,target_type,target_id) DO UPDATE
        SET restricted=excluded.restricted,congestion=excluded.congestion,source=excluded.source,valid_until=excluded.valid_until,observed_at=now(),updated_by=excluded.updated_by
        RETURNING target_type AS "targetType",target_id AS "targetId",restricted,congestion,valid_until AS "validUntil"`,
        [targetType,targetId,restricted,congestion,validUntil ?? null,req.dispatcher?.id ?? null,snapshot.map?.version]);
      if (!result.rows[0]) throw new ConflictError('Chưa có bản đồ bãi đang hoạt động.');
      sendSuccess(res, result.rows[0]);
    } catch (error) { next(error); }
  };
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
  router.get('/smart-routes', authenticateDispatcher, controller.getSmartRoutes);
  router.put('/smart-routes/conditions', authenticateDispatcher, controller.updateRouteCondition);
  router.get('/yard/destinations', authenticateDispatcher, controller.getYardDestinations);
  router.get('/operations/:operationId/destination', authenticateDispatcher, controller.getOperationDestination);
  router.put('/operations/:operationId/destination', authenticateDispatcher, controller.assignOperationDestination);
  router.get('/', controller.getAll);
  router.get('/:id', controller.getById);
  router.post('/', controller.create);

  return router;
};
