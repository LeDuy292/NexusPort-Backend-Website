import plan from "./tien-sa-plan.json";
import {
  RouteNode,
  RouteTarget,
  YardMap,
  yardMapSchema,
} from "../../modules/driver/domain/smart-route";

export function buildTienSaMap(): YardMap {
  const map = yardMapSchema.parse({
    version: plan.version,
    nodes: plan.nodes,
    edges: plan.edges,
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
