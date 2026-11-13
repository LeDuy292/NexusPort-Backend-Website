import 'dotenv/config';
import jwt from 'jsonwebtoken';
import amqp from 'amqplib';
import pool from '../infrastructure/database/db';
import { connectWithCapacityRetry } from '../infrastructure/database/connection-capacity';
async function main() {
  let poolClosed = false;
  try {
    const client = await connectWithCapacityRetry(() => pool.connect(), { timeoutMs: 30000 });
    let rows, registered;
    try {
      rows = (await client.query("SELECT id FROM users WHERE role='Shipping Line' AND is_active=true LIMIT 1")).rows;
      registered = (await client.query(`SELECT c.id,ct.code,c.iso_size_type,c.is_reefer,c.container_no FROM containers c JOIN container_types ct ON ct.id=c.container_type_id
        WHERE c.container_no ~ '^[A-Z]{3}[UJZ][0-9]{7}$' LIMIT 1`)).rows[0];
    } finally { client.release(); }
    if (!rows[0] || !process.env.JWT_SECRET) throw new Error('An active Shipping Line account and JWT_SECRET are required.');
    const token = jwt.sign({ sub: rows[0].id }, process.env.JWT_SECRET, { expiresIn: '2m', algorithm: 'HS256' });
    const headers = { Authorization: `Bearer ${token}` };
    await pool.end();
    poolClosed = true;
    if (registered) {
      const response = await fetch(`http://localhost:5173/node-api/shipping-line/manifests/container-lookup?containerNumber=${registered.container_no}`, { headers });
      const body = await response.json() as { data?: { containerId: string; isoType: string; isoSizeType: string | null; isReefer: boolean } };
      if (!response.ok || body.data?.containerId !== registered.id || body.data?.isoType !== registered.code || body.data?.isoSizeType !== registered.iso_size_type || body.data?.isReefer !== registered.is_reefer) {
        console.log('Lookup status:', response.status, 'response:', body);
        throw new Error('Registered container lookup does not match equipment in DB.');
      }
      console.log('Registered-container lookup through FE proxy matches DB ISO and reefer fields.');
    }
    for (const url of ['http://127.0.0.1:4000/api/v1/shipping-line/manifests', 'http://localhost:5173/node-api/shipping-line/manifests', 'http://127.0.0.1:3001/api/shipping-line/manifests', 'http://127.0.0.1:3001/api/shipping-line/bills-of-lading', 'http://127.0.0.1:3001/api/shipping-line/voyages']) {
      const response = await fetch(url, { headers, signal: AbortSignal.timeout(10000) });
      const body = await response.json() as { data?: unknown[]; success?: boolean };
      console.log(url, response.status, 'records:', body.data?.length);
      if (!response.ok || body.success === false) throw new Error(`Runtime API check failed (${response.status}).`);
    }
    const connection = await amqp.connect(process.env.RABBITMQ_URL || 'amqp://guest:guest@localhost:5672');
    try {
      const channel = await connection.createChannel();
      for (const queue of ['yard.operation.completed','dispatcher.status.updated']) {
        const status = await channel.checkQueue(queue);
        console.log('RabbitMQ queue:',queue,'consumers:',status.consumerCount);
        if (!status.consumerCount) throw new Error('Realtime consumer missing: '+queue);
      }
      await channel.close();
    } finally { await connection.close(); }
    console.log('Runtime Manifest and realtime checks passed.');
  } finally { if (!poolClosed) await pool.end(); }
}
main().catch(e=>{console.error(e.message);process.exitCode=1});
