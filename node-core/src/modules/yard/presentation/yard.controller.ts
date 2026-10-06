import { NextFunction, Request, Response, Router } from 'express';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { ForbiddenError, UnauthorizedError } from '../../../shared/errors/app-error';
import { sendCreated, sendSuccess } from '../../../shared/utils/response';
import { YardContainerService } from '../application/yard-container.service';
import { YardRepository } from '../infrastructure/yard.repository';

interface YardRequest extends Request { yardUser?: { id: string } }

const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

const authenticateYardStaff = (req: YardRequest, _res: Response, next: NextFunction): void => {
  const token = req.headers.authorization?.match(/^Bearer\s+(.+)$/i)?.[1];
  if (!token) return next(new UnauthorizedError('Yard Staff cần đăng nhập để tiếp nhận Container.'));
  try {
    const payload = jwt.verify(token,
      process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!',
      { algorithms: ['HS256'] }) as JwtPayload;
    const role = String(payload.role
      ?? payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] ?? '').trim().toLowerCase();
    if (!['yard staff', 'yard operator', 'yard', 'administrator', 'admin'].includes(role)) {
      return next(new ForbiddenError('Chỉ Yard Staff hoặc Administrator được tiếp nhận và lưu Container.'));
    }
    const rawId = payload.sub ?? payload.id
      ?? payload['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier'];
    if (typeof rawId !== 'string' || !UUID_PATTERN.test(rawId)) {
      return next(new UnauthorizedError('Token không chứa ID người dùng hợp lệ.'));
    }
    req.yardUser = { id: rawId };
    next();
  } catch (error) {
    if (error instanceof ForbiddenError || error instanceof UnauthorizedError) return next(error);
    next(new UnauthorizedError('Token không hợp lệ hoặc đã hết hạn.'));
  }
};

export class YardController {
  private repository = new YardRepository();
  private containerService = new YardContainerService();

  listAvailableSlots = async (req: YardRequest, res: Response, next: NextFunction): Promise<void> => {
    try { sendSuccess(res, await this.containerService.listAvailableSlots(req.params.containerId)); }
    catch (error) { next(error); }
  };

  confirmContainerReception = async (req: YardRequest, res: Response, next: NextFunction): Promise<void> => {
    try {
      sendSuccess(res, await this.containerService.confirmReception(req.params.containerId, req.body, {
        id: req.yardUser!.id, ipAddress: req.ip,
      }));
    } catch (error) { next(error); }
  };

  reserveContainerSlot = async (req: YardRequest, res: Response, next: NextFunction): Promise<void> => {
    try {
      sendSuccess(res, await this.containerService.reserveSlot(req.params.containerId, req.body, {
        id: req.yardUser!.id, ipAddress: req.ip,
      }));
    } catch (error) { next(error); }
  };

  placeContainer = async (req: YardRequest, res: Response, next: NextFunction): Promise<void> => {
    try {
      sendSuccess(res, await this.containerService.placeContainer(req.params.containerId, req.body, {
        id: req.yardUser!.id, ipAddress: req.ip,
      }));
    } catch (error) { next(error); }
  };

  getAll = async (_req: Request, res: Response): Promise<void> => {
    sendSuccess(res, await this.repository.findAll());
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
    sendCreated(res, await this.repository.create({
      name: req.body.name || 'Default Name', status: 'Active', description: req.body.description,
    }));
  };
}

export const createYardRouter = (): Router => {
  const router = Router();
  const controller = new YardController();

  router.get('/containers/:containerId/available-slots', authenticateYardStaff, controller.listAvailableSlots);
  router.post('/containers/:containerId/reception', authenticateYardStaff, controller.confirmContainerReception);
  router.put('/containers/:containerId/reservation', authenticateYardStaff, controller.reserveContainerSlot);
  router.put('/containers/:containerId/placement', authenticateYardStaff, controller.placeContainer);
  router.get('/', controller.getAll);
  router.get('/:id', controller.getById);
  router.post('/', controller.create);

  return router;
};
