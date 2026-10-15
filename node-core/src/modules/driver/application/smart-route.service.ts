import { createHash } from 'crypto';
import { RouteNode, RouteTarget, SmartRoute, shortestRoute } from '../domain/smart-route';
import { DriverRouteRow } from '../infrastructure/driver-route.repository';
import { ConfiguredYardMapProvider, YardMapProvider } from '../infrastructure/yard-map.provider';

const reasons: Record<string, string> = {
  YARD_LOCATION_NOT_ASSIGNED: 'Chưa có điểm đến được phân công. Vui lòng chờ điều phối.',
  YARD_LOCATION_INVALID: 'Vị trí trong Operation không khớp với Slot hiện có trong bãi. Vui lòng liên hệ điều phối.',
  YARD_MAP_NOT_CONFIGURED: 'Chưa có bản đồ đường nội bộ được xác nhận. Vui lòng liên hệ điều phối để được hướng dẫn.',
  YARD_MAP_INVALID: 'Bản đồ đường nội bộ không hợp lệ hoặc tạm thời không đọc được. Vui lòng liên hệ điều phối.',
  YARD_MAP_EXPIRED: 'Dữ liệu điều kiện di chuyển đã hết hạn. Vui lòng chờ bản đồ được cập nhật.',
  ORIGIN_NOT_MAPPED: 'Chưa xác định được cổng Gate In trên bản đồ bãi.',
  CURRENT_POSITION_NOT_MAPPED: 'Vị trí hiện tại không còn trên bản đồ. Vui lòng xác nhận lại vị trí.',
  DESTINATION_NOT_MAPPED: 'Điểm đến được phân công chưa được ánh xạ trên bản đồ bãi.',
  DESTINATION_RESTRICTED: 'Vị trí được phân công đang bảo trì hoặc bị hạn chế. Vui lòng liên hệ điều phối.',
  NO_SAFE_ROUTE: 'Không tìm được tuyến khả dụng đến điểm đích. Vui lòng dừng tại nơi an toàn và liên hệ điều phối.',
};

export class SmartRouteService {
  constructor(private readonly maps: YardMapProvider = new ConfiguredYardMapProvider()) {}

  async create(row: DriverRouteRow, target: RouteTarget | null, currentNodeId?: string): Promise<SmartRoute> {
    const result: SmartRoute = {
      status: target ? 'unavailable' : 'pending_destination', reasonCode: null, message: '',
      revision: '', generatedAt: new Date().toISOString(), refreshAfterSeconds: 15,
      mapVersion: null, origin: null, destinationNode: null, waypoints: [], segments: [], directions: [],
      distanceMeters: null, estimatedSeconds: null, rerouted: false,
    };
    const finish = (reasonCode?: string): SmartRoute => {
      if (reasonCode) { result.reasonCode = reasonCode; result.message = reasons[reasonCode]; }
      result.revision = createHash('sha256').update(JSON.stringify({
        gateTransactionId: row.gateTransactionId, operationId: row.operationId, target,
        ...result, generatedAt: undefined, revision: undefined,
      })).digest('hex').slice(0, 24);
      return result;
    };
    if (!target) return finish('YARD_LOCATION_NOT_ASSIGNED');
    if (target.type === 'area' && /^(.*)-([0-9]+)-([0-9]+)-([0-9]+)$/.test(target.location)) return finish('YARD_LOCATION_INVALID');
    if (row.slotStatus === 'maintenance') return finish('DESTINATION_RESTRICTED');
    const snapshot = await this.maps.load(row.mapData);
    if (!snapshot.map) return finish(snapshot.errorCode ?? 'YARD_MAP_NOT_CONFIGURED');
    const map = snapshot.map;
    result.mapVersion = map.version;
    const unique = (nodes: RouteNode[]) => nodes.length === 1 ? nodes[0] : null;
    result.origin = currentNodeId
      ? map.nodes.find(node => node.id === currentNodeId) ?? null
      : unique(map.nodes.filter(node => node.gateCode === row.gateCode));
    if (!result.origin) return finish(currentNodeId ? 'CURRENT_POSITION_NOT_MAPPED' : 'ORIGIN_NOT_MAPPED');
    result.destinationNode = (target.slotId ? unique(map.nodes.filter(node => node.slotId === target.slotId)) : null)
      ?? unique(map.nodes.filter(node => node.location === target.location))
      ?? (target.type === 'area' ? unique(map.nodes.filter(node => node.blockCode === target.location && !node.slotId && !node.location)) : null)
      ?? (target.type === 'slot' ? unique(map.nodes.filter(node => node.blockCode === target.block && !node.slotId && !node.location)) : null);
    if (!result.destinationNode) return finish('DESTINATION_NOT_MAPPED');
    if (result.destinationNode.restricted) return finish('DESTINATION_RESTRICTED');
    const path = shortestRoute(map, result.origin.id, result.destinationNode.id);
    if (!path) return finish('NO_SAFE_ROUTE');
    const normal = shortestRoute(map, result.origin.id, result.destinationNode.id, true);
    const signature = (edges: typeof path.segments) => edges.map(edge => `${edge.id}:${edge.from}:${edge.to}`).join('|');
    result.status = 'available';
    result.rerouted = normal !== null && signature(normal.segments) !== signature(path.segments);
    result.message = result.rerouted ? 'Đã chọn tuyến thay thế để tránh đường bị hạn chế hoặc ùn tắc.' : 'Đã tìm được tuyến đến điểm được phân công.';
    Object.assign(result, path);
    result.directions = [
      `Xuất phát từ ${result.origin.label}.`,
      ...path.segments.map((edge, index) => edge.instruction ?? `Đi ${edge.distanceMeters} m đến ${path.waypoints[index + 1].label}.`),
      target.type === 'slot'
        ? `Đến Block ${target.block}, Bay ${target.bay}, Row ${target.row}. Dừng theo hướng dẫn Yard để xử lý container tại Tier ${target.tier}.`
        : `Dừng tại khu vực được phân công: ${target.location}.`,
    ];
    return finish();
  }
}
