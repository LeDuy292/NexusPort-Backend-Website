import { readFile } from 'fs/promises';
import { YardMap, yardMapSchema } from '../domain/smart-route';

export interface YardMapSnapshot {
  map: YardMap | null;
  errorCode?: 'YARD_MAP_NOT_CONFIGURED' | 'YARD_MAP_INVALID' | 'YARD_MAP_EXPIRED';
}

/** Replace this boundary with Smart Yard/IoT snapshots without changing Driver APIs. */
export interface YardMapProvider {
  load(instructionMapData: unknown): Promise<YardMapSnapshot>;
}

export class ConfiguredYardMapProvider implements YardMapProvider {
  async load(instructionMapData: unknown): Promise<YardMapSnapshot> {
    let input: unknown;
    try {
      // A configured live snapshot takes precedence over an older instruction snapshot.
      // Re-read on every refresh so restrictions/congestion never get stuck in a process cache.
      const mapPath = process.env.SMART_ROUTE_MAP_PATH;
      if (mapPath) input = JSON.parse(await readFile(mapPath, 'utf8'));
      else if (instructionMapData && typeof instructionMapData === 'object' && 'smartYardMap' in instructionMapData) {
        input = (instructionMapData as { smartYardMap: unknown }).smartYardMap;
      } else return { map: null, errorCode: 'YARD_MAP_NOT_CONFIGURED' };
      const result = yardMapSchema.safeParse(input);
      if (!result.success) return { map: null, errorCode: 'YARD_MAP_INVALID' };
      if (result.data.validUntil && Date.parse(result.data.validUntil) <= Date.now()) {
        return { map: null, errorCode: 'YARD_MAP_EXPIRED' };
      }
      return { map: result.data };
    } catch {
      return { map: null, errorCode: 'YARD_MAP_INVALID' };
    }
  }
}
