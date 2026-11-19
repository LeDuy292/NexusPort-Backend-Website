import plan from "./tien-sa-plan.json";
import {
  RouteNode,
  RouteTarget,
  YardMap,
  yardMapSchema,
} from "../../modules/driver/domain/smart-route";

const handoverStops = [
  ["A-S1", "tsa:772,1181.25"],
  ["A-S2", "tsa:772,1151.25"],
] as const;

export function buildTienSaMap(): YardMap {
  const handovers = handoverStops.map(([location, roadId]) => {
    const road = plan.nodes.find(node => node.id === roadId)!;
    const id = `tsa:handover:${location}`;
    return {
      node: { id, location, label: `Điểm giao nhận ${location}`, x: road.x, y: road.y },
      edge: {
        id: `access:${id}`, from: roadId, to: id, bidirectional: true, distanceMeters: 1, travelSeconds: 1,
        instruction: `Tiếp cận điểm giao nhận ${location} trên làn giao nhận.`,
      },
    };
  });
  const map = yardMapSchema.parse({
    version: plan.version,
    nodes: [...plan.nodes, ...handovers.map(handover => handover.node)],
    edges: [...plan.edges, ...handovers.map(handover => handover.edge)],
    layout: {
      drawingVersion: plan.version,
      blocks: plan.areas.map((area) => ({
        code: area.code,
        label: area.code,
        x: area.x,
        y: area.y,
        width: area.width,
        height: area.height,
        bays: area.planning.bayModules * 2 - 1,
        rows: area.planning.rows,
        tiers: area.planning.tiers,
      })),
      slots: [], // Inventory and reservations remain in their existing DB tables.
    },
  });
  return { ...map, terminalPlan: { version: plan.version } };
}

export function tienSaDestination(
  target: RouteTarget,
  map: YardMap,
): RouteNode | null {
  const block =
    target.block || (target.type === "area" ? target.location : null);
  const area = plan.areas.find((area) => area.code === block);
  if (!area) return null;
  const bay = target.bay ?? 1;
  if (
    !Number.isInteger(bay) ||
    bay < 1 ||
    bay >= area.planning.bayModules * 2 ||
    (target.row != null &&
      (!Number.isInteger(target.row) ||
        target.row < 1 ||
        target.row > area.planning.rows)) ||
    (target.tier != null &&
      (!Number.isInteger(target.tier) ||
        target.tier < 1 ||
        target.tier > area.planning.tiers))
  )
    return null;
  const nodeId = (plan.stops as Record<string, string>)[`${block}:${bay}`];
  return map.nodes.find((node) => node.id === nodeId) ?? null;
}

export function isTienSaBlock(block: string | null | undefined): boolean {
  return plan.areas.some((area) => area.code === block);
}

export function returnsToMainRoad(block: string | null | undefined): boolean {
  return (
    plan.areas.find((area) => area.code === block)?.accessPolicy === "return"
  );
}
