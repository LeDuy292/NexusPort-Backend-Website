import { Request, Response, NextFunction, Router } from 'express';
import { AuthService } from '../application/auth.service';

export class AuthController {
  constructor(private readonly authService = new AuthService()) {}

  login = async (req: Request, res: Response, next: NextFunction): Promise<void> => {
    try {
      const { username, password, rememberMe } = req.body;
      const result = await this.authService.login(username, password, !!rememberMe);
      res.status(200).json({
        success: true,
        message: 'Đăng nhập thành công.',
        data: result,
      });
    } catch (error) {
      next(error);
    }
  };
}

export const createAuthRouter = (): Router => {
  const router = Router();
  const controller = new AuthController();

  router.post('/login', controller.login);

  return router;
};
