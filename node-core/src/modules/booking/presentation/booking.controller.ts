import { Request, Response } from 'express';
import { Router } from 'express';
import { BookingRepository } from '../infrastructure/booking.repository';
import { sendSuccess, sendCreated } from '../../../shared/utils/response';

export class BookingController {
  private repository = new BookingRepository();

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

export const createBookingRouter = (): Router => {
  const router = Router();
  const controller = new BookingController();

  router.get('/', controller.getAll);
  router.get('/:id', controller.getById);
  router.post('/', controller.create);

  return router;
};
