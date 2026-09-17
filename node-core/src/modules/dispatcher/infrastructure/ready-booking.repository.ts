import { query } from '../../../infrastructure/database/db';
import { ReadyBookingDto } from '../application/ready-booking.dto';

interface ReadyBookingRow {
  id: string;
  bookingCode: string;
  bookingType: string;
  status: string;
  appointmentStart: Date;
  appointmentEnd: Date;
  driverId: string | null;
  driverName: string | null;
  driverStatus: string | null;
  vehicleId: string | null;
  vehiclePlate: string | null;
  vehicleStatus: string | null;
  containerId: string;
  containerNumber: string;
  containerStatus: string;
}

export class ReadyBookingRepository {
  async findAll(): Promise<ReadyBookingDto[]> {
    // Match the PostgreSQL tables already used by Booking, Driver, Vehicle and Container.
    const result = await query(`
      SELECT b.id, b.booking_code AS "bookingCode", b.booking_type::text AS "bookingType",
             b.status::text AS status, b.appointment_start AS "appointmentStart",
             b.appointment_end AS "appointmentEnd", d.id AS "driverId",
             d.full_name AS "driverName", d.status::text AS "driverStatus",
             t.id AS "vehicleId", t.plate_number AS "vehiclePlate",
             t.status::text AS "vehicleStatus", c.id AS "containerId",
             c.container_no AS "containerNumber", c.status::text AS "containerStatus"
        FROM bookings b
        JOIN booking_containers bc ON bc.booking_id = b.id
        JOIN containers c ON c.id = bc.container_id
        LEFT JOIN drivers d ON d.id = b.driver_id
        LEFT JOIN trucks t ON t.id = b.truck_id
       WHERE lower(b.status::text) = 'approved'
         AND COALESCE(b.is_deleted, false) = false
       ORDER BY b.appointment_start, b.id, c.container_no
    `);

    const bookings = new Map<string, ReadyBookingDto>();
    for (const row of result.rows as ReadyBookingRow[]) {
      let booking = bookings.get(row.id);
      if (!booking) {
        booking = {
          id: row.id,
          bookingCode: row.bookingCode,
          bookingType: row.bookingType,
          status: row.status,
          appointmentStart: row.appointmentStart,
          appointmentEnd: row.appointmentEnd,
          driver: row.driverId ? {
            id: row.driverId, name: row.driverName ?? '', status: row.driverStatus ?? '',
          } : null,
          vehicle: row.vehicleId ? {
            id: row.vehicleId, name: row.vehiclePlate ?? '', status: row.vehicleStatus ?? '',
          } : null,
          containers: [],
        };
        bookings.set(row.id, booking);
      }
      booking.containers.push({
        id: row.containerId,
        containerNumber: row.containerNumber,
        status: row.containerStatus,
      });
    }
    return [...bookings.values()];
  }
}
