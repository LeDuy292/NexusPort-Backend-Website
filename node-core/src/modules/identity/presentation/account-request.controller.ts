import { Router, Request, Response, NextFunction } from 'express';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { query } from '../../../infrastructure/database/db';
import { ForbiddenError, UnauthorizedError, ValidationError } from '../../../shared/errors/app-error';
import { AccountRequestService } from '../application/account-request.service';
interface ActorRequest extends Request { actor?: {id:string;role:string} }
export const authenticateAccountManager = async (req: ActorRequest, _res: Response, next: NextFunction) => {
 try {
  const token = req.headers.authorization?.match(/^Bearer\s+(.+)$/i)?.[1];
  if (!token) throw new UnauthorizedError();
  let payload: JwtPayload;
  try {payload=jwt.verify(token,process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!',{algorithms:['HS256']}) as JwtPayload;} catch {throw new UnauthorizedError();}
  const id = payload.sub || payload.id;
  if (typeof id!=='string' || !/^[0-9a-f-]{36}$/i.test(id)) throw new UnauthorizedError();
  const user = (await query('SELECT id,role,is_active,session_id FROM users WHERE id=$1',[id])).rows[0];
  if (!user?.is_active || (user.session_id && user.session_id!==payload.sessionId)) throw new UnauthorizedError();
  if (!['Administrator','Transport Company'].includes(user.role)) throw new ForbiddenError();
  req.actor=user;next();
 } catch(error) {next(error);}
};
export const createAccountRequestRouter = () => {
 const router=Router(); const service=new AccountRequestService();
 router.use(authenticateAccountManager);
 const admin = (req:ActorRequest) => {if(req.actor?.role!=='Administrator') throw new ForbiddenError();};
 const id = (req:Request) => {if(!/^[0-9a-f-]{36}$/i.test(req.params.id)) throw new ValidationError('ID không hợp lệ.');return req.params.id;};
 router.get('/driver-links',async(req:ActorRequest,res,next)=>{try {
  const result=await query(`SELECT d.id,d.user_id AS "userId",u.username,u.email,u.invitation_pending AS "invitationPending"
   FROM drivers d LEFT JOIN users u ON u.id=d.user_id WHERE $2::boolean OR EXISTS(
    SELECT 1 FROM carrier_users cu WHERE cu.user_id=$1 AND cu.carrier_id=d.carrier_id)`,[req.actor!.id,req.actor!.role==='Administrator']);
  res.json({success:true,data:result.rows});
 }catch(e){next(e);}});
 router.get('/',async(req:ActorRequest,res,next)=>{try {res.json({success:true,data:await service.list(req.actor!.id,req.actor!.role==='Administrator')});}catch(e){next(e);}});
 router.post('/',async(req:ActorRequest,res,next)=>{try {if(req.actor!.role!=='Transport Company') throw new ForbiddenError();res.status(201).json({success:true,data:await service.submit(req.actor!.id,req.body)});}catch(e){next(e);}});
 for (const action of ['approve','reject'] as const) router.post(`/:id/${action}`,async(req:ActorRequest,res,next)=>{try{admin(req);res.json({success:true,data:await service.review(req.actor!.id,id(req),action==='approve')});}catch(e){next(e);}});
 router.post('/:id/resend',async(req:ActorRequest,res,next)=>{try{admin(req);res.json({success:true,data:await service.resend(id(req))});}catch(e){next(e);}});
 return router;
};
