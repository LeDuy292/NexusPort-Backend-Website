'use strict';
const { QueryTypes } = require('sequelize');
const { sequelize } = require('../../config/database');
const { getIO, emitToDispatchers, emitToDriver } = require('../../config/socket');
const { runAutoAdmission } = require('./yard-admission');
let running = null;
let lastError = 0;
async function deliver() {
  if (!getIO()) return;
  // Avoid opening an empty transaction on every background tick.
  const pending = await sequelize.query(`SELECT id FROM booking_dispatch_outbox
    WHERE published_at IS NULL LIMIT 1`, { type: QueryTypes.SELECT, logging: false });
  if (!pending.length) return;
  // At-least-once delivery: clients invalidate snapshots; reconnect always reloads.
  await sequelize.transaction({ logging: false }, async transaction => {
    const events = await sequelize.query(`SELECT e.*,d.user_id FROM booking_dispatch_outbox e
      LEFT JOIN drivers d ON d.id=e.driver_id
      WHERE e.published_at IS NULL ORDER BY e.created_at,e.version LIMIT 50
      FOR UPDATE OF e SKIP LOCKED`, { type: QueryTypes.SELECT, transaction, logging: false });
    for (const event of events) {
      const payload = { eventId: event.id, bookingId: event.booking_id, version: event.version,
        kind: event.kind, occurredAt: event.created_at };
      emitToDispatchers('booking:dispatch_updated', payload);
      // Driver socket room IDs are login IDs, not fleet profile IDs.
      if (event.user_id) emitToDriver(event.user_id, 'booking:dispatch_updated', payload);
      await sequelize.query('UPDATE booking_dispatch_outbox SET published_at=now() WHERE id=:id',
        { replacements: { id: event.id }, transaction, logging: false });
    }
  });
}
async function expireCalls() {
  const [rows] = await sequelize.query(`SELECT booking_id FROM booking_dispatch_state
    WHERE (status='called' AND call_expires_at<=now()) OR
      (status='acknowledged' AND EXISTS(SELECT 1 FROM gate_entry_orders e WHERE e.booking_id=booking_dispatch_state.booking_id
        AND e.status='active' AND e.valid_until<=now())) LIMIT 30`, { logging: false });
  for (const row of rows) {
    await sequelize.transaction({ logging: false }, async transaction => {
      // Same lock order as API writes.
      await sequelize.query(`SELECT b.id FROM bookings b JOIN drivers d ON d.id=b.driver_id
        WHERE b.id=:id FOR UPDATE OF b,d`, { replacements: { id: row.booking_id }, transaction, logging: false });
      await sequelize.query(`UPDATE booking_dispatch_state SET action=CASE WHEN status='acknowledged' THEN 'entry_expired' ELSE 'call_expired' END,
        message=CASE WHEN status='acknowledged' THEN 'Lệnh vào cổng đã hết hạn sau 15 phút. Tiếp tục chờ điều độ gọi lại.'
          ELSE 'Bạn chưa phản hồi trong 10 phút. Tiếp tục chờ điều độ gọi lại.' END,status='ready',
        called_at=NULL,call_expires_at=NULL,acknowledged_at=NULL,ready_at=now(),version=version+1,
        updated_at=now(),updated_by=NULL
        WHERE booking_id=:id AND ((status='called' AND call_expires_at<=now()) OR
          (status='acknowledged' AND EXISTS(SELECT 1 FROM gate_entry_orders e WHERE e.booking_id=booking_dispatch_state.booking_id
            AND e.status='active' AND e.valid_until<=now())))`,
      { replacements: { id: row.booking_id }, transaction, logging: false });
    });
  }
}
function pumpDispatchEvents() {
  if (!running) running = (async () => { await expireCalls(); await runAutoAdmission(); await deliver(); })()
    .catch(error => {
      if (Date.now() - lastError > 30000) { lastError = Date.now(); console.error('[Booking dispatch realtime]', error.message); }
    }).finally(() => { running = null; });
  return running;
}
function startDispatchRealtime() {
  // Commands pump immediately after commit; this timer handles recovery and expiry.
  const timer = setInterval(() => { void pumpDispatchEvents(); }, 5000);
  timer.unref();
  void pumpDispatchEvents();
  return () => clearInterval(timer);
}
module.exports = { pumpDispatchEvents, startDispatchRealtime };
