import { Request, Response } from 'express';
import { Router } from 'express';
import { IdentityRepository } from '../infrastructure/identity.repository';
import { sendSuccess, sendCreated } from '../../../shared/utils/response';

export class IdentityController {
  private repository = new IdentityRepository();

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

export const createIdentityRouter = (): Router => {
  const router = Router();
  const controller = new IdentityController();

  router.get('/', controller.getAll);
  router.get('/:id', controller.getById);
  router.post('/', controller.create);

  return router;
};
