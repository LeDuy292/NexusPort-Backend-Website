import { Router, Request, Response, NextFunction } from 'express';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { query } from '../../../infrastructure/database/db';
import { ForbiddenError, UnauthorizedError, ValidationError } from '../../../shared/errors/app-error';
import { DriverAccountService } from '../application/driver-account.service';

interface ActorRequest extends Request { actor?: {id: string; role: string} }
export const authenticateAccountManager = async (req: ActorRequest, _res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.match(/^Bearer\s+(.+)$/i)?.[1];
    if (!token) throw new UnauthorizedError();
    let payload: JwtPayload;
    try {
      payload = jwt.verify(token, process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!', {algorithms: ['HS256']}) as JwtPayload;
    } catch { throw new UnauthorizedError(); }
    const id = payload.sub || payload.id;
    if (typeof id !== 'string' || !/^[0-9a-f-]{36}$/i.test(id)) throw new UnauthorizedError();
    const user = (await query('SELECT id,role,is_active,session_id FROM users WHERE id=$1', [id])).rows[0];
    if (!user?.is_active || (user.session_id && user.session_id !== payload.sessionId)) throw new UnauthorizedError();
    if (!['Administrator', 'Transport Company'].includes(user.role)) throw new ForbiddenError();
    req.actor = user;
    next();
  } catch (error) { next(error); }
};

export const createDriverAccountRouter = () => {
  const router = Router();
  const service = new DriverAccountService();
  router.use(authenticateAccountManager);
  router.get('/driver-links', async (req: ActorRequest, res, next) => {
    try { res.json({success: true, data: await service.links(req.actor!.id, req.actor!.role === 'Administrator')}); }
    catch (error) { next(error); }
  });
  router.post('/', async (req: ActorRequest, res, next) => {
    try {
      if (req.actor!.role !== 'Transport Company') throw new ForbiddenError('Công ty vận tải tự tạo tài khoản cho tài xế của mình.');
      res.status(201).json({success: true, data: await service.create(req.actor!.id, req.body)});
    } catch (error) { next(error); }
  });
  router.post('/:driverId/resend', async (req: ActorRequest, res, next) => {
    try {
      if (!/^[0-9a-f-]{36}$/i.test(req.params.driverId)) throw new ValidationError('ID tài xế không hợp lệ.');
      res.json({success: true, data: await service.resend(req.actor!.id, req.params.driverId, req.actor!.role === 'Administrator')});
    } catch (error) { next(error); }
  });
  return router;
};
