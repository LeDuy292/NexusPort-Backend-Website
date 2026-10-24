import { DestinationService } from './destination.service';
import { DestinationRepository } from '../infrastructure/destination.repository';

describe('DestinationService', () => {
  const operationId = '11111111-1111-4111-8111-111111111111';
  const slotId = '22222222-2222-4222-8222-222222222222';

  it('lists only supported Yard slot statuses', async () => {
    const repository = { findYardDestinations: jest.fn().mockResolvedValue([]) } as unknown as DestinationRepository;
    const service = new DestinationService(repository);

    await service.list('empty');

    expect(repository.findYardDestinations).toHaveBeenCalledWith('empty');
    await expect(service.list('broken')).rejects.toMatchObject({ statusCode: 422, errorCode: 'VALIDATION_ERROR' });
  });

  it('passes the authenticated Dispatcher identity into an assignment', async () => {
    const assignment = { operationId, destination: { slotId } };
    const repository = { assign: jest.fn().mockResolvedValue(assignment) } as unknown as DestinationRepository;
    const service = new DestinationService(repository);

    await expect(service.assign(operationId, { slotId }, { id: null, name: 'dispatcher01' })).resolves.toBe(assignment);
    expect(repository.assign).toHaveBeenCalledWith({
      operationId, slotId, dispatcherId: null, dispatcherName: 'dispatcher01',
    });
  });

  it('rejects invalid identifiers and extra request fields', async () => {
    const repository = { assign: jest.fn() } as unknown as DestinationRepository;
    const service = new DestinationService(repository);

    await expect(service.assign('invalid', { slotId }, { id: null, name: 'dispatcher' }))
      .rejects.toMatchObject({ statusCode: 422 });
    await expect(service.assign(operationId, { slotId, driverId: operationId }, { id: null, name: 'dispatcher' }))
      .rejects.toMatchObject({ statusCode: 422 });
    expect(repository.assign).not.toHaveBeenCalled();
  });
});
