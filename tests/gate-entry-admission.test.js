'use strict';

jest.mock('../src/config/database', () => ({ sequelize: { query: jest.fn(), transaction: jest.fn() } }));
jest.mock('../src/modules/booking-dispatch/yard-admission', () => ({
  lock: jest.fn(), requireSpace: jest.fn(), assignDestination: jest.fn(),
}));

const { sequelize } = require('../src/config/database');
const yard = require('../src/modules/booking-dispatch/yard-admission');
const { authorizeEntry, claimInboundHardwareOpen } = require('../src/modules/gate-iot/gate-entry-admission');

const transaction = {};
const scan = { vehiclePlate: '43C-123.45', gateCode: 'GATE-01' };
const booking = { id: 'booking', booking_code: 'BK-1', driver_id: 'driver', truck_id: 'truck', on_time: true };

beforeEach(() => {
  jest.resetAllMocks();
  sequelize.transaction.mockImplementation(async (_options, run) => run(transaction));
  yard.assignDestination.mockResolvedValue(true);
});

function bookingAtGate(candidate) {
  return sequelize.query.mockResolvedValueOnce([{ id: 'gate' }]).mockResolvedValueOnce([candidate]);
}

test('on-time scan uses the shared yard lock and creates a gate order without driver acknowledgment', async () => {
  bookingAtGate(booking).mockResolvedValueOnce([]).mockResolvedValueOnce([])
    .mockResolvedValueOnce([{ booking_id: booking.id }])
    .mockResolvedValueOnce([{ id: 'gate-order', source: 'gate_scan' }]);
  await expect(authorizeEntry('officer', scan)).resolves.toMatchObject({
    bookingCode: 'BK-1', orderId: 'gate-order', source: 'gate_scan',
  });
  expect(yard.lock).toHaveBeenCalledWith(transaction);
  expect(yard.requireSpace).toHaveBeenCalledWith(transaction);
  expect(yard.assignDestination).toHaveBeenCalledWith('booking', transaction);
  const stateWrite = sequelize.query.mock.calls[4];
  expect(stateWrite[0]).toContain("'gate_admitted'");
  expect(stateWrite[0]).toContain('acknowledged_at=NULL');
  expect(stateWrite[1].replacements.userId).toBe('officer');
});

test('outside the appointment a scan reuses an active dispatch order and otherwise denies entry', async () => {
  bookingAtGate({ ...booking, on_time: false });
  await expect(authorizeEntry('officer', scan)).rejects.toThrow('ngoài khung giờ');
  expect(yard.requireSpace).not.toHaveBeenCalled();
  bookingAtGate({ ...booking, on_time: false, order_id: 'order', source: 'dispatch' });
  await expect(authorizeEntry('officer', scan)).resolves.toMatchObject({ orderId: 'order', source: 'dispatch' });
  expect(yard.assignDestination).not.toHaveBeenCalled();
});

test('full yard rejects a new direct order while an existing pending reservation can be reused', async () => {
  bookingAtGate(booking).mockResolvedValueOnce([]).mockResolvedValueOnce([]);
  yard.requireSpace.mockRejectedValueOnce(new Error('full'));
  await expect(authorizeEntry('officer', scan)).rejects.toThrow('full');
  expect(yard.assignDestination).not.toHaveBeenCalled();

  bookingAtGate(booking).mockResolvedValueOnce([]).mockResolvedValueOnce([{ id: 'pending' }])
    .mockResolvedValueOnce([{ booking_id: booking.id }])
    .mockResolvedValueOnce([{ id: 'gate-order', source: 'gate_scan' }]);
  await expect(authorizeEntry('officer', scan)).resolves.toMatchObject({ source: 'gate_scan' });
  expect(yard.requireSpace).toHaveBeenCalledTimes(1);
});

test('held and not-ready trips cannot be directly admitted', async () => {
  bookingAtGate({ ...booking, held: true });
  bookingAtGate({ ...booking, not_ready: true });
  await expect(authorizeEntry('officer', scan)).rejects.toThrow('chưa sẵn sàng');
  await expect(authorizeEntry('officer', scan)).rejects.toThrow('chưa sẵn sàng');
  expect(yard.assignDestination).not.toHaveBeenCalled();
});

test('hardware proof requires an unused manual command or a PASS record with an active entry order', async () => {
  sequelize.query.mockResolvedValueOnce([]).mockResolvedValueOnce([{ id: 'command' }]);
  await expect(claimInboundHardwareOpen('missing')).resolves.toBe(false);
  await expect(claimInboundHardwareOpen('command')).resolves.toBe(true);
  const claimSql = sequelize.query.mock.calls[1][0];
  expect(claimSql).toContain('hardware_requested_at IS NULL');
  expect(claimSql).toContain("c.source='manual' AND c.verification_id IS NULL AND c.entry_order_id IS NULL");
  expect(claimSql).toContain("v.status='PASS'");
  expect(claimSql).toContain("e.status='active'");
});
