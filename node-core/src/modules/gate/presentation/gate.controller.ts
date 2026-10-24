import { Request, Response } from 'express';
import { Router } from 'express';
import { GateRepository } from '../infrastructure/gate.repository';
import { sendSuccess, sendCreated } from '../../../shared/utils/response';
import { query } from '../../../infrastructure/database/db';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { GateMovementService } from '../application/gate-movement.service';
import { ForbiddenError, UnauthorizedError } from '../../../shared/errors/app-error';

export class GateController {
  completeOut = async (req: Request, res: Response, next: import('express').NextFunction): Promise<void> => {
    try {
      const token = req.headers.authorization?.match(/^Bearer\s+(.+)$/i)?.[1];
      if (!token) throw new UnauthorizedError('Nhân viên cổng cần đăng nhập.');
      let payload: JwtPayload;
      try { payload = jwt.verify(token, process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!', { algorithms: ['HS256'] }) as JwtPayload; }
      catch { throw new UnauthorizedError('Token không hợp lệ hoặc đã hết hạn.'); }
      const role = String(payload.role ?? payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] ?? '').toLowerCase();
      if (!['gate officer', 'gate', 'admin', 'administrator'].includes(role)) throw new ForbiddenError('Chỉ nhân viên cổng được xác nhận xe đã ra cổng.');
      const actor = payload.sub ?? payload.id ?? payload['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier'];
      if (typeof actor !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(actor)) throw new UnauthorizedError('Tài khoản cổng không hợp lệ.');
      sendSuccess(res, await new GateMovementService().completeOut(actor, req.body));
    } catch (error) { next(error); }
  };
  locations = async (_req: Request, res: Response, next: import('express').NextFunction): Promise<void> => {
    try { sendSuccess(res, (await query(`SELECT id,code,name,CASE WHEN code='GATE-03' THEN 'gate_out' ELSE 'gate_in' END AS "gateType",is_active AS "isActive" FROM gates WHERE is_active AND code IN ('GATE-01','GATE-03') ORDER BY code`)).rows); }
    catch (error) { next(error); }
  };
  private repository = new GateRepository();

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

export const createGateRouter = (): Router => {
  const router = Router();
  const controller = new GateController();

  router.get('/locations', controller.locations);
  router.post('/complete-out', controller.completeOut);
  router.get('/', controller.getAll);
  router.get('/:id', controller.getById);
  router.post('/', controller.create);

  return router;
};
