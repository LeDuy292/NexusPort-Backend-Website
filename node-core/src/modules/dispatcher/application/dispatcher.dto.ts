import { DispatcherEntity } from '../domain/dispatcher.entity';

export interface CreateDispatcherDto {
  name: string;
  description?: string;
}

export interface DispatcherDto extends DispatcherEntity {}

export interface IDispatcherService {
  getAll(): Promise<DispatcherDto[]>;
  getById(id: string): Promise<DispatcherDto | null>;
  create(dto: CreateDispatcherDto): Promise<DispatcherDto>;
}
