import { Router, Request, Response, NextFunction } from 'express';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { z } from 'zod';
import { query } from '../../infrastructure/database/db';
import { ForbiddenError, UnauthorizedError, ValidationError } from '../../shared/errors/app-error';
import { sendCreated, sendSuccess } from '../../shared/utils/response';
import { manifestSchema } from './manifest.schema';
import { ManifestRepository } from './manifest.repository';
import { isValidIso6346, normalizeContainerNumber } from '../container/application/container.validator';

type ManifestRequest = Request & { manifestOwner?: string };
export function createManifestRouter(repository = new ManifestRepository()): Router {
  const router = Router();
  router.use(async (req: ManifestRequest, _res: Response, next: NextFunction) => {
    try {
      const token = req.headers.authorization?.match(/^Bearer\s+(.+)$/i)?.[1];
      if (!token || !process.env.JWT_SECRET) throw new UnauthorizedError('Vui lòng đăng nhập.');
      let payload: JwtPayload;
      try { payload = jwt.verify(token, process.env.JWT_SECRET, { algorithms: ['HS256'] }) as JwtPayload; }
      catch { throw new UnauthorizedError('Phiên đăng nhập không hợp lệ.'); }
      const id = z.string().uuid().safeParse(payload.sub || payload.id);
      if (!id.success) throw new UnauthorizedError('Phiên đăng nhập không hợp lệ.');
      const user = (await query("SELECT id FROM users WHERE id=$1 AND is_active=true AND role='Shipping Line'", [id.data])).rows[0];
      if (!user) throw new ForbiddenError('Chỉ tài khoản hãng tàu đang hoạt động được quản lý bản khai.');
      req.manifestOwner = id.data;
      next();
    } catch (error) { next(error); }
  });
  router.get('/', async (req: ManifestRequest, res, next) => {
    try { sendSuccess(res, await repository.list(req.manifestOwner!, String(req.query.direction || ''))); } catch (e) { next(e); }
  });
  router.get('/container-lookup', async (req: ManifestRequest, res, next) => {
    try {
      const number = normalizeContainerNumber(String(req.query.containerNumber || ''));
      if (!/^[A-Z]{3}[UJZ]\d{7}$/.test(number)) throw new ValidationError('Nhập đầy đủ số container gồm 11 ký tự.');
      if (!isValidIso6346(number)) throw new ValidationError('Số container sai chữ số kiểm tra ISO 6346. Vui lòng kiểm tra lại số container.');
      sendSuccess(res, await repository.lookupContainer(number));
    } catch (e) { next(e); }
  });
  router.get('/:id', async (req: ManifestRequest, res, next) => {
    try { sendSuccess(res, await repository.detail(req.manifestOwner!, parseId(req.params.id))); } catch (e) { next(e); }
  });
  const save = (update: boolean) => async (req: ManifestRequest, res: Response, next: NextFunction) => {
    try {
      const parsed = manifestSchema.safeParse(req.body);
      if (!parsed.success) throw new ValidationError(parsed.error.issues.map(i => {
        const bill = typeof i.path[1] === 'number' ? `Vận đơn ${i.path[1] + 1}: ` : '';
        const container = typeof i.path[3] === 'number' ? `Container ${i.path[3] + 1}: ` : '';
        return bill + container + i.message;
      }).join('\n'));
      const document = await repository.save(req.manifestOwner!, parsed.data, update ? parseId(req.params.id) : undefined);
      if (update) sendSuccess(res, document); else sendCreated(res, document);
    } catch (e) { next(e); }
  };
  router.post('/', save(false));
  router.put('/:id', save(true));
  router.post('/:id/send-to-port', async (req: ManifestRequest, res, next) => {
    try { sendSuccess(res, await repository.send(req.manifestOwner!, parseId(req.params.id))); } catch (e) { next(e); }
  });
  router.post('/:id/recall', async (req: ManifestRequest, res, next) => {
    try { await repository.recall(req.manifestOwner!, parseId(req.params.id)); sendSuccess(res, { recalled: true }); } catch (e) { next(e); }
  });
  return router;
}
function parseId(id: string): string {
  const result = z.string().uuid().safeParse(id);
  if (!result.success) throw new ValidationError('Mã bản khai không hợp lệ.');
  return result.data;
}
