import { AppError } from '../../../shared/errors/app-error';
import { DriverRouteRepository, DriverRouteRow } from '../infrastructure/driver-route.repository';
import { RouteTarget, SmartRoute } from '../domain/smart-route';
import { SmartRouteService } from './smart-route.service';

export interface DriverRoute {
  containerId: string;
  containerNumber: string;
  bookingId: string;
  gateTransactionId: string;
  gateStatus: string;
  gateCode: string;
  operation?: { id: string; taskCode: string | null; type: string | null; status: string | null } | null;
  target?: RouteTarget | null;
  navigation?: SmartRoute;
  destination: { slotId: string; zone: string; block: string; bay: number; row: number; tier: number } | null;
  route: {
    instructionId: string | null;
    fromGate: string;
    directions: string[];
    detail: string | null;
    mapData: unknown | null;
  } | null;
}

export const toDriverRoute = (row: DriverRouteRow): DriverRoute => {
  const destination = row.slotId && row.blockCode && row.bay != null && row.row != null && row.tier != null
    ? {
        slotId: row.slotId,
        zone: row.zone ?? '',
        block: row.blockCode,
        bay: Number(row.bay),
        row: Number(row.row),
        tier: Number(row.tier),
      }
    : null;
  const fromGate = row.gateCode;
  const directions = destination ? [
    `Xuất phát từ cổng ${fromGate}.`,
    ...(destination.zone ? [`Đi đến khu ${destination.zone}.`] : []),
    `Đi đến Block ${destination.block}.`,
    `Tiếp tục đến Bay ${destination.bay}, Row ${destination.row}.`,
    `Dừng tại vị trí Tier ${destination.tier} theo hướng dẫn của nhân viên Yard.`,
  ] : [];

  return {
    containerId: row.containerId,
    containerNumber: row.containerNumber,
    bookingId: row.bookingId,
    gateTransactionId: row.gateTransactionId,
    gateStatus: row.gateStatus,
    gateCode: row.gateCode,
    destination,
    route: destination ? {
      instructionId: row.instructionId,
      fromGate,
      directions,
      detail: row.routeDetail,
      mapData: row.mapData,
    } : null,
  };
};

export class DriverRouteService {
  constructor(
    private readonly repository = new DriverRouteRepository(),
    private readonly navigation = new SmartRouteService(),
  ) {}

  private async enrich(row: DriverRouteRow, currentNodeId?: string): Promise<DriverRoute> {
    const result = toDriverRoute(row);
    const slot = result.destination;
    result.operation = row.operationId ? {
      id: row.operationId, taskCode: row.taskCode ?? null,
      type: row.operationType ?? null, status: row.operationStatus ?? null,
    } : null;
    result.target = slot ? {
      type: 'slot', location: row.targetLocation || `${slot.block}-${slot.bay}-${slot.row}-${slot.tier}`,
      ...slot, source: row.destinationSource ?? 'reservation',
    } : row.targetLocation?.trim() && row.operationId ? {
      type: 'area', location: row.targetLocation.trim(), slotId: null,
      block: null, zone: null, bay: null, row: null, tier: null, source: 'operation',
    } : null;
    result.navigation = await this.navigation.create(row, result.target, currentNodeId);
    return result;
  }

  async list(driverId: string): Promise<DriverRoute[]> {
    return Promise.all((await this.repository.findForDriver(driverId)).map(row => this.enrich(row)));
  }

  async get(driverId: string, containerId: string, currentNodeId?: string): Promise<DriverRoute> {
    const row = await this.repository.findForDriverAndContainer(driverId, containerId);
    if (!row) throw new AppError('Không tìm thấy Container đã qua Gate-In của Driver này.', 404, 'DRIVER_ROUTE_NOT_FOUND');
    const result = await this.enrich(row, currentNodeId);
    if (!result.target) throw new AppError('Container chưa được gán vị trí Yard.', 409, 'YARD_LOCATION_NOT_ASSIGNED');
    return result;
  }
}
