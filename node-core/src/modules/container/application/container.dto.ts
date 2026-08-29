import { ContainerEntity } from '../domain/container.entity';

export interface CreateContainerDto {
  name: string;
  description?: string;
}

export interface ContainerDto extends ContainerEntity {}

export interface IContainerService {
  getAll(): Promise<ContainerDto[]>;
  getById(id: string): Promise<ContainerDto | null>;
  create(dto: CreateContainerDto): Promise<ContainerDto>;
}
