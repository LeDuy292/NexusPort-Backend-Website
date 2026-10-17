import { AppError } from '../../../shared/errors/app-error';
import { DriverRouteRepository, DriverRouteRow, routeDestinationKey } from '../infrastructure/driver-route.repository';
import { ValidationError, ConflictError } from '../../../shared/errors/app-error';
import { z } from 'zod';
import { RouteTarget, SmartRoute } from '../domain/smart-route';
import { SmartRouteService } from './smart-route.service';

export interface DriverRoute {
  driverId?: string; driverName?: string; truckPlate?: string; bookingCode?: string; bookingType?: string;
  journey?: {
    phase: 'to_yard' | 'at_yard' | 'ready_for_gate_out' | 'exited';
    gateInAt: string | null; arrivedAt: string | null; containerConfirmedAt: string | null;
    gateOutAt: string | null; currentNodeId: string | null; canArrive: boolean;
    canConfirmContainer: boolean; exitGateCode: string | null;
  };
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
    driverId: row.driverId, driverName: row.driverName, truckPlate: row.truckPlate,
    bookingCode: row.bookingCode, bookingType: row.bookingType,
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
    const arrived = row.destinationKey === routeDestinationKey(row) ? row.arrivedAt ?? null : null;
    const readyForExit = !!row.containerConfirmedAt && row.bookingStatus === 'completed';
    const phase = row.gateOutAt ? 'exited' : readyForExit ? 'ready_for_gate_out' : arrived ? 'at_yard' : 'to_yard';
    const position = currentNodeId ?? row.currentNodeId ?? undefined;
    if (readyForExit || row.gateOutAt) {
      const pickupRoute = !position && result.target ? await this.navigation.create(row, result.target) : null;
      const exitTarget: RouteTarget | null = row.exitGateCode ? {
        type: 'area', location: row.exitGateCode, slotId: null, block: null, zone: null,
        bay: null, row: null, tier: null, source: 'gate',
      } : null;
      result.navigation = await this.navigation.create(row, exitTarget, position ?? pickupRoute?.destinationNode?.id);
      if (row.gateOutAt && result.navigation.destinationNode) {
        result.navigation = await this.navigation.create(row, exitTarget, result.navigation.destinationNode.id);
        result.navigation.message = 'Xe đã được cổng xác nhận ra khỏi bãi.';
      }
      result.target = exitTarget;
    } else result.navigation = await this.navigation.create(row, result.target, position);
    result.journey = {
      phase, gateInAt: row.gateInAt ?? null, arrivedAt: arrived,
      containerConfirmedAt: row.containerConfirmedAt ?? null, gateOutAt: row.gateOutAt ?? null,
      currentNodeId: result.navigation.origin?.id ?? null,
      canArrive: phase === 'to_yard' && result.navigation.status === 'available',
      canConfirmContainer: phase === 'at_yard' && !!row.yardCompletedAt && !row.containerConfirmedAt,
      exitGateCode: row.exitGateCode ?? null,
    };
    return result;
  }

  async list(driverId: string): Promise<DriverRoute[]> {
    return Promise.all((await this.repository.findForDriver(driverId)).map(row => this.enrich(row)));
  }

  async get(driverId: string, containerId: string, currentNodeId?: string, bookingId?: string): Promise<DriverRoute> {
    const row = bookingId ? await this.repository.findForDriverAndContainer(driverId, containerId, bookingId)
      : await this.repository.findForDriverAndContainer(driverId, containerId);
    if (!row) throw new AppError('Không tìm thấy Container đã qua Gate-In của Driver này.', 404, 'DRIVER_ROUTE_NOT_FOUND');
    const result = await this.enrich(row, currentNodeId);
    return result;
  }

  async monitor(): Promise<{ routes: DriverRoute[]; generatedAt: string }> {
    return { routes: await Promise.all((await this.repository.findForDispatcher()).map(row => this.enrich(row))), generatedAt: new Date().toISOString() };
  }

  async arrive(driverId: string, containerId: string, input: unknown): Promise<DriverRoute> {
    const parsed = z.object({ bookingId: z.string().uuid(), action: z.literal('arrive'), currentNodeId: z.string().min(1).max(120).optional() }).strict().safeParse(input);
    if (!parsed.success) throw new ValidationError('Thông tin xác nhận đến điểm giao nhận không hợp lệ.');
    const { bookingId, currentNodeId } = parsed.data;
    const row = await this.repository.findForDriverAndContainer(driverId, containerId, bookingId);
    if (!row) throw new AppError('Không tìm thấy lượt xe của Driver.', 404, 'DRIVER_ROUTE_NOT_FOUND');
    const route = await this.enrich(row);
    if (route.journey?.phase === 'at_yard') return route;
    if (!route.journey?.canArrive || !route.navigation?.destinationNode) throw new ConflictError('Chưa có tuyến khả dụng để xác nhận đến điểm giao nhận.');
    if (currentNodeId && currentNodeId !== route.navigation.destinationNode.id) throw new ValidationError('Chỉ xác nhận tại điểm giao nhận được phân công.');
    await this.repository.arrive(driverId, containerId, bookingId, route.navigation.destinationNode.id, routeDestinationKey(row));
    return this.get(driverId, containerId, undefined, bookingId);
  }

  async confirm(driverId: string, containerId: string, input: unknown): Promise<DriverRoute> {
    const parsed = z.object({ bookingId: z.string().uuid(), condition: z.enum(['OK', 'DAMAGED', 'ISSUE']).default('OK'), notes: z.string().trim().max(1000).optional() }).strict().safeParse(input);
    if (!parsed.success) throw new ValidationError('Thông tin xác nhận container không hợp lệ.');
    await this.repository.confirm(driverId, containerId, parsed.data.bookingId, parsed.data.condition, parsed.data.notes);
    return this.get(driverId, containerId, undefined, parsed.data.bookingId);
  }
}
