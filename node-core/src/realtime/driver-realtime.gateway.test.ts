import { yardOperationCompletedSchema } from './driver-realtime.gateway';

describe('yard operation realtime event contract', () => {
  const validEvent = {
    eventId: '11111111-1111-4111-8111-111111111111',
    operationId: '22222222-2222-4222-8222-222222222222',
    containerId: '33333333-3333-4333-8333-333333333333',
    driverId: '44444444-4444-4444-8444-444444444444',
    operationStatus: 'Completed',
    completedAt: '2026-09-13T10:00:00.000Z',
  };

  it('accepts the required driver notification fields', () => {
    expect(yardOperationCompletedSchema.safeParse(validEvent).success).toBe(true);
  });

  it('rejects a missing container ID or non-completed status', () => {
    expect(yardOperationCompletedSchema.safeParse({ ...validEvent, containerId: undefined }).success).toBe(false);
    expect(yardOperationCompletedSchema.safeParse({ ...validEvent, operationStatus: 'Pending' }).success).toBe(false);
  });
});
