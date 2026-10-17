import { Request, Response, NextFunction, Router } from 'express';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { UserService } from '../application/user.service';
import { UserRepository } from '../infrastructure/user.repository';
import { UnauthorizedError, ForbiddenError } from '../../../shared/errors/app-error';

const JWT_SECRET = process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!';

export class UserController {
  constructor(
    private readonly userService = new UserService(),
    private readonly userRepo = new UserRepository()
  ) {}

  private async getCarrierIdFromReq(req: Request): Promise<string> {
    const authHeader = req.headers.authorization;
    const token = authHeader?.match(/^Bearer\s+(.+)$/i)?.[1];
    if (!token) throw new UnauthorizedError('Chưa xác thực người dùng.');

    let payload: JwtPayload;
    try {
      payload = jwt.verify(token, JWT_SECRET) as JwtPayload;
    } catch {
      throw new UnauthorizedError('Phiên đăng nhập không hợp lệ hoặc đã hết hạn.');
    }

    const carrierId = payload.CarrierId || payload.carrierId || payload.carrier_id;
    if (carrierId) return String(carrierId);

    const userId = payload.id || payload.sub;
    if (userId) {
      const dbCarrierId = await this.userRepo.findCarrierIdByUserId(String(userId));
      if (dbCarrierId) return dbCarrierId;
    }

    throw new ForbiddenError('Tài khoản không gắn với công ty vận tải hợp lệ.');
  }

  getCarrierStaffs = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const carrierId = await this.getCarrierIdFromReq(req);
      const staffs = await this.userService.getCarrierStaffs(carrierId);
      res.status(200).json({
        success: true,
        data: { staffs },
      });
    } catch (error) {
      next(error);
    }
  };

  createCarrierStaff = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const carrierId = await this.getCarrierIdFromReq(req);
      const { username, email, password, fullName } = req.body;
      const staff = await this.userService.createCarrierStaff(
        { username, email, password, fullName },
        carrierId
      );
      res.status(201).json({
        success: true,
        message: 'Tạo tài khoản nhân viên thành công.',
        data: staff,
      });
    } catch (error) {
      next(error);
    }
  };

  activateUser = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const user = await this.userService.activateUser(req.params.id);
      res.status(200).json({
        success: true,
        message: 'Kích hoạt tài khoản thành công.',
        data: user,
      });
    } catch (error) {
      next(error);
    }
  };

  deactivateUser = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const user = await this.userService.deactivateUser(req.params.id);
      res.status(200).json({
        success: true,
        message: 'Vô hiệu hóa tài khoản thành công.',
        data: user,
      });
    } catch (error) {
      next(error);
    }
  };

  listUsers = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const result = await this.userService.listUsers(req.query);
      res.status(200).json({
        success: true,
        data: result,
      });
    } catch (error) {
      next(error);
    }
  };
}

export const createUserRouter = (): Router => {
  const router = Router();
  const controller = new UserController();

  router.get('/carrier-staff', controller.getCarrierStaffs);
  router.post('/carrier-staff', controller.createCarrierStaff);
  router.patch('/:id/activate', controller.activateUser);
  router.patch('/:id/deactivate', controller.deactivateUser);
  router.get('/', controller.listUsers);

  return router;
};
