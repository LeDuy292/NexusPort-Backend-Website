import { Request, Response } from 'express';
import { sendSuccess } from '../../shared/utils/response';

export class HealthController {
  public check(req: Request, res: Response): void {
    sendSuccess(res, {
      status: 'Healthy',
      service: 'NexusPort Node.js Core Service',
      version: '1.0.0',
      timestamp: new Date().toISOString(),
    });
  }
}
