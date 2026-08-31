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
