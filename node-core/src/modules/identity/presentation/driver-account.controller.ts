import { Request, Response, NextFunction } from 'express';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { query } from '../../../infrastructure/database/db';
import { ForbiddenError, UnauthorizedError } from '../../../shared/errors/app-error';

interface ActorRequest extends Request { actor?: {id: string; role: string} }
export const authenticateAccountManager = async (req: ActorRequest, _res: Response, next: NextFunction) => {
  try {
    const token = req.headers.authorization?.match(/^Bearer\s+(.+)$/i)?.[1];
    if (!token) throw new UnauthorizedError();
    let payload: JwtPayload;
    try {
      payload = jwt.verify(token, process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!', {algorithms: ['HS256']}) as JwtPayload;
    } catch { throw new UnauthorizedError(); }
    const id = payload.sub || payload.id;
    if (typeof id !== 'string' || !/^[0-9a-f-]{36}$/i.test(id)) throw new UnauthorizedError();
    const user = (await query('SELECT id,role,is_active,session_id FROM users WHERE id=$1', [id])).rows[0];
    if (!user?.is_active || (user.session_id && user.session_id !== payload.sessionId)) throw new UnauthorizedError();
    if (!['Administrator', 'Transport Company'].includes(user.role)) throw new ForbiddenError();
    req.actor = user;
    next();
  } catch (error) { next(error); }
};
