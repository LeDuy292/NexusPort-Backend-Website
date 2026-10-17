import { getClient } from '../../../infrastructure/database/db';
import { ConflictError, NotFoundError, ValidationError } from '../../../shared/errors/app-error';
import { z } from 'zod';

export class GateMovementService {
  async completeOut(actorId: string, input: unknown): Promise<{id: string}> {
    const parsed=z.object({bookingId:z.string().uuid(),gateId:z.string().uuid()}).strict().safeParse(input);
    if(!parsed.success) throw new ValidationError('Cần chọn đúng booking và cổng ra.');
    const {bookingId,gateId}=parsed.data;
    const client=await getClient();
    try {
      await client.query('BEGIN');
      const booking=(await client.query(`SELECT id,status::text,booking_type::text FROM bookings WHERE id=$1 FOR UPDATE`,[bookingId])).rows[0];
      if(!booking) throw new NotFoundError('Booking',bookingId);
      const previous=(await client.query(`SELECT id FROM gate_transactions WHERE booking_id=$1 AND gate_type='gate_out' AND status='completed' LIMIT 1`,[bookingId])).rows[0];
      if(previous){await client.query('COMMIT');return previous;}
      const gate=(await client.query(`SELECT id FROM gates WHERE id=$1 AND code='GATE-03' AND is_active`,[gateId])).rows[0];
      if(!gate) throw new ValidationError('Cổng ra không hợp lệ hoặc đã ngừng hoạt động.');
      if(booking.status!=='completed') throw new ConflictError('Xe chưa hoàn tất giao nhận tại bãi.');
      const entered=(await client.query(`SELECT id FROM gate_transactions WHERE booking_id=$1 AND gate_type='gate_in' AND status='completed' LIMIT 1`,[bookingId])).rows[0];
      if(!entered) throw new ConflictError('Chưa có xác nhận xe đã vào cổng.');
      const links=(await client.query(`SELECT count(*)::int AS total,
        count(*) FILTER(WHERE a.id IS NOT NULL)::int AS confirmed,
        count(*) FILTER(WHERE v.visit_status='active')::int AS active
        FROM booking_containers bc LEFT JOIN container_confirmation_audits a ON a.booking_id=bc.booking_id AND a.container_id=bc.container_id
        LEFT JOIN container_visits v ON v.id=bc.container_visit_id WHERE bc.booking_id=$1`,[bookingId])).rows[0];
      if(!links.total || links.confirmed!==links.total) throw new ConflictError('Cần xác nhận đủ container trước khi ra cổng.');
      if(booking.booking_type==='pickup' && links.active!==links.total) throw new ConflictError('Pickup chưa có đủ lượt bãi đang hoạt động.');
      const record=(await client.query(`INSERT INTO gate_transactions(id,booking_id,gate_id,driver_id,truck_id,checked_by,gate_type,status,plate_number_detected,checked_at,completed_at)
        SELECT gen_random_uuid(),b.id,$2,b.driver_id,b.truck_id,(SELECT id FROM users WHERE id=$3),'gate_out','completed',b.vehicle_plate,now(),now()
        FROM bookings b WHERE b.id=$1 RETURNING id`,[bookingId,gateId,actorId])).rows[0];
      if(booking.booking_type==='pickup') {
        const released=await client.query(`UPDATE container_positions p SET is_current=false,removed_at=now()
          FROM booking_containers bc WHERE bc.booking_id=$1 AND p.container_id=bc.container_id AND p.is_current=true RETURNING p.slot_id`,[bookingId]);
        const reservations=await client.query(`UPDATE yard_slot_reservations r SET reserved_to=now() FROM booking_containers bc
          WHERE bc.booking_id=$1 AND r.container_id=bc.container_id AND r.reserved_to IS NULL RETURNING r.slot_id`,[bookingId]);
        const slotIds=[...new Set([...released.rows,...reservations.rows].map(x=>x.slot_id))];
        if(slotIds.length) await client.query(`UPDATE yard_slots ys SET status='empty' WHERE ys.id=ANY($1::uuid[]) AND ys.status IN ('occupied','reserved')
          AND NOT EXISTS(SELECT 1 FROM container_positions cp WHERE cp.slot_id=ys.id AND cp.is_current=true)
          AND NOT EXISTS(SELECT 1 FROM yard_slot_reservations r WHERE r.slot_id=ys.id AND r.reserved_to IS NULL)`,[slotIds]);
      }
      await client.query('COMMIT');
      return record;
    } catch(error){await client.query('ROLLBACK');throw error;}finally{client.release();}
  }
}
