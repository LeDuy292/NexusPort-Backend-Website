import { ContainerStatusService, toContainerStatus } from './container-status.service';
import { ContainerStatus, PersistedContainerStatus } from '../domain/container.entity';
import { ContainerRepository } from '../infrastructure/container.repository';

const persisted: Record<ContainerStatus, PersistedContainerStatus> = {
  [ContainerStatus.Registered]: 'expected',
  [ContainerStatus.Booked]: 'reserved',
  [ContainerStatus.GateIn]: 'gate_in',
  [ContainerStatus.InYard]: 'in_yard',
  [ContainerStatus.ReadyForGateOut]: 'moving',
  [ContainerStatus.GateOut]: 'gate_out',
};

const sequence = [
  ContainerStatus.Registered,
  ContainerStatus.Booked,
  ContainerStatus.GateIn,
  ContainerStatus.InYard,
  ContainerStatus.ReadyForGateOut,
  ContainerStatus.GateOut,
];

const buildRepository = (current: ContainerStatus) => ({
  findCurrentStatus: jest.fn().mockResolvedValue({ status: persisted[current], updatedAt: new Date('2026-09-05T00:00:00Z') }),
  transitionStatus: jest.fn().mockResolvedValue({ updatedAt: new Date('2026-09-05T01:00:00Z') }),
  findStatusHistory: jest.fn().mockResolvedValue([]),
}) as unknown as jest.Mocked<ContainerRepository>;

describe('ContainerStatusService', () => {
  it.each(sequence.slice(0, -1).map((fromStatus, index) => [fromStatus, sequence[index + 1]]))(
    'allows the lifecycle transition from %s to %s',
    async (fromStatus, toStatus) => {
      const repository = buildRepository(fromStatus);
      const service = new ContainerStatusService(repository);

      const result = await service.transition(
        '12d9ae31-b06b-455a-9666-9fe6752bc493',
        toStatus,
        '5ce5cd9b-1da8-441e-8442-04b9cb7f76c7',
      );

      expect(result.status).toBe(toStatus);
      expect(repository.transitionStatus).toHaveBeenCalledWith(expect.objectContaining({
        expectedStatus: persisted[fromStatus],
        targetStatus: persisted[toStatus],
        fromStatus,
        toStatus,
      }));
    },
  );

  it('rejects a skipped lifecycle transition without writing history', async () => {
    const repository = buildRepository(ContainerStatus.Registered);
    const service = new ContainerStatusService(repository);

    await expect(service.transition(
      '12d9ae31-b06b-455a-9666-9fe6752bc493',
      ContainerStatus.GateIn,
      '5ce5cd9b-1da8-441e-8442-04b9cb7f76c7',
    )).rejects.toMatchObject({ errorCode: 'INVALID_STATUS_TRANSITION', statusCode: 409 });
    expect(repository.transitionStatus).not.toHaveBeenCalled();
  });

  it('maps existing PostgreSQL lifecycle values to the business enum', () => {
    for (const status of sequence) expect(toContainerStatus(persisted[status])).toBe(status);
  });

  it('rejects legacy statuses that are outside this lifecycle', () => {
    expect(() => toContainerStatus('damaged')).toThrow(expect.objectContaining({
      errorCode: 'UNMANAGED_CONTAINER_STATUS',
    }));
  });
});
