import { AppError, NotFoundError } from '../../../shared/errors/app-error';
import { CreateContainerDto, ContainerSearchDto, UpdateContainerDto } from './container.dto';
import { ContainerRepository } from '../infrastructure/container.repository';

type PostgresError = Error & { code?: string; constraint?: string };

export class ContainerService {
  constructor(private readonly repository = new ContainerRepository()) {}

  getAll(filters: ContainerSearchDto) { return this.repository.findAll(filters); }
  getTypes() { return this.repository.findTypes(); }

  async getById(id: string) {
    const container = await this.repository.findById(id);
    if (!container) throw new NotFoundError('Container', id);
    return container;
  }

  async create(dto: CreateContainerDto) {
    if (await this.repository.findByContainerNumber(dto.containerNumber)) {
      throw new AppError('Container ID already exists.', 409, 'CONTAINER_ID_DUPLICATE');
    }
    const containerType = await this.repository.findTypeById(dto.containerTypeId);
    if (!containerType) throw new NotFoundError('Container type', dto.containerTypeId);
    try {
      return await this.repository.create(dto, containerType.category);
    } catch (error) {
      this.handlePersistenceError(error as PostgresError);
    }
  }

  async update(id: string, dto: UpdateContainerDto) {
    const current = await this.getById(id);
    if (dto.containerNumber && dto.containerNumber !== current.containerNumber) {
      if (await this.repository.findByContainerNumber(dto.containerNumber)) {
        throw new AppError('Container ID already exists.', 409, 'CONTAINER_ID_DUPLICATE');
      }
    }
    const typeId = dto.containerTypeId ?? current.containerTypeId;
    const containerType = await this.repository.findTypeById(typeId);
    if (!containerType) throw new NotFoundError('Container type', typeId);
    try {
      return await this.repository.update(id, dto, containerType.category, dto.cargoType ?? current.cargoType);
    } catch (error) {
      this.handlePersistenceError(error as PostgresError);
    }
  }

  async softDelete(id: string) {
    const current = await this.getById(id);
    if (current.status !== 'canceled') await this.repository.softDelete(id);
  }

  private handlePersistenceError(error: PostgresError): never {
    if (error.code === '23505') throw new AppError('Container ID already exists.', 409, 'CONTAINER_ID_DUPLICATE');
    if (error.code === '23503') throw new AppError('A referenced carrier, vessel call, or container type does not exist.', 422, 'INVALID_REFERENCE');
    throw error;
  }
}
