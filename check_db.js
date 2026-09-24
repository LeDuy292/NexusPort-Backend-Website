const { Client } = require('./node_modules/pg');

const client = new Client({
  host: 'localhost',
  port: 5432,
  database: 'nexusport',
  user: 'postgres',
  password: 'Tung@123456789'
});

async function check() {
  await client.connect();
  console.log('--- BOOKINGS IN DATABASE ---');
  const bookingsRes = await client.query('SELECT id, carrier_id, booking_code, booking_type, status, created_at FROM bookings ORDER BY created_at DESC LIMIT 10;');
  console.log(JSON.stringify(bookingsRes.rows, null, 2));

  console.log('--- BOOKING CONTAINERS ---');
  const bcRes = await client.query('SELECT * FROM booking_containers LIMIT 10;');
  console.log(JSON.stringify(bcRes.rows, null, 2));

  console.log('--- CONTAINERS ---');
  const contRes = await client.query('SELECT id, container_no, status, gross_weight_kg FROM containers LIMIT 10;');
  console.log(JSON.stringify(contRes.rows, null, 2));

  console.log('--- CARRIER USERS ---');
  const cuRes = await client.query('SELECT * FROM carrier_users;');
  console.log(JSON.stringify(cuRes.rows, null, 2));

  console.log('--- USERS ---');
  const uRes = await client.query("SELECT id, username, email, role FROM users WHERE username = 'carrier01';");
  console.log(JSON.stringify(uRes.rows, null, 2));

  await client.end();
}

check().catch(e => { console.error(e); process.exit(1); });
