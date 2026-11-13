import 'dotenv/config';
import jwt from 'jsonwebtoken';
import pool from '../infrastructure/database/db';
import { connectWithCapacityRetry } from '../infrastructure/database/connection-capacity';

async function main() {
  let closed = false;
  try {
    const client = await connectWithCapacityRetry(() => pool.connect(), { timeoutMs: 30000 });
    let owner: string;
    try {
      const result = await client.query("SELECT id FROM users WHERE role='Shipping Line' AND is_active=true LIMIT 1");
      owner = result.rows[0]?.id;
      if (!owner || !process.env.JWT_SECRET) throw new Error('An active shipping account is required.');
    } finally { client.release(); }
    const token = jwt.sign({ sub: owner }, process.env.JWT_SECRET!, { expiresIn: '2m', algorithm: 'HS256' });
    await pool.end(); closed = true;
    for (let attempt = 1; attempt <= 3; attempt++) {
      if (attempt > 1) await new Promise(resolve => setTimeout(resolve, 6000));
      const response = await fetch('http://localhost:5173/node-api/shipping-line/manifests/container-lookup?containerNumber=CMAU3381923', {
        headers: { Authorization: `Bearer ${token}` }, signal: AbortSignal.timeout(10000),
      });
      const body = await response.json() as { data?: { containerNumber: string; isoType: string; isReefer: boolean } };
      console.log('Lookup after idle',attempt,'HTTP',response.status,'type',body.data?.isoType,'reefer',body.data?.isReefer);
      if (!response.ok || body.data?.containerNumber !== 'CMAU3381923' || !body.data.isReefer) throw new Error('Reefer lookup failed after idle.');
    }
  } finally { if (!closed) await pool.end(); }
}
main().catch(e=>{console.error(e.message);process.exitCode=1});
