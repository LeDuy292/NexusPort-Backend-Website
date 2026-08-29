import { EquipmentEntity } from '../domain/equipment.entity';

export interface CreateEquipmentDto {
  name: string;
  description?: string;
}

export interface EquipmentDto extends EquipmentEntity {}

export interface IEquipmentService {
  getAll(): Promise<EquipmentDto[]>;
  getById(id: string): Promise<EquipmentDto | null>;
  create(dto: CreateEquipmentDto): Promise<EquipmentDto>;
}
