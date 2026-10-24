import { ReeferRepository } from '../infrastructure/reefer.repository';
import {
  ReeferPlug,
  ReeferPowerRequest,
  ReeferPowerSession,
  ReeferTemperatureRecord,
  ReeferAlert,
  ReeferOverviewStats,
  SocketUsageStatus,
  SocketTechnicalStatus,
  PowerSessionStatus,
  ReeferRequestStatus,
  AlertSeverity,
  ReeferAlertStatus
} from '../domain/reefer.types';
import { AppError } from '../../../shared/errors/app-error';

export class ReeferService {
  private repository = new ReeferRepository();

  async getOverviewStats(): Promise<ReeferOverviewStats> {
    return this.repository.getOverviewStats();
  }

  async getEligibleContainers(): Promise<any[]> {
    return this.repository.getEligibleContainers();
  }

  async getPlugs(filters?: {
    usage_status?: SocketUsageStatus;
    technical_status?: SocketTechnicalStatus;
    search?: string;
  }): Promise<ReeferPlug[]> {
    return this.repository.getPlugs(filters);
  }

  async getPlugById(id: string): Promise<ReeferPlug> {
    const plug = await this.repository.getPlugById(id);
    if (!plug) throw new AppError('Không tìm thấy thông tin ổ cắm điện.', 404);
    return plug;
  }

  async updatePlugStatus(id: string, technical_status: SocketTechnicalStatus): Promise<ReeferPlug> {
    return this.repository.updatePlugTechnicalStatus(id, technical_status);
  }

  async getRequests(filters?: {
    status?: ReeferRequestStatus;
    container_id?: string;
    search?: string;
  }): Promise<ReeferPowerRequest[]> {
    return this.repository.getRequests(filters);
  }

  async getRequestById(id: string): Promise<ReeferPowerRequest> {
    const req = await this.repository.getRequestById(id);
    if (!req) throw new AppError('Không tìm thấy yêu cầu cấp điện.', 404);
    return req;
  }

  async createRequest(data: {
    container_id: string;
    target_temperature: number;
    temperature_unit?: 'C' | 'F';
    temp_tolerance?: number;
    cargo_description?: string;
    special_instructions?: string;
    requested_by?: string;
  }): Promise<ReeferPowerRequest> {
    return this.repository.createRequest(data);
  }

  async cancelRequest(id: string, reason: string): Promise<ReeferPowerRequest> {
    const req = await this.repository.getRequestById(id);
    if (!req) throw new AppError('Không tìm thấy yêu cầu cấp điện.', 404);
    if (['COMPLETED', 'CANCELLED'].includes(req.status)) {
      throw new AppError(`Không thể hủy yêu cầu đang ở trạng thái ${req.status}.`, 400);
    }
    return this.repository.updateRequestStatus(id, 'CANCELLED', reason);
  }

  async getSessions(filters?: {
    session_status?: PowerSessionStatus;
    container_id?: string;
    plug_id?: string;
    search?: string;
  }): Promise<ReeferPowerSession[]> {
    return this.repository.getSessions(filters);
  }

  async getSessionById(id: string): Promise<ReeferPowerSession> {
    const session = await this.repository.getSessionById(id);
    if (!session) throw new AppError('Không tìm thấy phiên cấp điện.', 404);
    return session;
  }

  async assignPlug(data: {
    container_id: string;
    plug_id: string;
    request_id?: string;
    target_temperature?: number;
    temp_tolerance?: number;
    cargo_description?: string;
    assigned_by: string;
    note?: string;
  }): Promise<ReeferPowerSession> {
    try {
      return await this.repository.assignPlugToContainer(data);
    } catch (err: any) {
      throw new AppError(err.message, 400);
    }
  }

  async confirmPower(sessionId: string, confirmedBy: string, initialReturnTemp?: number): Promise<ReeferPowerSession> {
    try {
      return await this.repository.confirmPowerOn(sessionId, confirmedBy, initialReturnTemp);
    } catch (err: any) {
      throw new AppError(err.message, 400);
    }
  }

  async terminateSession(sessionId: string, reason: string): Promise<ReeferPowerSession> {
    try {
      return await this.repository.terminateSession(sessionId, reason);
    } catch (err: any) {
      throw new AppError(err.message, 400);
    }
  }

  async cancelReservation(sessionId: string, reason: string): Promise<ReeferPowerSession> {
    try {
      return await this.repository.cancelReservation(sessionId, reason);
    } catch (err: any) {
      throw new AppError(err.message, 400);
    }
  }

  async transferPlug(sessionId: string, newPlugId: string, reason: string, userId: string): Promise<ReeferPowerSession> {
    try {
      return await this.repository.transferPlug(sessionId, newPlugId, reason, userId);
    } catch (err: any) {
      throw new AppError(err.message, 400);
    }
  }

  async recordTemperature(data: {
    session_id: string;
    supply_temperature?: number;
    return_temperature: number;
    temperature_unit?: 'C' | 'F';
    ambient_temperature?: number;
    data_source?: any;
    recorded_by?: string;
    device_id?: string;
    notes?: string;
  }): Promise<ReeferTemperatureRecord> {
    try {
      return await this.repository.recordTemperature(data);
    } catch (err: any) {
      throw new AppError(err.message, 400);
    }
  }

  async getTemperatureHistory(sessionId: string): Promise<ReeferTemperatureRecord[]> {
    return this.repository.getTemperatureHistory(sessionId);
  }

  async getAlerts(filters?: {
    status?: ReeferAlertStatus;
    severity?: AlertSeverity;
    session_id?: string;
  }): Promise<ReeferAlert[]> {
    return this.repository.getAlerts(filters);
  }

  async acknowledgeAlert(id: string, userId: string): Promise<ReeferAlert> {
    return this.repository.acknowledgeAlert(id, userId);
  }

  async resolveAlert(id: string, userId: string, notes: string): Promise<ReeferAlert> {
    return this.repository.resolveAlert(id, userId, notes);
  }
}
