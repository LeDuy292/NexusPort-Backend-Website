import { VehicleEntity } from '../domain/vehicle.entity';

export interface CreateVehicleDto {
  name: string;
  description?: string;
}

export interface VehicleDto extends VehicleEntity {}

export interface IVehicleService {
  getAll(): Promise<VehicleDto[]>;
  getById(id: string): Promise<VehicleDto | null>;
  create(dto: CreateVehicleDto): Promise<VehicleDto>;
}
