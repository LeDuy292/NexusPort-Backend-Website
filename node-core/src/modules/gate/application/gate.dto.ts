import { GateEntity } from '../domain/gate.entity';

export interface CreateGateDto {
  name: string;
  description?: string;
}

export interface GateDto extends GateEntity {}

export interface IGateService {
  getAll(): Promise<GateDto[]>;
  getById(id: string): Promise<GateDto | null>;
  create(dto: CreateGateDto): Promise<GateDto>;
}
