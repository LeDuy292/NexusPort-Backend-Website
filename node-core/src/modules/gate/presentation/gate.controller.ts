import { Request, Response } from 'express';
import { Router } from 'express';
import { GateRepository } from '../infrastructure/gate.repository';
import { sendSuccess, sendCreated } from '../../../shared/utils/response';
import { query } from '../../../infrastructure/database/db';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { GateMovementService } from '../application/gate-movement.service';
import { ForbiddenError, UnauthorizedError } from '../../../shared/errors/app-error';

export class GateController {
  completeOut = async (req: Request, res: Response, next: import('express').NextFunction): Promise<void> => {
    try {
      const token = req.headers.authorization?.match(/^Bearer\s+(.+)$/i)?.[1];
      if (!token) throw new UnauthorizedError('Nhân viên cổng cần đăng nhập.');
      let payload: JwtPayload;
      try { payload = jwt.verify(token, process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!', { algorithms: ['HS256'] }) as JwtPayload; }
      catch { throw new UnauthorizedError('Token không hợp lệ hoặc đã hết hạn.'); }
      const role = String(payload.role ?? payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] ?? '').toLowerCase();
      if (!['gate officer', 'gate', 'admin', 'administrator'].includes(role)) throw new ForbiddenError('Chỉ nhân viên cổng được xác nhận xe đã ra cổng.');
      const actor = payload.sub ?? payload.id ?? payload['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier'];
      if (typeof actor !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(actor)) throw new UnauthorizedError('Tài khoản cổng không hợp lệ.');
      sendSuccess(res, await new GateMovementService().completeOut(actor, req.body));
    } catch (error) { next(error); }
  };

  /**
   * GET /api/gate/verify-plate?plate=47AB-294.69&gateCode=GATE_IN_A
   * Dùng bởi gate_runner.py (ESP32 OCR) để kiểm tra biển số có booking hợp lệ không.
   * Trả về { valid: true/false, booking: {...} | null }
   */
  verifyPlate = async (req: Request, res: Response, next: import('express').NextFunction): Promise<void> => {
    try {
      const rawPlate = String(req.query.plate ?? '').trim();
      if (!rawPlate) {
        res.json({ valid: false, reason: 'missing_plate' });
        return;
      }

      // Chuẩn hoá biển số: bỏ ký tự thừa, uppercase để so sánh linh hoạt
      // "47-AB 294.69" / "47AB29469" / "47AB-294.69" → đều tìm được
      const normalized = rawPlate.toUpperCase().replace(/[\s\-\.]/g, '');

      const result = await query(
        `SELECT id, booking_code, vehicle_plate, status, appointment_start, appointment_end, driver_name, gate_type
         FROM bookings
         WHERE status IN ('approved', 'ready', 'checked_in')
           AND appointment_start <= now()
           AND appointment_end   >= now()
           AND UPPER(REPLACE(REPLACE(REPLACE(vehicle_plate, '-', ''), '.', ''), ' ', '')) = $1
         ORDER BY appointment_start ASC
         LIMIT 1`,
        [normalized]
      );

      if (result.rows.length === 0) {
        console.log(`[verify-plate] plate='${rawPlate}' normalized='${normalized}' → NOT FOUND`);
        res.json({ valid: false, reason: 'no_active_booking', plate: rawPlate });
        return;
      }

      const booking = result.rows[0];
      console.log(`[verify-plate] plate='${rawPlate}' → FOUND booking ${booking.booking_code} (${booking.status})`);
      res.json({
        valid: true,
        plate: rawPlate,
        booking: {
          id: booking.id,
          bookingCode: booking.booking_code,
          vehiclePlate: booking.vehicle_plate,
          status: booking.status,
          driverName: booking.driver_name,
          gateType: booking.gate_type,
          appointmentStart: booking.appointment_start,
          appointmentEnd: booking.appointment_end,
        }
      });
    } catch (error) { next(error); }
  };

  locations = async (_req: Request, res: Response, next: import('express').NextFunction): Promise<void> => {
    try { sendSuccess(res, (await query(`SELECT id,code,name,CASE WHEN code IN ('GATE-02','GATE-03') THEN 'gate_out' ELSE 'gate_in' END AS "gateType",is_active AS "isActive" FROM gates WHERE is_active AND code IN ('GATE-01','GATE-02','GATE-03') ORDER BY code`)).rows); }
    catch (error) { next(error); }
  };
  private repository = new GateRepository();

  getAll = async (req: Request, res: Response): Promise<void> => {
    const items = await this.repository.findAll();
    sendSuccess(res, items);
  };

  getById = async (req: Request, res: Response): Promise<void> => {
    const item = await this.repository.findById(req.params.id);
    if (!item) {
      res.status(404).json({ success: false, message: 'Not found' });
      return;
    }
    sendSuccess(res, item);
  };

  create = async (req: Request, res: Response): Promise<void> => {
    const item = await this.repository.create({
      name: req.body.name || 'Default Name',
      status: 'Active',
      description: req.body.description
    });
    sendCreated(res, item);
  };
}

export const createGateRouter = (): Router => {
  const router = Router();
  const controller = new GateController();

  router.get('/verify-plate', controller.verifyPlate);   // ← ESP32 OCR verify
  router.get('/locations', controller.locations);
  router.post('/complete-out', controller.completeOut);
  router.get('/', controller.getAll);
  router.get('/:id', controller.getById);
  router.post('/', controller.create);

  return router;
};
