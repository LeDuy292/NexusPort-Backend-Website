import { VesselEntity } from '../domain/vessel.entity';

export interface CreateVesselDto {
  name: string;
  description?: string;
}

export interface VesselDto extends VesselEntity {}

export interface IVesselService {
  getAll(): Promise<VesselDto[]>;
  getById(id: string): Promise<VesselDto | null>;
  create(dto: CreateVesselDto): Promise<VesselDto>;
}
