import { ConflictError } from '../../../shared/errors/app-error';
import { assertYardSlotCompatible } from './yard-container.repository';

const container = {
  id: 'container-1', containerNumber: 'EMCU8361795', sealNumber: 'SEAL-01',
  status: 'discharged', grossWeightKg: 30000, isReefer: false,
  isDangerous: false, isOversized: false, movementType: 'vessel_discharge',
  visitId: 'visit-1', visitSealNumber: 'SEAL-01',
};

const slot = {
  slotId: 'slot-1', blockId: 'block-1', blockCode: 'A01', blockName: 'Block A', zone: null,
  bay: 1, row: 1, tier: 1, status: 'empty', maxWeightKg: 35000,
  hasReeferPlug: false, reservedForContainer: false,
  isReeferArea: false, isDangerousArea: false, isOversizedArea: false,
  activeReservationContainerId: null, currentContainerId: null, unsupportedTier: false,
  unoccupiedLowerTier: false,
} as const;

describe('Yard slot compatibility', () => {
  it('accepts an empty supported slot within its weight limit', () => {
    expect(() => assertYardSlotCompatible(container, slot)).not.toThrow();
  });

  it.each([
    [{ ...container, grossWeightKg: 36000 }, slot, 'tải trọng'],
    [{ ...container, isReefer: true }, slot, 'khu lạnh'],
    [{ ...container, isDangerous: true }, slot, 'hàng nguy hiểm'],
    [{ ...container, isOversized: true }, slot, 'quá khổ'],
    [container, { ...slot, unsupportedTier: true }, 'tầng bên dưới'],
    [container, { ...slot, status: 'maintenance' }, 'maintenance'],
    [container, { ...slot, activeReservationContainerId: 'another-container' }, 'đặt trước'],
    [container, { ...slot, currentContainerId: 'another-container' }, 'Container khác'],
  ])('rejects an unsuitable slot: %s', (candidateContainer, candidateSlot, message) => {
    expect(() => assertYardSlotCompatible(candidateContainer, candidateSlot as never))
      .toThrow(expect.objectContaining<Partial<ConflictError>>({ statusCode: 409, message: expect.stringContaining(message) }));
  });

  it('accepts a reserved slot when the reservation belongs to the same Container', () => {
    expect(() => assertYardSlotCompatible(container, {
      ...slot, status: 'reserved', activeReservationContainerId: container.id,
    })).not.toThrow();
  });

  it('allows planning above a reserved lower tier but blocks physical placement until it is occupied', () => {
    const plannedUpperSlot = { ...slot, tier: 2, unoccupiedLowerTier: true };
    expect(() => assertYardSlotCompatible(container, plannedUpperSlot)).not.toThrow();
    expect(() => assertYardSlotCompatible(container, plannedUpperSlot, true))
      .toThrow('tầng bên dưới chưa có Container');
  });
});
