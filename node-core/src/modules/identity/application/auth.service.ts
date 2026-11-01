import bcrypt from 'bcryptjs';
import jwt from 'jsonwebtoken';
import { UserRepository } from '../infrastructure/user.repository';
import { DbUser, LoginResult, SafeUser } from '../domain/user.types';
import { UnauthorizedError, ForbiddenError, BadRequestError } from '../../../shared/errors/app-error';

const JWT_SECRET = process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!';
const JWT_ISSUER = 'NexusPort';
const JWT_AUDIENCE = 'NexusPortClients';

export class AuthService {
  constructor(private readonly userRepo = new UserRepository()) {}

  private toSafeUser(u: DbUser): SafeUser {
    return {
      id: u.id,
      username: u.username,
      email: u.email,
      role: u.role,
      fullName: u.full_name,
      isActive: u.is_active,
      createdAt: u.created_at,
      updatedAt: u.updated_at,
    };
  }

  async login(usernameOrEmail: string, password: string, rememberMe = false): Promise<LoginResult> {
    if (!usernameOrEmail || !password) {
      throw new BadRequestError('Vui lòng nhập tên đăng nhập và mật khẩu.');
    }

    const user = await this.userRepo.findByUsernameOrEmail(usernameOrEmail);
    if (!user || !user.password) {
      throw new UnauthorizedError('Tài khoản không tồn tại!');
    }

    if (!user.is_active) {
      throw new ForbiddenError('Tài khoản đã bị vô hiệu hóa. Vui lòng liên hệ quản trị viên.');
    }

    const isMatch = await bcrypt.compare(password, user.password);
    if (!isMatch) {
      throw new UnauthorizedError('Mật khẩu không chính xác!');
    }

    let carrierId: string | null = null;
    const lowerRole = user.role.toLowerCase();
    if (lowerRole.includes('transport') || lowerRole.includes('carrier')) {
      carrierId = await this.userRepo.findCarrierIdByUserId(user.id);
    }

    const payload: Record<string, unknown> = {
      id: user.id,
      sub: user.id,
      'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier': user.id,
      username: user.username,
      name: user.username,
      email: user.email,
      role: user.role,
      'http://schemas.microsoft.com/ws/2008/06/identity/claims/role': user.role,
      ...(carrierId && { CarrierId: carrierId, carrier_id: carrierId }),
    };

    const expiresIn = rememberMe ? '30d' : '8h';
    const token = jwt.sign(payload, JWT_SECRET, {
      expiresIn,
      issuer: JWT_ISSUER,
      audience: JWT_AUDIENCE,
    });

    return {
      token,
      user: this.toSafeUser(user),
    };
  }
}
