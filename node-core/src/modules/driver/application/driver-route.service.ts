import { AppError } from '../../../shared/errors/app-error';
import { DriverRouteRepository, DriverRouteRow } from '../infrastructure/driver-route.repository';

export interface DriverRoute {
  containerId: string;
  containerNumber: string;
  bookingId: string;
  gateTransactionId: string;
  gateStatus: string;
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
  const fromGate = row.fromGate || row.gateCode;
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
  constructor(private readonly repository = new DriverRouteRepository()) {}

  async list(driverId: string): Promise<DriverRoute[]> {
    return (await this.repository.findForDriver(driverId)).map(toDriverRoute);
  }

  async get(driverId: string, containerId: string): Promise<DriverRoute> {
    const row = await this.repository.findForDriverAndContainer(driverId, containerId);
    if (!row) throw new AppError('Không tìm thấy Container đã qua Gate-In của Driver này.', 404, 'DRIVER_ROUTE_NOT_FOUND');
    const result = toDriverRoute(row);
    if (!result.destination) throw new AppError('Container chưa được gán vị trí Yard.', 409, 'YARD_LOCATION_NOT_ASSIGNED');
    return result;
  }
}
