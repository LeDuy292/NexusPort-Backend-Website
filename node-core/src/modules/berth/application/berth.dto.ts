import { BerthEntity } from '../domain/berth.entity';

export interface CreateBerthDto {
  name: string;
  description?: string;
}

export interface BerthDto extends BerthEntity {}

export interface IBerthService {
  getAll(): Promise<BerthDto[]>;
  getById(id: string): Promise<BerthDto | null>;
  create(dto: CreateBerthDto): Promise<BerthDto>;
}
