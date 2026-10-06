import { ValidationError } from '../../../shared/errors/app-error';
import { YardContainerRepository } from '../infrastructure/yard-container.repository';
import { YardContainerService } from './yard-container.service';

const containerId = '5ce5cd9b-1da8-441e-8442-04b9cb7f76c7';
const slotId = '6ce5cd9b-1da8-441e-8442-04b9cb7f76c8';
const actor = { id: '7ce5cd9b-1da8-441e-8442-04b9cb7f76c9', ipAddress: '127.0.0.1' };

const repositoryMock = () => ({
  listAvailableSlots: jest.fn().mockResolvedValue([]),
  confirmReception: jest.fn().mockResolvedValue({ containerId, status: 'discharged' }),
  reserveSlot: jest.fn().mockResolvedValue({ containerId, slotId }),
  placeContainer: jest.fn().mockResolvedValue({ containerId, slotId, status: 'in_yard' }),
}) as unknown as jest.Mocked<YardContainerRepository>;

describe('YardContainerService', () => {
  it('normalizes and verifies the scanned Container ID before reception', async () => {
    const repository = repositoryMock();
    const service = new YardContainerService(repository);

    await service.confirmReception(containerId, {
      containerNumber: 'emcu 836179 5', sealNumber: ' SEAL-01 ', condition: 'sound',
    }, actor);

    expect(repository.confirmReception).toHaveBeenCalledWith(containerId, expect.objectContaining({
      containerNumber: 'EMCU8361795', sealNumber: 'SEAL-01', condition: 'sound',
    }), actor);
  });

  it('requires damage notes so the inspected condition is not silently lost', async () => {
    const service = new YardContainerService(repositoryMock());

    await expect(service.confirmReception(containerId, {
      containerNumber: 'EMCU8361795', sealNumber: 'SEAL-01', condition: 'damaged',
    }, actor)).rejects.toMatchObject<Partial<ValidationError>>({
      errorCode: 'VALIDATION_ERROR', errors: { conditionNotes: expect.any(Array) },
    });
  });

  it('rejects an invalid slot before reserving or placing it', async () => {
    const repository = repositoryMock();
    const service = new YardContainerService(repository);

    await expect(service.reserveSlot(containerId, { slotId: 'not-a-uuid' }, actor))
      .rejects.toMatchObject({ errorCode: 'VALIDATION_ERROR' });
    await expect(service.placeContainer(containerId, { slotId: 'not-a-uuid' }, actor))
      .rejects.toMatchObject({ errorCode: 'VALIDATION_ERROR' });
    expect(repository.reserveSlot).not.toHaveBeenCalled();
    expect(repository.placeContainer).not.toHaveBeenCalled();
  });

  it('passes a valid reservation and placement to the transactional repository', async () => {
    const repository = repositoryMock();
    const service = new YardContainerService(repository);

    await service.reserveSlot(containerId, { slotId }, actor);
    await service.placeContainer(containerId, { slotId }, actor);

    expect(repository.reserveSlot).toHaveBeenCalledWith(containerId, slotId, actor);
    expect(repository.placeContainer).toHaveBeenCalledWith(containerId, slotId, actor);
  });
});
