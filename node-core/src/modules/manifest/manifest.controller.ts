import { Router, Request, Response, NextFunction } from 'express';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { z } from 'zod';
import { query } from '../../infrastructure/database/db';
import { ForbiddenError, UnauthorizedError, ValidationError } from '../../shared/errors/app-error';
import { sendCreated, sendSuccess } from '../../shared/utils/response';
import { manifestSchema } from './manifest.schema';
import { ManifestRepository } from './manifest.repository';
import { isValidIso6346, normalizeContainerNumber } from '../container/application/container.validator';
import multer from 'multer';
import { ManifestImportService } from './manifest-import.service';

type ManifestRequest = Request & { manifestOwner?: string; userRole?: string };
export function createManifestRouter(repository = new ManifestRepository()): Router {
  const router = Router();
  router.use(async (req: ManifestRequest, _res: Response, next: NextFunction) => {
    try {
      const token = req.headers.authorization?.match(/^Bearer\s+(.+)$/i)?.[1];
      if (token && process.env.JWT_SECRET) {
        try {
          const payload = jwt.verify(token, process.env.JWT_SECRET, { algorithms: ['HS256'] }) as JwtPayload;
          const id = z.string().uuid().safeParse(payload.sub || payload.id);
          if (id.success) {
            const user = (await query("SELECT id, role FROM users WHERE id=$1 AND is_active=true", [id.data])).rows[0];
            if (user) {
              req.manifestOwner = id.data;
              req.userRole = user.role;
            }
          }
        } catch {
          // Leave the request unauthenticated if token verification or account lookup fails.
        }
      }

      // Cho phép các endpoint /port/* truy cập tự do để đồng bộ real-time giữa các máy
      if (req.path.startsWith('/port/')) {
        return next();
      }

      if (!req.manifestOwner) throw new UnauthorizedError('Vui lòng đăng nhập.');
      next();
    } catch (error) { next(error); }
  });

  // ── Port Synchronization Routes (Cho phép Dispatcher, Admin, Shipping Line, Transport truy cập chung) ──
  router.get('/port/synced', async (_req: ManifestRequest, res, next) => {
    try { sendSuccess(res, await repository.listSyncedForPort()); } catch (e) { next(e); }
  });
  router.post('/port/approve/:id', async (req: ManifestRequest, res, next) => {
    try {
      const id = String(req.params.id || '').trim();
      sendSuccess(res, await repository.approvePort(id));
    } catch (e) { next(e); }
  });
  router.post('/port/reset-pending/:id', async (req: ManifestRequest, res, next) => {
    try {
      const id = String(req.params.id || '').trim();
      sendSuccess(res, await repository.resetPortPending(id));
    } catch (e) { next(e); }
  });
  router.post('/port/reset-all', async (_req: ManifestRequest, res, next) => {
    try {
      sendSuccess(res, await repository.resetAll());
    } catch (e) { next(e); }
  });
  router.post('/port/bookings/:id', async (req: ManifestRequest, res, next) => {
    try {
      const id = String(req.params.id || '').trim();
      const bookings = Array.isArray(req.body.bookings) ? req.body.bookings : [];
      sendSuccess(res, await repository.savePortBookings(id, bookings));
    } catch (e) { next(e); }
  });

  // ── Shipping Line Specific Routes ──
  const requireShippingLine = (req: ManifestRequest) => {
    if (req.userRole !== 'Shipping Line' && req.userRole !== 'Administrator') {
      throw new ForbiddenError('Chỉ tài khoản hãng tàu đang hoạt động được quản lý bản khai.');
    }
  };

  router.get('/', async (req: ManifestRequest, res, next) => {
    try {
      if (req.userRole === 'Dispatcher' || req.userRole === 'Administrator') {
        sendSuccess(res, await repository.listSyncedForPort());
      } else {
        requireShippingLine(req);
        sendSuccess(res, await repository.list(req.manifestOwner!, String(req.query.direction || '')));
      }
    } catch (e) { next(e); }
  });
  const importer = new ManifestImportService(repository);
  router.get('/import/template', (req: ManifestRequest, res, next) => {
    try {
      requireShippingLine(req);
      res.type('application/vnd.openxmlformats-officedocument.spreadsheetml.sheet');
      res.attachment('Manifest_mau.xlsx').send(importer.buildTemplate());
    } catch (e) { next(e); }
  });
  const upload = multer({ storage: multer.memoryStorage(), limits: { fileSize: 5 * 1024 * 1024, files: 1, fields: 0, parts: 1 } });
  router.post('/import/preview', upload.single('file'), async (req: ManifestRequest, res, next) => {
    try {
      requireShippingLine(req);
      sendSuccess(res, await importer.preview(req.file, req.manifestOwner!));
    } catch (e) { next(e); }
  });
  router.get('/container-lookup', async (req: ManifestRequest, res, next) => {
    try {
      requireShippingLine(req);
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
      requireShippingLine(req);
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
    try {
      requireShippingLine(req);
      sendSuccess(res, await repository.send(req.manifestOwner!, parseId(req.params.id)));
    } catch (e) { next(e); }
  });
  router.post('/:id/recall', async (req: ManifestRequest, res, next) => {
    try {
      requireShippingLine(req);
      await repository.recall(req.manifestOwner!, parseId(req.params.id));
      sendSuccess(res, { recalled: true });
    } catch (e) { next(e); }
  });
  return router;
}
function parseId(id: string): string {
  const result = z.string().uuid().safeParse(id);
  if (!result.success) throw new ValidationError('Mã bản khai không hợp lệ.');
  return result.data;
}
