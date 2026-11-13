import { createServer, Server } from 'http';
import { randomUUID } from 'crypto';
import jwt from 'jsonwebtoken';
import { createApp } from '../../api/app';
import { query } from '../../infrastructure/database/db';
import { ManifestRepository } from './manifest.repository';
import { isValidIso6346 } from '../container/application/container.validator';

jest.mock('../../infrastructure/database/db', () => ({ query: jest.fn() }));
jest.mock('../../shared/utils/logger', () => ({ logger: { error: jest.fn(), warn: jest.fn(), info: jest.fn() } }));

describe('Large imported Manifest through the production Express app', () => {
  let server: Server, base: string, token: string;
  const oldSecret = process.env.JWT_SECRET;
  const save = jest.spyOn(ManifestRepository.prototype, 'save');
  beforeAll(async () => {
    const owner = randomUUID();
    process.env.JWT_SECRET = 'large-manifest-test-only';
    token = jwt.sign({sub:owner}, process.env.JWT_SECRET);
    (query as jest.Mock).mockResolvedValue({rows:[{id:owner}]});
    save.mockResolvedValue({id:randomUUID(),status:'DRAFT'} as Awaited<ReturnType<ManifestRepository['save']>>);
    server = createServer(createApp());
    await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve));
    base = `http://127.0.0.1:${(server.address() as {port:number}).port}`;
  });
  afterAll(async () => {
    await new Promise<void>((resolve, reject) => server.close(e => e ? reject(e) : resolve()));
    if (oldSecret === undefined) delete process.env.JWT_SECRET; else process.env.JWT_SECRET = oldSecret;
  });
  it.each(['/api/v1', '/api'])('saves a reviewed bulk payload above the former 100 KB limit at %s', async prefix => {
    const containers = Array.from({length:100}, (_, i) => ({
      containerNumber: Array.from({length:10}, (_, check) => `CSQU${100000+i}${check}`).find(isValidIso6346),
      sealNumber:`SEAL-${i}`, grossWeightKg:18000, commodity:'Hàng hóa đóng thùng carton. '.repeat(60),
    }));
    const body = JSON.stringify({requestKey:randomUUID(),voyageNumber:'TEST',vesselName:'TEST VESSEL',direction:'EXPORT',isDraft:true,bills:[{blNumber:'BULK-TEST',shipper:'Shipper',consignee:'Consignee',containers}]});
    expect(Buffer.byteLength(body)).toBeGreaterThan(100*1024);
    const response = await fetch(`${base}${prefix}/shipping-line/manifests`, {method:'POST',headers:{Authorization:`Bearer ${token}`,'Content-Type':'application/json'},body});
    expect(response.status).toBe(201);
    expect(save.mock.calls[0][1].bills[0].containers).toHaveLength(100);
    expect(save.mock.calls[0][1].bills[0].containers[0].commodity).toBe(containers[0].commodity.trim());
  });
  it('preserves the smaller body limit for other modules and returns a readable 413', async () => {
    const response = await fetch(`${base}/api/v1/auth/login`, {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({padding:'x'.repeat(110*1024)})});
    expect(response.status).toBe(413);
    expect(await response.json()).toMatchObject({error:{code:'PAYLOAD_TOO_LARGE'}});
  });
});
