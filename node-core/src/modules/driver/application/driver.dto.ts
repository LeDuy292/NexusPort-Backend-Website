import { DriverEntity } from '../domain/driver.entity';

export interface CreateDriverDto {
  name: string;
  description?: string;
}

export interface DriverDto extends DriverEntity {}

export interface IDriverService {
  getAll(): Promise<DriverDto[]>;
  getById(id: string): Promise<DriverDto | null>;
  create(dto: CreateDriverDto): Promise<DriverDto>;
}
