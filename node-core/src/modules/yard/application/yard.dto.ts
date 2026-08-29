import { YardEntity } from '../domain/yard.entity';

export interface CreateYardDto {
  name: string;
  description?: string;
}

export interface YardDto extends YardEntity {}

export interface IYardService {
  getAll(): Promise<YardDto[]>;
  getById(id: string): Promise<YardDto | null>;
  create(dto: CreateYardDto): Promise<YardDto>;
}
