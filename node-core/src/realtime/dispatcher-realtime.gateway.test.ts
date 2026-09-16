import { dispatcherStatusUpdatedSchema } from './driver-realtime.gateway';

describe('dispatcher realtime event contract', () => {
  const validEvent = {
    eventId: '11111111-1111-4111-8111-111111111111',
    domain: 'gate',
    entityId: 'gate-in-001',
    status: 'Approved',
    occurredAt: '2026-09-14T08:00:00.000Z',
    label: 'Gate A',
    metadata: { source: 'gate-service' },
  };

  it('accepts supported operational domains', () => {
    expect(dispatcherStatusUpdatedSchema.safeParse(validEvent).success).toBe(true);
    expect(dispatcherStatusUpdatedSchema.safeParse({ ...validEvent, domain: 'equipment' }).success).toBe(true);
  });

  it('rejects unknown domains and missing status', () => {
    expect(dispatcherStatusUpdatedSchema.safeParse({ ...validEvent, domain: 'unknown' }).success).toBe(false);
    expect(dispatcherStatusUpdatedSchema.safeParse({ ...validEvent, status: '' }).success).toBe(false);
  });
});
