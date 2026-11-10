'use strict';

const { sequelize } = require('../../src/config/database');
const { QueryTypes } = require('sequelize');

async function seed() {
  const transaction = await sequelize.transaction();
  try {
    const drivers = await sequelize.query(
      'SELECT d.id, d.user_id, d.full_name, u.username FROM drivers d JOIN users u ON u.id = d.user_id',
      { type: QueryTypes.SELECT, transaction }
    );
    console.log('Found drivers in system:', drivers.map(d => ({ username: d.username, fullName: d.full_name })));

    const tienDriver = drivers.find(d => d.username === 'tiendriver') || drivers[0];
    const defaultDriver = drivers.find(d => d.username === 'driver01') || drivers[0];
    const carrierId = 'ca2f3007-30b5-4236-9013-95c66dcdf5b6';

    const [cType] = await sequelize.query("SELECT id FROM container_types WHERE code = '40HC' LIMIT 1", { type: QueryTypes.SELECT, transaction });
    const cTypeId = cType ? cType.id : '653bc1d5-524e-4694-946f-11269bc87ac0';

    let [tienTruck] = await sequelize.query("SELECT id FROM trucks WHERE plate_number = '43C-888.88'", { type: QueryTypes.SELECT, transaction });
    if (!tienTruck) {
      const [newTruck] = await sequelize.query(`
        INSERT INTO trucks (id, carrier_id, driver_id, plate_number, status, created_at)
        VALUES (gen_random_uuid(), :carrierId, :driverId, '43C-888.88', 'active', NOW()) RETURNING id
      `, { replacements: { carrierId, driverId: tienDriver.id }, type: QueryTypes.INSERT, transaction });
      tienTruck = newTruck[0];
    }
    const [defTruck] = await sequelize.query("SELECT id FROM trucks WHERE plate_number = '51D-123.45'", { type: QueryTypes.SELECT, transaction });

    const containerNumbers = ['TGHU888888-8', 'MSCU3727664', 'EVER991203-4'];
    const containerIds = {};
    const declarationIds = {};

    for (const cNo of containerNumbers) {
      let [c] = await sequelize.query('SELECT id FROM containers WHERE container_no = :cNo', { replacements: { cNo }, type: QueryTypes.SELECT, transaction });
      if (!c) {
        const [nc] = await sequelize.query(`
          INSERT INTO containers (id, container_no, container_type_id, carrier_id, status, cargo_type, gross_weight_kg, seal_no, created_at, updated_at)
          VALUES (gen_random_uuid(), :cNo, :cTypeId, :carrierId, 'in_yard', 'general', 28500, 'SEAL-VN-9921', NOW(), NOW()) RETURNING id
        `, { replacements: { cNo, cTypeId, carrierId }, type: QueryTypes.INSERT, transaction });
        c = nc[0];
      }
      containerIds[cNo] = c.id;

      let [decl] = await sequelize.query('SELECT id FROM container_declarations WHERE container_id = :cId LIMIT 1', { replacements: { cId: c.id }, type: QueryTypes.SELECT, transaction });
      if (!decl) {
        const [nd] = await sequelize.query(`
          INSERT INTO container_declarations (id, container_id, carrier_id, declared_by, cargo_type, gross_weight_kg, load_status, seal_number, customer_name, created_at, updated_at)
          VALUES (gen_random_uuid(), :cId, :carrierId, :userId, 'general', 28500, 'full', 'SEAL-VN-9921', 'Samsung Electronics VN', NOW(), NOW()) RETURNING id
        `, { replacements: { cId: c.id, carrierId, userId: tienDriver.user_id }, type: QueryTypes.INSERT, transaction });
        decl = nd[0];
      }
      declarationIds[cNo] = decl.id;
    }

    const now = new Date();
    const apptStart = new Date(now.getTime() - 30 * 60 * 1000);
    const apptEnd = new Date(now.getTime() + 4 * 60 * 60 * 1000);

    // Clean up old duplicate bookings if any
    const oldBookings = await sequelize.query("SELECT id FROM bookings WHERE booking_code IN ('BK-2026-NXP991', 'BK-2026-NXP992', 'BK-2026-NXP01')", { type: QueryTypes.SELECT, transaction });
    if (oldBookings.length > 0) {
      const bIds = oldBookings.map(b => b.id);
      await sequelize.query("DELETE FROM driver_booking_notifications WHERE booking_id IN (:bIds)", { replacements: { bIds }, transaction });
      await sequelize.query("DELETE FROM gate_entry_orders WHERE booking_id IN (:bIds)", { replacements: { bIds }, transaction });
      await sequelize.query("DELETE FROM booking_containers WHERE booking_id IN (:bIds)", { replacements: { bIds }, transaction });
      await sequelize.query("DELETE FROM bookings WHERE id IN (:bIds)", { replacements: { bIds }, transaction });
    }

    // Booking 1 for tiendriver: Approved, active Gate Pass (dropoff)
    const [b1] = await sequelize.query(`
      INSERT INTO bookings (
        id, booking_code, carrier_id, driver_id, truck_id,
        booking_type, status, appointment_start, appointment_end,
        approved_at, approved_by, vehicle_plate, driver_name, gate_type, description,
        created_at, updated_at
      ) VALUES (
        gen_random_uuid(), 'BK-2026-NXP991', :carrierId, :driverId, :truckId,
        'dropoff', 'approved', :apptStart, :apptEnd,
        NOW(), :userId, '43C-888.88', :driverName, 'GATE-A', 'Vận chuyển linh kiện điện tử Samsung Hải Phòng - Cảng Tiên Sa',
        NOW(), NOW()
      ) RETURNING id
    `, {
      replacements: {
        carrierId,
        driverId: tienDriver.id,
        truckId: tienTruck.id,
        apptStart,
        apptEnd,
        userId: tienDriver.user_id,
        driverName: tienDriver.full_name
      },
      type: QueryTypes.INSERT,
      transaction
    });
    const booking1Id = b1[0].id;

    await sequelize.query(`
      INSERT INTO booking_containers (booking_id, container_id, declaration_id)
      VALUES (:bookingId, :containerId, :declId) ON CONFLICT DO NOTHING
    `, { replacements: { bookingId: booking1Id, containerId: containerIds['TGHU888888-8'], declId: declarationIds['TGHU888888-8'] }, transaction });

    await sequelize.query(`
      INSERT INTO gate_entry_orders (
        id, booking_id, driver_id, truck_id, vehicle_plate,
        status, issued_at, valid_from, valid_until, dispatch_version
      ) VALUES (
        gen_random_uuid(), :bookingId, :driverId, :truckId, '43C-888.88',
        'active', NOW(), :validFrom, :validUntil, 1
      )
    `, {
      replacements: {
        bookingId: booking1Id,
        driverId: tienDriver.id,
        truckId: tienTruck.id,
        validFrom: apptStart,
        validUntil: apptEnd
      },
      transaction
    });

    await sequelize.query(`
      INSERT INTO driver_booking_notifications (id, user_id, booking_id, event_key, title, message, is_read, created_at)
      VALUES 
        (gen_random_uuid(), :userId, :bookingId, 'assigned', 'Booking được phân công', 'Bạn được phân công vận chuyển Booking BK-2026-NXP991 (Cont TGHU888888-8).', false, NOW()),
        (gen_random_uuid(), :userId, :bookingId, 'approved', 'Booking đã được duyệt', 'Booking BK-2026-NXP991 đã được duyệt. Xuất trình e-Pass tại Cổng A1 khi đến.', false, NOW())
      ON CONFLICT (user_id, booking_id, event_key) DO NOTHING
    `, { replacements: { userId: tienDriver.user_id, bookingId: booking1Id }, transaction });

    // Booking 2 for tiendriver: Upcoming tomorrow
    const tomorrowStart = new Date(now.getTime() + 24 * 60 * 60 * 1000);
    const tomorrowEnd = new Date(now.getTime() + 28 * 60 * 60 * 1000);
    const [b2] = await sequelize.query(`
      INSERT INTO bookings (
        id, booking_code, carrier_id, driver_id, truck_id,
        booking_type, status, appointment_start, appointment_end,
        approved_at, approved_by, vehicle_plate, driver_name, gate_type, description,
        created_at, updated_at
      ) VALUES (
        gen_random_uuid(), 'BK-2026-NXP992', :carrierId, :driverId, :truckId,
        'dropoff', 'approved', :apptStart, :apptEnd,
        NOW(), :userId, '43C-888.88', :driverName, 'GATE-B', 'Hạ bãi vỏ rỗng container 40HC',
        NOW(), NOW()
      ) RETURNING id
    `, {
      replacements: {
        carrierId,
        driverId: tienDriver.id,
        truckId: tienTruck.id,
        apptStart: tomorrowStart,
        apptEnd: tomorrowEnd,
        userId: tienDriver.user_id,
        driverName: tienDriver.full_name
      },
      type: QueryTypes.INSERT,
      transaction
    });
    const booking2Id = b2[0].id;

    await sequelize.query(`
      INSERT INTO booking_containers (booking_id, container_id, declaration_id)
      VALUES (:bookingId, :containerId, :declId) ON CONFLICT DO NOTHING
    `, { replacements: { bookingId: booking2Id, containerId: containerIds['EVER991203-4'], declId: declarationIds['EVER991203-4'] }, transaction });

    // Booking 3 for defaultDriver (driver01) (dropoff)
    const [b3] = await sequelize.query(`
      INSERT INTO bookings (
        id, booking_code, carrier_id, driver_id, truck_id,
        booking_type, status, appointment_start, appointment_end,
        approved_at, approved_by, vehicle_plate, driver_name, gate_type, description,
        created_at, updated_at
      ) VALUES (
        gen_random_uuid(), 'BK-2026-NXP01', :carrierId, :driverId, :truckId,
        'dropoff', 'approved', :apptStart, :apptEnd,
        NOW(), :userId, '51D-123.45', :driverName, 'GATE-A', 'Hạ bãi container xuất khẩu MSCU3727664 tại Bãi Tiên Sa',
        NOW(), NOW()
      ) RETURNING id
    `, {
      replacements: {
        carrierId,
        driverId: defaultDriver.id,
        truckId: defTruck ? defTruck.id : tienTruck.id,
        apptStart,
        apptEnd,
        userId: defaultDriver.user_id,
        driverName: defaultDriver.full_name
      },
      type: QueryTypes.INSERT,
      transaction
    });
    const booking3Id = b3[0].id;

    await sequelize.query(`
      INSERT INTO booking_containers (booking_id, container_id, declaration_id)
      VALUES (:bookingId, :containerId, :declId) ON CONFLICT DO NOTHING
    `, { replacements: { bookingId: booking3Id, containerId: containerIds['MSCU3727664'], declId: declarationIds['MSCU3727664'] }, transaction });

    await sequelize.query(`
      INSERT INTO gate_entry_orders (
        id, booking_id, driver_id, truck_id, vehicle_plate,
        status, issued_at, valid_from, valid_until, dispatch_version
      ) VALUES (
        gen_random_uuid(), :bookingId, :driverId, :truckId, '51D-123.45',
        'active', NOW(), :validFrom, :validUntil, 1
      )
    `, {
      replacements: {
        bookingId: booking3Id,
        driverId: defaultDriver.id,
        truckId: defTruck ? defTruck.id : tienTruck.id,
        validFrom: apptStart,
        validUntil: apptEnd
      },
      transaction
    });

    await sequelize.query(`
      INSERT INTO driver_booking_notifications (id, user_id, booking_id, event_key, title, message, is_read, created_at)
      VALUES 
        (gen_random_uuid(), :userId, :bookingId, 'assigned', 'Booking được phân công', 'Bạn được phân công Booking BK-2026-NXP01.', false, NOW()),
        (gen_random_uuid(), :userId, :bookingId, 'approved', 'Booking đã được duyệt', 'Booking BK-2026-NXP01 đã được duyệt. Xuất trình e-Pass tại Cổng A.', false, NOW())
      ON CONFLICT (user_id, booking_id, event_key) DO NOTHING
    `, { replacements: { userId: defaultDriver.user_id, bookingId: booking3Id }, transaction });

    await transaction.commit();
    console.log('SUCCESS: Seeded real bookings, declarations, containers, gate passes, and notifications!');
  } catch (err) {
    await transaction.rollback();
    console.error('FAILED seeding:', err);
  }
  process.exit(0);
}

seed();
