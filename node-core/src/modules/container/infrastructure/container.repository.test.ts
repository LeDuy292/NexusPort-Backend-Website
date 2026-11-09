import { getClient } from '../../../infrastructure/database/db';
import { ContainerStatus } from '../domain/container.entity';
import { ContainerRepository } from './container.repository';

jest.mock('../../../infrastructure/database/db', () => ({ getClient: jest.fn(), query: jest.fn() }));

describe('ContainerRepository.transitionStatus', () => {
  it('casts the reused status parameter as the PostgreSQL enum', async () => {
    const updatedAt = new Date('2026-09-16T00:00:00Z');
    const client = {
      query: jest.fn()
        .mockResolvedValueOnce({})
        .mockResolvedValueOnce({ rows: [{ status: 'expected' }] })
        .mockResolvedValueOnce({ rows: [{ updatedAt }] })
        .mockResolvedValueOnce({})
        .mockResolvedValueOnce({}),
      release: jest.fn(),
    };
    (getClient as jest.Mock).mockResolvedValue(client);

    const result = await new ContainerRepository().transitionStatus({
      containerId: 'a0fba4cf-c181-4d0f-9c4a-257fa0e7fd28',
      expectedStatus: 'expected',
      targetStatus: 'reserved',
      fromStatus: ContainerStatus.Registered,
      toStatus: ContainerStatus.Booked,
      userId: '5ce5cd9b-1da8-441e-8442-04b9cb7f76c7',
      ipAddress: null,
    });

    expect(result).toEqual({ updatedAt });
    const updateSql = client.query.mock.calls[2][0] as string;
    expect(updateSql).toContain('status = $2::container_status');
    expect(updateSql).toContain("$2::container_status = 'gate_in'");
    expect(updateSql).toContain("$2::container_status = 'gate_out'");
    expect(client.query.mock.calls[4][0]).toBe('COMMIT');
    expect(client.release).toHaveBeenCalled();
  });
});
