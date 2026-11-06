import { AppError } from '../../../shared/errors/app-error';
import { CreateContainerDto, ContainerSearchDto, UpdateContainerDto } from './container.dto';
import { ContainerRepository } from '../infrastructure/container.repository';

type PostgresError = Error & { code?: string; constraint?: string };

export class ContainerService {
  constructor(private readonly repository = new ContainerRepository()) {}

  getAll(filters: ContainerSearchDto) { return this.repository.findAll(filters); }
  getTypes() { return this.repository.findTypes(); }

  async getById(id: string) {
    const container = await this.repository.findById(id);
    if (!container) throw new AppError(`Không tìm thấy Container có ID '${id}'.`, 404, 'CONTAINER_NOT_FOUND');
    return container;
  }

  async create(dto: CreateContainerDto) {
    if (await this.repository.findByContainerNumber(dto.containerNumber)) {
      throw new AppError('Mã Container đã tồn tại.', 409, 'CONTAINER_ID_DUPLICATE');
    }
    const containerType = await this.repository.findTypeById(dto.containerTypeId);
    if (!containerType) throw new AppError(`Không tìm thấy loại Container có ID '${dto.containerTypeId}'.`, 404, 'CONTAINER_TYPE_NOT_FOUND');
    try {
      return await this.repository.create(dto, containerType.category);
    } catch (error) {
      this.handlePersistenceError(error as PostgresError);
    }
  }

  async update(id: string, dto: UpdateContainerDto) {
    const current = await this.getById(id);
    if (dto.status !== undefined && dto.status !== current.status) {
      throw new AppError(
        'Trạng thái Container chỉ được thay đổi thông qua chức năng chuyển trạng thái.',
        409,
        'STATUS_TRANSITION_REQUIRED',
      );
    }
    if (dto.containerNumber && dto.containerNumber !== current.containerNumber) {
      if (await this.repository.findByContainerNumber(dto.containerNumber)) {
        throw new AppError('Mã Container đã tồn tại.', 409, 'CONTAINER_ID_DUPLICATE');
      }
    }
    const typeId = dto.containerTypeId ?? current.containerTypeId;
    const containerType = await this.repository.findTypeById(typeId);
    if (!containerType) throw new AppError(`Không tìm thấy loại Container có ID '${typeId}'.`, 404, 'CONTAINER_TYPE_NOT_FOUND');
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
    if (error.code === '23505') throw new AppError('Mã Container đã tồn tại.', 409, 'CONTAINER_ID_DUPLICATE');
    if (error.code === '23503') throw new AppError('Carrier, chuyến tàu hoặc loại Container được tham chiếu không tồn tại.', 422, 'INVALID_REFERENCE');
    throw error;
  }
}
