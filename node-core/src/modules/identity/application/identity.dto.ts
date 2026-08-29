import { IdentityEntity } from '../domain/identity.entity';

export interface CreateIdentityDto {
  name: string;
  description?: string;
}

export interface IdentityDto extends IdentityEntity {}

export interface IIdentityService {
  getAll(): Promise<IdentityDto[]>;
  getById(id: string): Promise<IdentityDto | null>;
  create(dto: CreateIdentityDto): Promise<IdentityDto>;
}
