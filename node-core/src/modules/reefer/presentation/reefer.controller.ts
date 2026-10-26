import { Request, Response, NextFunction, Router } from 'express';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { ReeferService } from '../application/reefer.service';
import { sendSuccess, sendCreated } from '../../../shared/utils/response';
import {
  createReeferRequestSchema,
  assignPlugSchema,
  confirmPowerSchema,
  terminateSessionSchema,
  transferPlugSchema,
  recordTemperatureSchema,
  updatePlugStatusSchema,
  resolveAlertSchema,
} from '../application/reefer.validator';

const getUserIdFromReq = (req: Request): string => {
  const token = req.headers.authorization?.match(/^Bearer\s+(.+)$/i)?.[1];
  if (token) {
    try {
      const payload = jwt.verify(
        token,
        process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!',
        { algorithms: ['HS256'] }
      ) as JwtPayload;
      const sub = payload.sub || payload.id || (payload as any).userId;
      if (sub) return String(sub);
    } catch {
      // Fallback
    }
  }
  return (req.headers['x-user-id'] as string) || '30768160-2766-47ab-aa88-d0187cd47c5d';
};

export class ReeferController {
  private service = new ReeferService();

  getOverview = async (_req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const data = await this.service.getOverviewStats();
      sendSuccess(res, data);
    } catch (err) {
      next(err);
    }
  };

  getEligibleContainers = async (_req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const data = await this.service.getEligibleContainers();
      sendSuccess(res, data);
    } catch (err) {
      next(err);
    }
  };

  getPlugs = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const { usage_status, technical_status, search } = req.query as any;
      const plugs = await this.service.getPlugs({ usage_status, technical_status, search });
      sendSuccess(res, plugs);
    } catch (err) {
      next(err);
    }
  };

  getPlugById = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const plug = await this.service.getPlugById(req.params.id);
      sendSuccess(res, plug);
    } catch (err) {
      next(err);
    }
  };

  updatePlugStatus = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const parsed = updatePlugStatusSchema.parse(req.body);
      const plug = await this.service.updatePlugStatus(req.params.id, parsed.technical_status);
      sendSuccess(res, plug);
    } catch (err) {
      next(err);
    }
  };

  getRequests = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const { status, container_id, search } = req.query as any;
      const requests = await this.service.getRequests({ status, container_id, search });
      sendSuccess(res, requests);
    } catch (err) {
      next(err);
    }
  };

  getRequestById = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const request = await this.service.getRequestById(req.params.id);
      sendSuccess(res, request);
    } catch (err) {
      next(err);
    }
  };

  createRequest = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const parsed = createReeferRequestSchema.parse(req.body);
      const userId = getUserIdFromReq(req);
      const request = await this.service.createRequest({ ...parsed, requested_by: userId });
      sendCreated(res, request);
    } catch (err) {
      next(err);
    }
  };

  cancelRequest = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const reason = req.body.reason || 'Người dùng hủy yêu cầu';
      const request = await this.service.cancelRequest(req.params.id, reason);
      sendSuccess(res, request);
    } catch (err) {
      next(err);
    }
  };

  getSessions = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const { session_status, container_id, plug_id, search } = req.query as any;
      const sessions = await this.service.getSessions({ session_status, container_id, plug_id, search });
      sendSuccess(res, sessions);
    } catch (err) {
      next(err);
    }
  };

  getSessionById = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const session = await this.service.getSessionById(req.params.id);
      sendSuccess(res, session);
    } catch (err) {
      next(err);
    }
  };

  assignPlug = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const parsed = assignPlugSchema.parse(req.body);
      const userId = getUserIdFromReq(req);
      const session = await this.service.assignPlug({ ...parsed, assigned_by: userId });
      sendCreated(res, session);
    } catch (err) {
      next(err);
    }
  };

  confirmPower = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const parsed = confirmPowerSchema.parse(req.body);
      const userId = getUserIdFromReq(req);
      const session = await this.service.confirmPower(req.params.id, userId, parsed.initial_temperature);
      sendSuccess(res, session);
    } catch (err) {
      next(err);
    }
  };

  terminateSession = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const parsed = terminateSessionSchema.parse(req.body);
      const session = await this.service.terminateSession(req.params.id, parsed.reason);
      sendSuccess(res, session);
    } catch (err) {
      next(err);
    }
  };

  cancelReservation = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const reason = req.body?.reason || 'Hủy gán ổ cắm theo yêu cầu điều độ';
      const session = await this.service.cancelReservation(req.params.id, reason);
      sendSuccess(res, session);
    } catch (err) {
      next(err);
    }
  };

  transferPlug = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const parsed = transferPlugSchema.parse(req.body);
      const userId = getUserIdFromReq(req);
      const session = await this.service.transferPlug(req.params.id, parsed.new_plug_id, parsed.reason, userId);
      sendSuccess(res, session);
    } catch (err) {
      next(err);
    }
  };

  recordTemperature = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const parsed = recordTemperatureSchema.parse(req.body);
      const userId = getUserIdFromReq(req);
      const record = await this.service.recordTemperature({
        ...parsed,
        session_id: req.params.id,
        recorded_by: userId,
      });
      sendCreated(res, record);
    } catch (err) {
      next(err);
    }
  };

  getTemperatureHistory = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const records = await this.service.getTemperatureHistory(req.params.id);
      sendSuccess(res, records);
    } catch (err) {
      next(err);
    }
  };

  getAlerts = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const { status, severity, session_id } = req.query as any;
      const alerts = await this.service.getAlerts({ status, severity, session_id });
      sendSuccess(res, alerts);
    } catch (err) {
      next(err);
    }
  };

  acknowledgeAlert = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const userId = getUserIdFromReq(req);
      const alert = await this.service.acknowledgeAlert(req.params.id, userId);
      sendSuccess(res, alert);
    } catch (err) {
      next(err);
    }
  };

  resolveAlert = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const parsed = resolveAlertSchema.parse(req.body);
      const userId = getUserIdFromReq(req);
      const alert = await this.service.resolveAlert(req.params.id, userId, parsed.notes);
      sendSuccess(res, alert);
    } catch (err) {
      next(err);
    }
  };

  createPlug = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const plug = await this.service.createPlug(req.body);
      sendCreated(res, plug);
    } catch (err) {
      next(err);
    }
  };

  togglePlugPower = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const isPowerOn = Boolean(req.body.is_power_on);
      const plug = await this.service.togglePlugPower(req.params.id, isPowerOn);
      sendSuccess(res, plug);
    } catch (err) {
      next(err);
    }
  };

  autoAllocate = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const userId = getUserIdFromReq(req);
      const result = await this.service.autoAllocatePlug({
        ...req.body,
        assigned_by: userId
      });
      sendCreated(res, result);
    } catch (err) {
      next(err);
    }
  };
}

export const createReeferRouter = (): Router => {
  const router = Router();
  const controller = new ReeferController();

  router.get('/overview', controller.getOverview);
  router.get('/eligible-containers', controller.getEligibleContainers);

  router.get('/plugs', controller.getPlugs);
  router.post('/plugs', controller.createPlug);
  router.get('/plugs/:id', controller.getPlugById);
  router.patch('/plugs/:id/technical-status', controller.updatePlugStatus);
  router.post('/plugs/:id/power-toggle', controller.togglePlugPower);

  router.get('/requests', controller.getRequests);
  router.get('/requests/:id', controller.getRequestById);
  router.post('/requests', controller.createRequest);
  router.post('/requests/:id/cancel', controller.cancelRequest);

  router.get('/sessions', controller.getSessions);
  router.get('/sessions/:id', controller.getSessionById);
  router.post('/sessions/assign', controller.assignPlug);
  router.post('/sessions/:id/confirm-power', controller.confirmPower);
  router.post('/sessions/:id/terminate', controller.terminateSession);
  router.post('/sessions/:id/cancel-reservation', controller.cancelReservation);
  router.post('/sessions/:id/transfer', controller.transferPlug);
  router.post('/auto-allocate', controller.autoAllocate);

  router.post('/sessions/:id/temperature', controller.recordTemperature);
  router.get('/sessions/:id/temperature-history', controller.getTemperatureHistory);

  router.get('/alerts', controller.getAlerts);
  router.post('/alerts/:id/acknowledge', controller.acknowledgeAlert);
  router.post('/alerts/:id/resolve', controller.resolveAlert);

  return router;
};
