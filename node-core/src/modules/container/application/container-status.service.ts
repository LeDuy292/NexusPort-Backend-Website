import { AppError } from '../../../shared/errors/app-error';
import {
  ContainerCurrentStatus,
  ContainerStatus,
  PersistedContainerStatus,
} from '../domain/container.entity';
import { ContainerRepository } from '../infrastructure/container.repository';

const nextStatus: Record<ContainerStatus, ContainerStatus | null> = {
  [ContainerStatus.Registered]: ContainerStatus.Booked,
  [ContainerStatus.Booked]: ContainerStatus.GateIn,
  [ContainerStatus.GateIn]: ContainerStatus.InYard,
  [ContainerStatus.InYard]: ContainerStatus.ReadyForGateOut,
  [ContainerStatus.ReadyForGateOut]: ContainerStatus.GateOut,
  [ContainerStatus.GateOut]: null,
};

const statusLabel: Record<ContainerStatus, string> = {
  [ContainerStatus.Registered]: 'Đã đăng ký',
  [ContainerStatus.Booked]: 'Đã đặt lịch',
  [ContainerStatus.GateIn]: 'Đã vào cổng',
  [ContainerStatus.InYard]: 'Trong bãi',
  [ContainerStatus.ReadyForGateOut]: 'Sẵn sàng ra cổng',
  [ContainerStatus.GateOut]: 'Đã ra cổng',
};

const persistedByStatus: Record<ContainerStatus, PersistedContainerStatus> = {
  [ContainerStatus.Registered]: 'expected',
  [ContainerStatus.Booked]: 'reserved',
  [ContainerStatus.GateIn]: 'gate_in',
  [ContainerStatus.InYard]: 'in_yard',
  [ContainerStatus.ReadyForGateOut]: 'moving',
  [ContainerStatus.GateOut]: 'gate_out',
};

const statusByPersisted: Partial<Record<PersistedContainerStatus, ContainerStatus>> =
  Object.fromEntries(Object.entries(persistedByStatus).map(([status, persisted]) => [persisted, status])) as
    Partial<Record<PersistedContainerStatus, ContainerStatus>>;

export const toContainerStatus = (status: PersistedContainerStatus): ContainerStatus => {
  const lifecycleStatus = statusByPersisted[status];
  if (!lifecycleStatus) {
    throw new AppError(
      `Trạng thái Container '${status}' không thuộc quy trình cổng đang được quản lý.`,
      409,
      'UNMANAGED_CONTAINER_STATUS',
    );
  }
  return lifecycleStatus;
};

export class ContainerStatusService {
  constructor(private readonly repository = new ContainerRepository()) {}

  async getCurrentStatus(containerId: string): Promise<ContainerCurrentStatus> {
    const current = await this.repository.findCurrentStatus(containerId);
    if (!current) throw new AppError(`Không tìm thấy Container có ID '${containerId}'.`, 404, 'CONTAINER_NOT_FOUND');
    return {
      containerId,
      status: toContainerStatus(current.status),
      persistedStatus: current.status,
      updatedAt: current.updatedAt,
    };
  }

  async getHistory(containerId: string) {
    if (!(await this.repository.findCurrentStatus(containerId))) {
      throw new AppError(`Không tìm thấy Container có ID '${containerId}'.`, 404, 'CONTAINER_NOT_FOUND');
    }
    return this.repository.findStatusHistory(containerId);
  }

  async transition(containerId: string, target: ContainerStatus, userId: string, ipAddress?: string | null) {
    const current = await this.getCurrentStatus(containerId);
    const allowedTarget = nextStatus[current.status];
    if (allowedTarget !== target) {
      throw new AppError(
        `Không thể chuyển trạng thái Container từ '${statusLabel[current.status]}' sang '${statusLabel[target]}'.`,
        409,
        'INVALID_STATUS_TRANSITION',
      );
    }

    const result = await this.repository.transitionStatus({
      containerId,
      expectedStatus: current.persistedStatus,
      targetStatus: persistedByStatus[target],
      fromStatus: current.status,
      toStatus: target,
      userId,
      ipAddress: ipAddress ?? null,
    });
    if (!result) throw new AppError(`Không tìm thấy Container có ID '${containerId}'.`, 404, 'CONTAINER_NOT_FOUND');
    if (result.conflictStatus) {
      throw new AppError(
        'Trạng thái Container vừa được thay đổi bởi thao tác khác. Vui lòng tải lại và thử lại.',
        409,
        'STATUS_TRANSITION_CONFLICT',
      );
    }
    return {
      containerId,
      status: target,
      persistedStatus: persistedByStatus[target],
      updatedAt: result.updatedAt!,
    } satisfies ContainerCurrentStatus;
  }
}
