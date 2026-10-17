'use strict';

/**
 * Seed script - Tạo 7 tài khoản test cho mỗi role trong NexusPort.
 *
 * Chạy: npm run seed
 * Password mặc định: NexusPort@2026
 */

require('dotenv').config();
require('../config/env');

const bcrypt = require('bcryptjs');
const { connectDB, syncDB } = require('../config/database');
const { User } = require('../models/User');
require('../models/TransportTrip');
const { bcrypt: bcryptConfig } = require('../config/env');

const DEFAULT_PASSWORD = 'NexusPort@2026';

const SEED_USERS = [
  {
    username: 'admin',
    email: 'admin@nexusport.vn',
    role: 'Administrator',
    full_name: 'Nguyễn Quản Trị',
  },
  {
    username: 'dispatcher01',
    email: 'dispatcher01@nexusport.vn',
    role: 'Dispatcher',
    full_name: 'Trần Điều Phối',
  },
  {
    username: 'gate01',
    email: 'gate01@nexusport.vn',
    role: 'Gate Officer',
    full_name: 'Lê Kiểm Cổng',
  },
  {
    username: 'yard01',
    email: 'yard01@nexusport.vn',
    role: 'Yard Operator',
    full_name: 'Phạm Bãi Hàng',
  },
  {
    username: 'berth01',
    email: 'berth01@nexusport.vn',
    role: 'Berth Staff',
    full_name: 'Hoàng Cầu Tàu',
  },
  {
    username: 'carrier01',
    email: 'carrier01@nexusport.vn',
    role: 'Transport Company',
    full_name: 'Võ Hãng Tàu',
  },
  {
    username: 'driver01',
    email: 'driver01@nexusport.vn',
    role: 'Driver',
    full_name: 'Đặng Tài Xế',
  },
];

async function seed() {
  console.log('\n🌱 NexusPort Seed Script\n');

  try {
    await connectDB();
    await syncDB({ alter: true });

    // Hash password một lần để dùng cho tất cả seed users
    console.log('🔐 Hashing password...');
    const hashedPassword = await bcrypt.hash(DEFAULT_PASSWORD, bcryptConfig.saltRounds);

    let created = 0;
    let skipped = 0;

    for (const userData of SEED_USERS) {
      const [user, wasCreated] = await User.findOrCreate({
        where: { username: userData.username },
        defaults: {
          ...userData,
          password: hashedPassword,
          is_active: true,
        },
      });

      if (wasCreated) {
        console.log(`  ✅ Tạo: [${user.role.padEnd(18)}] ${user.username} (${user.email})`);
        created++;
      } else {
        console.log(`  ⏭️  Bỏ qua (đã tồn tại): ${user.username}`);
        skipped++;
      }

      // Tự động liên kết tài khoản Transport Company vào carrier_users (nếu bảng tồn tại)
      if (user.role === 'Transport Company') {
        const { sequelize } = require('../config/database');
        try {
          await sequelize.query(`
            INSERT INTO carrier_users (carrier_id, user_id)
            SELECT 'c1010101-0000-0000-0000-000000000001', :userId
            ON CONFLICT DO NOTHING;
          `, { replacements: { userId: user.id } });
        } catch (err) {
          // Bỏ qua nếu bảng carrier_users chưa tồn tại trong schema mới
        }
      }

      // Seed 2 chuyến mẫu cho driver01
      if (user.role === 'Driver') {
        const { TransportTrip } = require('../models/TransportTrip');
        const existingTrips = await TransportTrip.count({ where: { driver_id: user.id } });
        if (existingTrips === 0) {
          await TransportTrip.bulkCreate([
            {
              trip_code: 'TRIP-2026-0001',
              driver_id: user.id,
              booking_code: 'BK-20260902-8891',
              booking_type: 'Pickup',
              truck_plate: '51C-987.65',
              container_no: 'TCLU9876543',
              container_type: '40ft Dry High Cube',
              cargo_type: 'Hàng tiêu dùng',
              gate_in_code: 'GATE-A1',
              gate_out_code: 'GATE-B2',
              yard_block: 'Block A',
              yard_slot: 'A05-02',
              yard_instructions: 'Vào Cổng GATE-A1 ➔ Rẽ phải Làn 2 ➔ Đi thẳng 150m tới Block A ➔ Hạ container tại ô A05-02',
              seal_no: 'SEAL-VN-998811',
              status: 'Assigned',
              appointment_start: new Date(Date.now() - 3600000),
              appointment_end: new Date(Date.now() + 7200000),
              note: 'Hàng dễ vỡ, cần giao đúng giờ tại Block A05-02',
            },
            {
              trip_code: 'TRIP-2026-0002',
              driver_id: user.id,
              booking_code: 'BK-20260902-7723',
              booking_type: 'Dropoff',
              truck_plate: '51D-123.45',
              container_no: 'MSKU1234567',
              container_type: '20ft Reefer',
              cargo_type: 'Hải sản đông lạnh',
              gate_in_code: 'GATE-A2',
              gate_out_code: 'GATE-B1',
              yard_block: 'Block C',
              yard_slot: 'C02-01',
              yard_instructions: 'Vào Cổng GATE-A2 ➔ Rẽ trái Làn Reefer Lạnh ➔ Đến Khu C Bãi Đông ➔ Hạ tại ô C02-01 gần trạm cắm điện',
              seal_no: 'SEAL-RF-554411',
              status: 'Container Picked Up',
              appointment_start: new Date(Date.now() - 7200000),
              appointment_end: new Date(Date.now() + 3600000),
              note: 'Container lạnh - duy trì nhiệt độ -18C',
            },
          ]);
          console.log(`  🚛 Đã seed 2 chuyến vận chuyển mẫu cho Driver: ${user.username}`);
        }
      }
    }

    console.log(`\n📊 Kết quả: ${created} tạo mới, ${skipped} bỏ qua`);
    console.log(`🔑 Password mặc định: ${DEFAULT_PASSWORD}`);
    console.log('\n✨ Seed hoàn tất!\n');

    process.exit(0);
  } catch (error) {
    console.error('\n❌ Seed thất bại:', error.message);
    process.exit(1);
  }
}

seed();
