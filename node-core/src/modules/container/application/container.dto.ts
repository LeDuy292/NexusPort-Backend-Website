import {
  CargoType, ContainerCategory, ContainerDetailEntity, ContainerEntity,
  ContainerSize, ContainerTypeEntity, PersistedContainerStatus,
} from '../domain/container.entity';

export interface CreateContainerDto {
  containerNumber: string;
  containerTypeId: string;
  sealNumber: string;
  carrierId?: string | null;
  vesselCallId?: string | null;
  cargoType?: CargoType;
  status?: PersistedContainerStatus;
  grossWeightKg?: number | null;
  expectedGateOutAt?: string | null;
}

export type UpdateContainerDto = Partial<Omit<CreateContainerDto, 'containerNumber'>> & {
  containerNumber?: string;
  arrivedAt?: string | null;
  leftAt?: string | null;
};

export interface ContainerSearchDto {
  page: number;
  limit: number;
  search?: string;
  status?: PersistedContainerStatus;
  containerTypeId?: string;
  size?: ContainerSize;
  category?: ContainerCategory;
  cargoType?: CargoType;
  carrierId?: string;
  includeCanceled: boolean;
  sortBy: 'containerNumber' | 'status' | 'createdAt' | 'updatedAt';
  sortOrder: 'asc' | 'desc';
}

export interface ContainerListItem extends ContainerEntity {
  typeCode: string;
  size: ContainerSize;
  category: ContainerCategory;
  carrierName: string | null;
  bookingCount: number;
}

export interface ContainerListResult {
  items: ContainerListItem[];
  total: number;
  page: number;
  limit: number;
  totalPages: number;
}

export type ContainerDto = ContainerEntity;
export type ContainerDetailDto = ContainerDetailEntity;
export type ContainerTypeDto = ContainerTypeEntity;

export interface TransitionContainerStatusDto {
  status: import('../domain/container.entity').ContainerStatus;
}
