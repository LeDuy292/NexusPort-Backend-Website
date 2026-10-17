'use strict';
const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const { getIO, emitToDispatchers, emitToDriver } = require('../../config/socket');
let running = null;
let lastError = 0;
async function deliver() {
  if (!getIO()) return;
  // At-least-once delivery: clients invalidate snapshots; reconnect always reloads.
  await sequelize.transaction(async transaction => {
    const events = await sequelize.query(`SELECT e.*,d.user_id FROM booking_dispatch_outbox e
      LEFT JOIN drivers d ON d.id=e.driver_id
      WHERE e.published_at IS NULL ORDER BY e.created_at,e.version LIMIT 50
      FOR UPDATE OF e SKIP LOCKED`, { type: QueryTypes.SELECT, transaction });
    for (const event of events) {
      const payload = { eventId: event.id, bookingId: event.booking_id, version: event.version,
        kind: event.kind, occurredAt: event.created_at };
      emitToDispatchers('booking:dispatch_updated', payload);
      // Driver socket room IDs are login IDs, not fleet profile IDs.
      if (event.user_id) emitToDriver(event.user_id, 'booking:dispatch_updated', payload);
      await sequelize.query('UPDATE booking_dispatch_outbox SET published_at=now() WHERE id=:id',
        { replacements: { id: event.id }, transaction });
    }
  });
}
async function expireCalls() {
  const [rows] = await sequelize.query(`SELECT booking_id FROM booking_dispatch_state
    WHERE status='called' AND call_expires_at<=now() LIMIT 30`);
  for (const row of rows) {
    await sequelize.transaction(async transaction => {
      // Same lock order as API writes.
      await sequelize.query(`SELECT b.id FROM bookings b JOIN drivers d ON d.id=b.driver_id
        WHERE b.id=:id FOR UPDATE OF b,d`, { replacements: { id: row.booking_id }, transaction });
      await sequelize.query(`UPDATE booking_dispatch_state SET status='ready',action='call_expired',
        called_at=NULL,call_expires_at=NULL,acknowledged_at=NULL,ready_at=now(),version=version+1,
        message='Bạn chưa phản hồi trong 10 phút. Tiếp tục chờ điều độ gọi lại.',updated_at=now(),updated_by=NULL
        WHERE booking_id=:id AND status='called' AND call_expires_at<=now()`,
      { replacements: { id: row.booking_id }, transaction });
    });
  }
}
function pumpDispatchEvents() {
  if (!running) running = (async () => { await expireCalls(); await deliver(); })()
    .catch(error => {
      if (Date.now() - lastError > 30000) { lastError = Date.now(); console.error('[Booking dispatch realtime]', error.message); }
    }).finally(() => { running = null; });
  return running;
}
function startDispatchRealtime() {
  const timer = setInterval(() => { void pumpDispatchEvents(); }, 1000);
  timer.unref();
  void pumpDispatchEvents();
  return () => clearInterval(timer);
}
module.exports = { pumpDispatchEvents, startDispatchRealtime };
