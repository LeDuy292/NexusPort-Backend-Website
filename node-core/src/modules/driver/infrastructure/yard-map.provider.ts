import { readFile } from 'fs/promises';
import { query } from '../../../infrastructure/database/db';
import { YardMap, yardMapSchema } from '../domain/smart-route';
import referenceLayout from '../../../shared/yard/reference-layout.json';

export interface YardMapSnapshot {
  map: YardMap | null;
  errorCode?: 'YARD_MAP_NOT_CONFIGURED' | 'YARD_MAP_INVALID' | 'YARD_MAP_EXPIRED';
}

/** Replace this boundary with Smart Yard/IoT snapshots without changing Driver APIs. */
export interface YardMapProvider {
  load(instructionMapData: unknown): Promise<YardMapSnapshot>;
}

type DatabaseReader = (sql: string, values?: unknown[]) => Promise<{ rows: Record<string, unknown>[] }>;

const instructionSnapshot = (data: unknown): unknown =>
  data && typeof data === 'object' && 'smartYardMap' in data
    ? (data as { smartYardMap: unknown }).smartYardMap : undefined;

export class ConfiguredYardMapProvider implements YardMapProvider {
  constructor(private readonly read: DatabaseReader = query) {}

  async load(instructionMapData: unknown): Promise<YardMapSnapshot> {
    let input: unknown;
    let mapId: string | undefined;
    try {
      // A configured live snapshot takes precedence over an older instruction snapshot.
      // Re-read on every refresh so restrictions/congestion never get stuck in a process cache.
      const mapPath = process.env.SMART_ROUTE_MAP_PATH;
      if (mapPath) input = JSON.parse(await readFile(mapPath, 'utf8'));
      else {
        const active = await this.read('SELECT id,map_data FROM smart_yard_maps WHERE is_active=true ORDER BY updated_at DESC LIMIT 1');
        if (active.rows[0]) {
          mapId = String(active.rows[0].id);
          input = active.rows[0].map_data;
        } else input = instructionSnapshot(instructionMapData);
      }
      if (input === undefined) return { map: null, errorCode: 'YARD_MAP_NOT_CONFIGURED' };
      const result = yardMapSchema.safeParse(input);
      if (!result.success) return { map: null, errorCode: 'YARD_MAP_INVALID' };
      if (result.data.validUntil && Date.parse(result.data.validUntil) <= Date.now()) {
        return { map: null, errorCode: 'YARD_MAP_EXPIRED' };
      }
      const map = result.data;
      if (mapId) {
        // Keep operational occupancy live; map snapshots never invent current container positions.
        const [conditions, slots] = await Promise.all([
          this.read(`SELECT target_type,target_id,restricted,congestion,observed_at FROM smart_yard_conditions
            WHERE map_id=$1 AND (valid_until IS NULL OR valid_until>now())`, [mapId]),
          this.read(`SELECT ys.id,yb.code AS block,ys.bay,ys.row_no AS row,ys.tier,ys.status::text AS status,
            (SELECT cp.container_id FROM container_positions cp WHERE cp.slot_id=ys.id AND cp.is_current=true
              ORDER BY cp.placed_at DESC LIMIT 1) AS "containerId"
            FROM yard_slots ys JOIN yard_blocks yb ON yb.id=ys.block_id ORDER BY yb.code,ys.bay,ys.row_no,ys.tier`),
        ]);
        for (const condition of conditions.rows) {
          const target = condition.target_type === 'node'
            ? map.nodes.find(node => node.id === condition.target_id)
            : map.edges.find(edge => edge.id === condition.target_id);
          if (!target) continue;
          target.restricted = target.restricted || condition.restricted === true;
          if ('congestion' in target) target.congestion = Math.max(target.congestion, Number(condition.congestion));
          const observedAt = new Date(String(condition.observed_at)).toISOString();
          if (!map.updatedAt || Date.parse(observedAt)>Date.parse(map.updatedAt)) map.updatedAt=observedAt;
        }
        if (map.layout) map.layout.slots = slots.rows.map(slot => ({
          id: String(slot.id), block: String(slot.block), bay: Number(slot.bay), row: Number(slot.row),
          tier: Number(slot.tier), status: String(slot.status),
          containerId: slot.containerId == null ? null : String(slot.containerId),
        }));
      }
      // Reference geometry is explicitly proposed. Never project an approved
      // route onto this drawing or substitute it for the active road graph.
      return { map: { ...map, referenceLayout } };
    } catch {
      return { map: null, errorCode: 'YARD_MAP_INVALID' };
    }
  }
}
