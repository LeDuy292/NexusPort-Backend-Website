import { NextFunction, Request, Response, Router } from 'express';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { ZodError, ZodSchema } from 'zod';
import { ForbiddenError, UnauthorizedError, ValidationError } from '../../../shared/errors/app-error';
import { sendCreated, sendSuccess } from '../../../shared/utils/response';
import { ContainerService } from '../application/container.service';
import { ContainerSearchDto } from '../application/container.dto';
import { containerSearchSchema, createContainerSchema, idSchema, updateContainerSchema } from '../application/container.validator';

type ContainerRole = 'Administrator' | 'Dispatcher' | 'Yard Staff' | 'Gate Officer';
interface AuthorizedRequest extends Request { containerUser?: { id: string; role: ContainerRole } }

const roleAliases: Record<string, ContainerRole> = {
  administrator: 'Administrator', admin: 'Administrator', dispatcher: 'Dispatcher', operation: 'Dispatcher',
  'yard staff': 'Yard Staff', 'yard operator': 'Yard Staff', yard: 'Yard Staff',
  'gate officer': 'Gate Officer', gate: 'Gate Officer',
};

const parse = <T>(schema: ZodSchema<T>, value: unknown): T => {
  try { return schema.parse(value); }
  catch (error) {
    if (error instanceof ZodError) {
      const details = error.issues.reduce<Record<string, string[]>>((result, issue) => {
        const field = issue.path.join('.') || 'request';
        result[field] = [...(result[field] ?? []), issue.message];
        return result;
      }, {});
      throw new ValidationError('Container data is invalid.', details);
    }
    throw error;
  }
};

const authenticateContainerUser = (req: AuthorizedRequest, _res: Response, next: NextFunction) => {
  const token = req.headers.authorization?.match(/^Bearer\s+(.+)$/i)?.[1];
  if (!token) return next(new UnauthorizedError('A valid bearer token is required.'));
  try {
    const payload = jwt.verify(token, process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!', { algorithms: ['HS256'] }) as JwtPayload;
    const rawRole = String(payload.role ?? payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] ?? '').toLowerCase();
    const role = roleAliases[rawRole];
    if (!role) return next(new ForbiddenError('Your role cannot access Container Management.'));
    req.containerUser = { id: String(payload.sub ?? payload.id ?? ''), role };
    next();
  } catch {
    next(new UnauthorizedError('Bearer token is invalid or expired.'));
  }
};

const allowRoles = (...roles: ContainerRole[]) => (req: AuthorizedRequest, _res: Response, next: NextFunction) => {
  if (!req.containerUser || !roles.includes(req.containerUser.role)) {
    return next(new ForbiddenError('You do not have permission to perform this action.'));
  }
  next();
};

export class ContainerController {
  constructor(private readonly service = new ContainerService()) {}

  getAll = async (req: Request, res: Response, next: NextFunction) => {
    try { sendSuccess(res, await this.service.getAll(parse(containerSearchSchema, req.query) as ContainerSearchDto)); }
    catch (error) { next(error); }
  };

  getTypes = async (_req: Request, res: Response, next: NextFunction) => {
    try { sendSuccess(res, await this.service.getTypes()); }
    catch (error) { next(error); }
  };

  getById = async (req: Request, res: Response, next: NextFunction) => {
    try { sendSuccess(res, await this.service.getById(parse(idSchema, req.params.id))); }
    catch (error) { next(error); }
  };

  create = async (req: Request, res: Response, next: NextFunction) => {
    try { sendCreated(res, await this.service.create(parse(createContainerSchema, req.body))); }
    catch (error) { next(error); }
  };

  update = async (req: Request, res: Response, next: NextFunction) => {
    try {
      const id = parse(idSchema, req.params.id);
      sendSuccess(res, await this.service.update(id, parse(updateContainerSchema, req.body)));
    } catch (error) { next(error); }
  };

  remove = async (req: Request, res: Response, next: NextFunction) => {
    try {
      await this.service.softDelete(parse(idSchema, req.params.id));
      res.status(204).send();
    } catch (error) { next(error); }
  };
}

export const createContainerRouter = (): Router => {
  const router = Router();
  const controller = new ContainerController();
  const readers: ContainerRole[] = ['Administrator', 'Dispatcher', 'Yard Staff', 'Gate Officer'];
  const writers: ContainerRole[] = ['Administrator', 'Dispatcher', 'Gate Officer'];

  router.use(authenticateContainerUser);
  router.get('/types', allowRoles(...readers), controller.getTypes);
  router.get('/', allowRoles(...readers), controller.getAll);
  router.get('/:id', allowRoles(...readers), controller.getById);
  router.post('/', allowRoles(...writers), controller.create);
  router.put('/:id', allowRoles(...writers), controller.update);
  router.delete('/:id', allowRoles('Administrator'), controller.remove);
  return router;
};
