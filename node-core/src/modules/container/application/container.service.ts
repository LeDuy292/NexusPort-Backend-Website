import { AppError, ForbiddenError, ValidationError } from '../../../shared/errors/app-error';
import { ContainerSearchDto, RegisterContainerIdentityDto, UpdateContainerDto } from './container.dto';
import { containerIsoCatalog } from '../domain/container-iso-catalog';
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

  async getRegistrationIsoTypes() {
    const types = new Map((await this.getTypes()).map(type => [type.code, type]));
    return containerIsoCatalog.flatMap(option => {
      const type = types.get(option.containerTypeCode);
      return type ? [{
        ...option, containerTypeId: type.id, containerTypeDescription: type.description, category: type.category,
      }] : [];
    });
  }

  async registerIdentity(dto: RegisterContainerIdentityDto, userId: string) {
    const option = (await this.getRegistrationIsoTypes()).find(type => type.isoSizeType === dto.isoSizeType);
    if (!option) {
      throw new ValidationError('Mã ISO chưa có trong danh mục đăng ký.', {
        isoSizeType: ['Chọn mã ISO kích thước/loại được hỗ trợ.'],
      });
    }
    const { category, containerTypeCode, containerTypeId, externalLengthMm, externalWidthMm, externalHeightMm } = option;
    const expected = { containerTypeId, externalLengthMm, externalWidthMm, externalHeightMm };
    const errors: Record<string, string[]> = {};
    if (dto.containerTypeId && dto.containerTypeId.toLowerCase() !== containerTypeId)
      errors.containerTypeId = [`Mã ISO ${dto.isoSizeType} tương ứng loại ${containerTypeCode}.`];
    for (const [field, label] of [['externalLengthMm', 'Chiều dài'], ['externalWidthMm', 'Chiều rộng'], ['externalHeightMm', 'Chiều cao']] as const) {
      if (dto[field] !== undefined && dto[field] !== expected[field])
        errors[field] = [`${label} danh định của mã ISO ${dto.isoSizeType} là ${expected[field]} mm.`];
    }
    if (Object.keys(errors).length)
      throw new ValidationError('Loại hoặc kích thước không khớp mã ISO.', errors);

    const resolved = { ...dto, ...expected };
    try {
      const id = await this.repository.createIdentity(resolved, category === 'reefer', userId);
      if (!id) throw new ForbiddenError('Tài khoản cần thuộc hãng tàu đã đăng ký tiền tố BIC của container này.');
      return { id, ...resolved, containerTypeCode };
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
