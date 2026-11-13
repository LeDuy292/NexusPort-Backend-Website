import express from 'express';
import { createServer, Server } from 'http';
import { randomUUID } from 'crypto';
import jwt from 'jsonwebtoken';
import * as XLSX from 'xlsx';
import { query } from '../../infrastructure/database/db';
import { errorHandler } from '../../api/middleware/error.middleware';
import { createManifestRouter } from './manifest.controller';
import { ManifestRepository } from './manifest.repository';
import { MANIFEST_IMPORT_HEADERS } from './manifest-import.service';

jest.mock('../../infrastructure/database/db', () => ({ query: jest.fn() }));
jest.mock('../../shared/utils/logger', () => ({ logger: { error: jest.fn(), warn: jest.fn() } }));

describe('Manifest Excel HTTP upload contract', () => {
  const owner = randomUUID(), repo = new ManifestRepository();
  const lookup = jest.spyOn(repo, 'lookupContainers');
  const used = jest.spyOn(repo, 'usedBillNumbers');
  const save = jest.spyOn(repo, 'save');
  const oldSecret = process.env.JWT_SECRET;
  let server: Server, base: string, token: string;
  const buildFile = () => {
    const wb = XLSX.utils.book_new();
    XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet([[...MANIFEST_IMPORT_HEADERS], ['EG-2605W','EVER GLORY','EXPORT','BL-HTTP','Shipper','Consignee','CMAU3381923','SEAL-1',18000,'Cá đông lạnh']]), 'Manifest');
    return XLSX.write(wb, { type:'buffer', bookType:'xlsx' }) as Buffer;
  };
  const upload = async (buffer: Buffer, name = 'Manifest.xlsx', authenticated = true, field = 'file') => {
    const body = new FormData(); body.append(field, new Blob([new Uint8Array(buffer)]), name);
    return fetch(`${base}/import/preview`, { method:'POST', body, headers: authenticated ? { Authorization:`Bearer ${token}` } : {} });
  };
  beforeAll(async () => {
    process.env.JWT_SECRET = 'manifest-import-test-only';
    token = jwt.sign({sub:owner}, process.env.JWT_SECRET);
    const app = express(); app.use(express.json()); app.use('/manifests', createManifestRouter(repo)); app.use(errorHandler);
    server = createServer(app);
    await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve));
    base = `http://127.0.0.1:${(server.address() as {port:number}).port}/manifests`;
  });
  beforeEach(() => {
    (query as jest.Mock).mockResolvedValue({rows:[{id:owner}]});
    lookup.mockResolvedValue([{containerNumber:'CMAU3381923',containerId:randomUUID(),isoType:'20RF',isoSizeType:'22R1',isReefer:true}]);
    used.mockResolvedValue([]);
  });
  afterAll(async () => {
    await new Promise<void>((resolve, reject) => server.close(e => e ? reject(e) : resolve()));
    if (oldSecret === undefined) delete process.env.JWT_SECRET; else process.env.JWT_SECRET = oldSecret;
  });
  it('protects both import routes and enforces active Shipping Line membership', async () => {
    expect((await upload(buildFile(), 'Manifest.xlsx', false)).status).toBe(401);
    expect((await fetch(`${base}/import/template`)).status).toBe(401);
    (query as jest.Mock).mockResolvedValue({rows:[]});
    expect((await upload(buildFile())).status).toBe(403);
    expect(lookup).not.toHaveBeenCalled();
  });
  it('downloads a valid blank template and accepts a real multipart upload without saving anything', async () => {
    const template = await fetch(`${base}/import/template`, {headers:{Authorization:`Bearer ${token}`}});
    expect(template.status).toBe(200);
    expect(template.headers.get('content-type')).toContain('spreadsheetml');
    const wb = XLSX.read(Buffer.from(await template.arrayBuffer()), {type:'buffer'});
    expect(XLSX.utils.sheet_to_json(wb.Sheets.Manifest, {header:1})).toEqual([[...MANIFEST_IMPORT_HEADERS]]);
    const response = await upload(buildFile());
    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({success:true,data:{valid:true,totalRows:1,totalBills:1,manifest:{direction:'EXPORT',bills:[{blNumber:'BL-HTTP',containers:[{isReefer:true,isoType:'20RF'}]}]}}});
    expect(used).toHaveBeenCalledWith(owner, ['BL-HTTP']);
    expect(save).not.toHaveBeenCalled();
  });
  it('returns row errors for missing equipment and never creates partial records', async () => {
    lookup.mockResolvedValue([]);
    const response = await upload(buildFile());
    expect(response.status).toBe(200);
    expect(await response.json()).toMatchObject({data:{valid:false,errors:[{row:2,column:'Số container'}]}});
    expect(save).not.toHaveBeenCalled();
  });
  it('reports DB capacity as a retryable service error rather than a missing container', async () => {
    lookup.mockRejectedValue(Object.assign(new Error('capacity'), {code:'53300'}));
    const response = await upload(buildFile());
    expect(response.status).toBe(503);
    expect(response.headers.get('retry-after')).toBe('3');
    expect(await response.json()).toMatchObject({error:{code:'DB_CONNECTION_LIMIT'}});
    expect(save).not.toHaveBeenCalled();
  });
  it('rejects files over 5 MB, unsupported extensions and wrong form fields', async () => {
    expect((await upload(Buffer.alloc(5*1024*1024+1))).status).toBe(422);
    expect((await upload(buildFile(), 'Manifest.xls')).status).toBe(422);
    expect((await upload(buildFile(), 'Manifest.xlsx', true, 'other')).status).toBe(422);
    expect(save).not.toHaveBeenCalled();
  });
});
