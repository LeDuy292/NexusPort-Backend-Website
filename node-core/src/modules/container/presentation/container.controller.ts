import { ContainerJourneyRepository } from '../infrastructure/container-journey.repository';
import { NextFunction, Request, Response, Router } from 'express';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { z, ZodError, ZodSchema, ZodTypeDef } from 'zod';
import { ForbiddenError, UnauthorizedError, ValidationError } from '../../../shared/errors/app-error';
import { sendCreated, sendSuccess } from '../../../shared/utils/response';
import { ContainerService } from '../application/container.service';
import { ContainerStatusService } from '../application/container-status.service';
import { ContainerIntakeService } from '../application/container-intake.service';
import { ShippingLineDirectoryRepository } from '../infrastructure/shipping-line-directory.repository';
import { ContainerSearchDto } from '../application/container.dto';
import { containerIdentitySearchSchema, containerSearchSchema, idSchema, registerContainerIdentitySchema, transitionContainerStatusSchema } from '../application/container.validator';
type ContainerRole = 'Administrator' | 'Dispatcher' | 'Yard Staff' | 'Gate Officer' | 'Carrier' | 'Shipping Line' | 'Driver' | 'Berth Staff';
interface AuthorizedRequest extends Request {
    containerUser?: {
        id: string;
        role: ContainerRole;
    };
}
const roleAliases: Record<string, ContainerRole> = {
    administrator: 'Administrator', admin: 'Administrator', dispatcher: 'Dispatcher', operation: 'Dispatcher',
    'yard staff': 'Yard Staff', 'yard operator': 'Yard Staff', yard: 'Yard Staff',
    'gate officer': 'Gate Officer', gate: 'Gate Officer', carrier: 'Carrier',
    'carrier staff': 'Carrier', 'transport company': 'Carrier', 'transportcompany': 'Carrier',
    'shipping line': 'Shipping Line', shippingline: 'Shipping Line', driver: 'Driver', 'berth staff': 'Berth Staff',
};
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const shippingLineSchema = z.object({
    fullName: z.string().trim().min(1, 'Nhập tên đầy đủ của hãng tàu.').max(250),
    shortName: z.string().trim().min(1, 'Nhập tên viết tắt của hãng tàu.').max(80),
    prefixes: z.array(z.string().trim().toUpperCase().regex(/^[A-Z]{3}[UJZ]$/, 'Tiền tố BIC gồm 3 chữ cái và ký tự U, J hoặc Z.'))
        .min(1, 'Nhập ít nhất một tiền tố BIC.').max(100).transform(values => [...new Set(values)]),
    userIds: z.array(z.string().uuid().toLowerCase()).max(100).transform(values => [...new Set(values)]),
}).strict();
const vietnameseValidationMessage = (issue: ZodError['issues'][number]): string => {
    if (issue.message !== 'Invalid input' && !issue.message.startsWith('Invalid'))
        return issue.message;
    if (issue.code === 'invalid_type')
        return 'Giá trị không đúng kiểu dữ liệu yêu cầu.';
    if (issue.code === 'invalid_enum_value')
        return 'Giá trị không nằm trong danh sách được hỗ trợ.';
    if (issue.code === 'invalid_string')
        return 'Giá trị không đúng định dạng yêu cầu.';
    return 'Giá trị không hợp lệ.';
};
const parse = <T>(schema: ZodSchema<T, ZodTypeDef, unknown>, value: unknown): T => {
    try {
        return schema.parse(value);
    }
    catch (error) {
        if (error instanceof ZodError) {
            const details = error.issues.reduce<Record<string, string[]>>((result, issue) => {
                const field = issue.path.join('.') || 'request';
                result[field] = [...(result[field] ?? []), vietnameseValidationMessage(issue)];
                return result;
            }, {});
            throw new ValidationError('Dữ liệu Container không hợp lệ.', details);
        }
        throw error;
    }
};
const authenticateContainerUser = (req: AuthorizedRequest, _res: Response, next: NextFunction) => {
    const token = req.headers.authorization?.match(/^Bearer\s+(.+)$/i)?.[1];
    if (!token)
        return next(new UnauthorizedError('Vui lòng đăng nhập để sử dụng chức năng quản lý Container.'));
    try {
        const secret = process.env.JWT_SECRET;
        if (!secret) return next(new UnauthorizedError('Dịch vụ chưa cấu hình xác thực Container.'));
        const payload = jwt.verify(token, secret, { algorithms: ['HS256'] }) as JwtPayload;
        const rawRole = String(payload.role ?? payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] ?? '').trim().toLowerCase();
        const role = Object.hasOwn(roleAliases, rawRole) ? roleAliases[rawRole] : undefined;
        if (!role)
            return next(new ForbiddenError('Vai trò của bạn không có quyền truy cập chức năng quản lý Container.'));
        const userId = String(payload.sub ?? payload.id ?? '');
        if (!UUID_PATTERN.test(userId))
            return next(new UnauthorizedError('Token đăng nhập không xác định được người dùng hợp lệ.'));
        req.containerUser = { id: userId, role };
        next();
    }
    catch {
        next(new UnauthorizedError('Token đăng nhập không hợp lệ hoặc đã hết hạn.'));
    }
};
const allowRoles = (...roles: ContainerRole[]) => (req: AuthorizedRequest, _res: Response, next: NextFunction) => {
    if (!req.containerUser || !roles.includes(req.containerUser.role)) {
        return next(new ForbiddenError('Bạn không có quyền thực hiện thao tác này.'));
    }
    next();
};
export class ContainerController {
    constructor(private readonly service = new ContainerService(), private readonly statusService = new ContainerStatusService(), private readonly intakeService = new ContainerIntakeService()) { }
    registerIdentity = async (req: AuthorizedRequest, res: Response, next: NextFunction) => {
        try {
            const dto = parse(registerContainerIdentitySchema, req.body);
            sendCreated(res, await this.service.registerIdentity(dto, req.containerUser!.id));
        }
        catch (error) {
            next(error);
        }
    };
    listImports = async (_req: Request, res: Response, next: NextFunction) => {
        try {
            sendSuccess(res, await this.intakeService.listImports());
        }
        catch (error) {
            next(error);
        }
    };
    listTransportDeclarations = async (req: AuthorizedRequest, res: Response, next: NextFunction) => {
        try {
            if (!/^[A-Z0-9]{4,11}$/.test(String(req.query.containerNumber??'').trim().toUpperCase())) throw new ValidationError('Nhập số Container để tìm bản khai.');
            sendSuccess(res, await this.intakeService.listTransportDeclarations(req.containerUser!.id, String(req.query.containerNumber ?? '')));
        }
        catch (error) {
            next(error);
        }
    };
    getAll = async (req: Request, res: Response, next: NextFunction) => {
        try {
            if (String(req.query.search??'').trim().length<4) throw new ValidationError('Nhập điều kiện trước khi tìm kiếm Container.');
            sendSuccess(res, await this.service.getAll(parse(containerSearchSchema, req.query) as ContainerSearchDto));
        }
        catch (error) {
            next(error);
        }
    };
    getTypes = async (_req: Request, res: Response, next: NextFunction) => {
        try {
            sendSuccess(res, await this.service.getTypes());
        }
        catch (error) {
            next(error);
        }
    };
    getCargoTypes = async (_req: Request, res: Response, next: NextFunction) => {
        try {
            sendSuccess(res, await this.intakeService.getCargoTypes());
        }
        catch (error) {
            next(error);
        }
    };
    getById = async (req: Request, res: Response, next: NextFunction) => {
        try {
            sendSuccess(res, await this.service.getById(parse(idSchema, req.params.id)));
        }
        catch (error) {
            next(error);
        }
    };
    getCurrentStatus = async (req: Request, res: Response, next: NextFunction) => {
        try {
            sendSuccess(res, await this.statusService.getCurrentStatus(parse(idSchema, req.params.id)));
        }
        catch (error) {
            next(error);
        }
    };
    getStatusHistory = async (req: Request, res: Response, next: NextFunction) => {
        try {
            sendSuccess(res, await this.statusService.getHistory(parse(idSchema, req.params.id)));
        }
        catch (error) {
            next(error);
        }
    };
    transitionStatus = async (req: AuthorizedRequest, res: Response, next: NextFunction) => {
        try {
            const id = parse(idSchema, req.params.id);
            const dto = parse(transitionContainerStatusSchema, req.body);
            if (['gate_in','gate_out'].includes(dto.status)) throw new ValidationError('Ghi nhận vào/ra qua nghiệp vụ cổng gắn với đúng booking và lượt bãi.');
            sendSuccess(res, await this.statusService.transition(id, dto.status, req.containerUser!.id, req.ip));
        }
        catch (error) {
            next(error);
        }
    };
}
export const createContainerRouter = (): Router => {
    const router = Router();
    const controller = new ContainerController();
    const readers: ContainerRole[] = ['Administrator', 'Dispatcher', 'Yard Staff', 'Gate Officer', 'Carrier'];
    const identityReaders: ContainerRole[] = [...readers, 'Shipping Line', 'Driver', 'Berth Staff'];
    const writers: ContainerRole[] = ['Administrator', 'Dispatcher', 'Gate Officer'];
    router.use(authenticateContainerUser);
    router.get('/types', allowRoles(...identityReaders), controller.getTypes);
    router.get('/cargo-types', allowRoles(...readers), controller.getCargoTypes);
    router.get('/intake/imports', allowRoles('Administrator', 'Dispatcher'), controller.listImports);
    router.get('/intake/transport/declarations', allowRoles('Carrier'), controller.listTransportDeclarations);
    router.post('/intake/shipping-line/manual', allowRoles('Shipping Line'), controller.registerIdentity);
    const shippingLines = new ShippingLineDirectoryRepository();
    router.get('/shipping-lines', allowRoles(...identityReaders), async (_req, res, next) => {
        try { sendSuccess(res, await shippingLines.list()); } catch (error) { next(error); }
    });
    router.get('/shipping-lines/me', allowRoles('Shipping Line'), async (req: AuthorizedRequest, res, next) => {
        try { sendSuccess(res, await shippingLines.own(req.containerUser!.id)); } catch (error) { next(error); }
    });
    router.get('/shipping-lines/accounts', allowRoles('Administrator'), async (req: AuthorizedRequest, res, next) => {
        try { sendSuccess(res, await shippingLines.accounts(req.containerUser!.id)); } catch (error) { next(error); }
    });
    router.post('/shipping-lines', allowRoles('Administrator'), async (req: AuthorizedRequest, res, next) => {
        try { sendCreated(res, await shippingLines.save(parse(shippingLineSchema, req.body), req.containerUser!.id)); }
        catch (error) { next(error); }
    });
    router.put('/shipping-lines/:id', allowRoles('Administrator'), async (req: AuthorizedRequest, res, next) => {
        try { sendSuccess(res, await shippingLines.save(parse(shippingLineSchema, req.body), req.containerUser!.id, parse(idSchema, req.params.id))); }
        catch (error) { next(error); }
    });
    const journeys = new ContainerJourneyRepository();
    router.get('/identities', allowRoles(...identityReaders), async (req, res, next) => {
        try {
            sendSuccess(res, await journeys.searchIdentities(parse(containerIdentitySearchSchema, req.query)));
        } catch (error) {
            next(error);
        }
    });
    router.get('/lookup', allowRoles(...identityReaders), async (req,res,next) => {
      try { sendSuccess(res,await journeys.lookup(String(req.query.containerNumber??'').trim().toUpperCase())); } catch(e){next(e);}
    });
    router.get('/journeys', allowRoles(...readers), async (req: AuthorizedRequest,res,next) => {
      try { sendSuccess(res,await journeys.search(req.query,req.containerUser!.role==='Carrier')); } catch(e){next(e);}
    });
    router.get('/own-visits/:id', allowRoles('Carrier'), async (req: AuthorizedRequest,res,next) => {
      try { sendSuccess(res,await journeys.ownVisits(parse(idSchema,req.params.id),req.containerUser!.id)); } catch(e){next(e);}
    });
    router.get('/legacy-bookings',allowRoles('Administrator','Dispatcher'),async(req,res,next)=>{
      try{sendSuccess(res,await journeys.legacyBookings(String(req.query.containerNumber??'').trim().toUpperCase()));}catch(e){next(e);}
    });
    router.get('/legacy-bookings/:id/options',allowRoles('Administrator','Dispatcher'),async(req,res,next)=>{
      try{sendSuccess(res,await journeys.legacyOptions(parse(idSchema,req.params.id),parse(idSchema,req.query.containerId)));}catch(e){next(e);}
    });
    router.post('/legacy-bookings/:id/link',allowRoles('Administrator','Dispatcher'),async(req:AuthorizedRequest,res,next)=>{
      try{sendSuccess(res,await journeys.reconcile(parse(idSchema,req.params.id),parse(idSchema,req.body.containerId),parse(idSchema,req.body.declarationId),req.body.visitId?parse(idSchema,req.body.visitId):null,req.containerUser!.id));}catch(e){next(e);}
    });
    router.get('/gates',allowRoles('Administrator','Dispatcher','Gate Officer'),async(_req,res,next)=>{
      try{const {query}=await import('../../../infrastructure/database/db');sendSuccess(res,(await query('SELECT id,code,name FROM gates WHERE is_active ORDER BY code')).rows);}catch(e){next(e);}
    });
    router.get('/access-carriers',allowRoles('Administrator','Dispatcher'),async(req,res,next)=>{
      try{sendSuccess(res,await journeys.carriers(String(req.query.search??'')));}catch(e){next(e);}
    });
    router.post('/visits/:id/access',allowRoles('Administrator','Dispatcher'),async(req:AuthorizedRequest,res,next)=>{
      try{sendSuccess(res,await journeys.grantVisit(parse(idSchema,req.params.id),parse(idSchema,req.body.carrierId),req.containerUser!.id));}catch(e){next(e);}
    });
    router.get('/', allowRoles('Administrator','Dispatcher','Yard Staff','Gate Officer'), controller.getAll);
    router.get('/:id/status/history', allowRoles('Administrator','Dispatcher','Yard Staff','Gate Officer'), controller.getStatusHistory);
    router.get('/:id/status', allowRoles('Administrator','Dispatcher','Yard Staff','Gate Officer'), controller.getCurrentStatus);
    router.post('/:id/status/transition', allowRoles(...writers), controller.transitionStatus);
    router.get('/:id', allowRoles('Administrator','Dispatcher','Yard Staff','Gate Officer'), controller.getById);
    return router;
};
