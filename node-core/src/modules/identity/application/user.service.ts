import bcrypt from 'bcryptjs';
import { UserRepository } from '../infrastructure/user.repository';
import { CarrierStaffUser, SafeUser } from '../domain/user.types';
import { BadRequestError, ConflictError, NotFoundError } from '../../../shared/errors/app-error';

export class UserService {
  constructor(private readonly userRepo = new UserRepository()) {}

  async createCarrierStaff(
    data: { username: string; email: string; password: string; fullName?: string },
    carrierId: string
  ): Promise<SafeUser> {
    if (!data.username || !data.email || !data.password) {
      throw new BadRequestError('Vui lòng cung cấp đầy đủ thông tin tài khoản.');
    }

    const existing = await this.userRepo.findByUsernameOrEmail(data.username);
    if (existing) {
      throw new ConflictError('Tên đăng nhập hoặc email đã tồn tại trong hệ thống.');
    }

    const passwordHash = await bcrypt.hash(data.password, 10);
    const createdUser = await this.userRepo.createUser({
      username: data.username,
      email: data.email,
      passwordHash,
      role: 'Carrier Staff',
      fullName: data.fullName,
      isActive: true,
    });

    await this.userRepo.linkCarrierUser(carrierId, createdUser.id);

    return {
      id: createdUser.id,
      username: createdUser.username,
      email: createdUser.email,
      role: createdUser.role,
      fullName: createdUser.full_name,
      isActive: createdUser.is_active,
      createdAt: createdUser.created_at,
      updatedAt: createdUser.updated_at,
    };
  }

  async getCarrierStaffs(carrierId: string): Promise<CarrierStaffUser[]> {
    return this.userRepo.getCarrierStaffs(carrierId);
  }

  async activateUser(id: string): Promise<SafeUser> {
    const user = await this.userRepo.updateActiveStatus(id, true);
    if (!user) throw new NotFoundError('Không tìm thấy người dùng.');
    return {
      id: user.id,
      username: user.username,
      email: user.email,
      role: user.role,
      fullName: user.full_name,
      isActive: user.is_active,
      createdAt: user.created_at,
      updatedAt: user.updated_at,
    };
  }

  async deactivateUser(id: string): Promise<SafeUser> {
    const user = await this.userRepo.updateActiveStatus(id, false);
    if (!user) throw new NotFoundError('Không tìm thấy người dùng.');
    return {
      id: user.id,
      username: user.username,
      email: user.email,
      role: user.role,
      fullName: user.full_name,
      isActive: user.is_active,
      createdAt: user.created_at,
      updatedAt: user.updated_at,
    };
  }

  async listUsers(options: {
    search?: string;
    role?: string;
    status?: string;
    page?: number;
    limit?: number;
  }) {
    const result = await this.userRepo.listUsers(options);
    return {
      ...result,
      users: result.users.map((u) => ({
        id: u.id,
        username: u.username,
        email: u.email,
        role: u.role,
        fullName: u.full_name,
        isActive: u.is_active,
        createdAt: u.created_at,
        updatedAt: u.updated_at,
      })),
    };
  }
}
