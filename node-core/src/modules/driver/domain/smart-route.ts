import { z } from "zod";
import referenceLayout from "../../../shared/yard/reference-layout.json";

const identifier = z.string().trim().min(1).max(120);
// PostgreSQL jsonb_build_object(now()) emits an ISO timestamp with an offset,
// while external snapshots often use Z. Accept both and expose one UTC form.
const mapTimestamp = z
  .string()
  .datetime({ offset: true })
  .transform((value) => new Date(value).toISOString());
export const yardMapSchema = z
  .object({
    version: identifier,
    updatedAt: mapTimestamp.optional(),
    validUntil: mapTimestamp.optional(),
    nodes: z
      .array(
        z.object({
          id: identifier,
          label: z.string().trim().min(1).max(200),
          x: z.number().finite(),
          y: z.number().finite(),
          gateCode: identifier.optional(),
          blockCode: identifier.optional(),
          slotId: z.string().uuid().optional(),
          location: identifier.optional(),
          restricted: z.boolean().default(false),
        }),
      )
      .min(1)
      .max(2000),
    edges: z
      .array(
        z.object({
          id: identifier,
          from: identifier,
          to: identifier,
          distanceMeters: z.number().finite().positive().max(100000),
          travelSeconds: z.number().finite().positive().max(86400),
          // Optional relative length in drawing units, separate from measured time.
          routingCost: z.number().finite().positive().optional(),
          bidirectional: z.boolean().default(false),
          restricted: z.boolean().default(false),
          congestion: z.number().finite().min(0).max(1).default(0),
          instruction: z.string().trim().min(1).max(500).optional(),
        }),
      )
      .max(10000),
    layout: z
      .object({
        drawingVersion: identifier.optional(),
        blocks: z
          .array(
            z.object({
              code: identifier,
              label: z.string().min(1).max(200),
              x: z.number().finite(),
              y: z.number().finite(),
              width: z.number().positive(),
              height: z.number().positive(),
              bays: z.number().int().positive(),
              rows: z.number().int().positive(),
              tiers: z.number().int().positive(),
            }),
          )
          .max(100),
        slots: z
          .array(
            z.object({
              id: z.string().uuid(),
              block: identifier,
              bay: z.number().int().positive(),
              row: z.number().int().positive(),
              tier: z.number().int().positive(),
              status: identifier,
              containerId: z.string().uuid().nullable().optional(),
            }),
          )
          .max(20000),
      })
      .optional(),
  })
  .superRefine((map, context) => {
    const nodeIds = new Set(map.nodes.map((node) => node.id));
    const edgeIds = new Set(map.edges.map((edge) => edge.id));
    if (
      nodeIds.size !== map.nodes.length ||
      edgeIds.size !== map.edges.length
    ) {
      context.addIssue({
        code: z.ZodIssueCode.custom,
        message: "Map identifiers must be unique.",
      });
    }
    if (
      map.edges.some((edge) => !nodeIds.has(edge.from) || !nodeIds.has(edge.to))
    ) {
      context.addIssue({
        code: z.ZodIssueCode.custom,
        message: "Map edges must reference existing nodes.",
      });
    }
  });

export type YardMap = z.infer<typeof yardMapSchema> & {
  referenceLayout?: typeof referenceLayout;
  terminalPlan?: { version: string };
};
export type RouteNode = YardMap["nodes"][number];
export type RouteEdge = YardMap["edges"][number];

export interface RouteTarget {
  type: "slot" | "area";
  location: string;
  slotId: string | null;
  block: string | null;
  zone: string | null;
  bay: number | null;
  row: number | null;
  tier: number | null;
  source:
    | "operation"
    | "reservation"
    | "container_position"
    | "gate"
    | "demo_admission";
}

export interface SmartRoute {
  map: YardMap | null;
  status: "available" | "unavailable" | "pending_destination";
  reasonCode: string | null;
  message: string;
  revision: string;
  generatedAt: string;
  refreshAfterSeconds: number;
  mapVersion: string | null;
  origin: RouteNode | null;
  destinationNode: RouteNode | null;
  waypoints: RouteNode[];
  segments: Array<RouteEdge & { from: string; to: string }>;
  directions: string[];
  distanceMeters: number | null;
  estimatedSeconds: number | null;
  rerouted: boolean;
}

/** Directed Dijkstra: restrictions are impassable; congestion adds travel time. */
export function shortestRoute(
  map: YardMap,
  start: string,
  finish: string,
  ignoreConditions = false,
) {
  const nodes = new Map(map.nodes.map((node) => [node.id, node]));
  if (!nodes.has(start) || !nodes.has(finish)) return null;
  if (
    !ignoreConditions &&
    (nodes.get(start)!.restricted || nodes.get(finish)!.restricted)
  )
    return null;
  const adjacency = new Map<string, RouteEdge[]>();
  for (const edge of map.edges) {
    if (
      !ignoreConditions &&
      (edge.restricted ||
        nodes.get(edge.from)?.restricted ||
        nodes.get(edge.to)?.restricted)
    )
      continue;
    adjacency.set(edge.from, [...(adjacency.get(edge.from) ?? []), edge]);
    if (edge.bidirectional) {
      adjacency.set(edge.to, [
        ...(adjacency.get(edge.to) ?? []),
        { ...edge, from: edge.to, to: edge.from, instruction: undefined },
      ]);
    }
  }
  const costs = new Map<string, number>([[start, 0]]);
  const previous = new Map<string, RouteEdge>();
  const pending = new Set([start]);
  const visited = new Set<string>();
  while (pending.size) {
    let current = [...pending][0];
    for (const id of pending)
      if (costs.get(id)! < costs.get(current)!) current = id;
    pending.delete(current);
    if (current === finish) break;
    visited.add(current);
    for (const edge of adjacency.get(current) ?? []) {
      if (visited.has(edge.to)) continue;
      const cost =
        costs.get(current)! +
        (edge.routingCost ?? edge.travelSeconds) *
          (ignoreConditions ? 1 : 1 + 4 * edge.congestion);
      if (cost < (costs.get(edge.to) ?? Infinity)) {
        costs.set(edge.to, cost);
        previous.set(edge.to, edge);
        pending.add(edge.to);
      }
    }
  }
  if (!costs.has(finish)) return null;
  const segments: RouteEdge[] = [];
  let cursor = finish;
  while (cursor !== start) {
    const edge = previous.get(cursor)!;
    segments.unshift(edge);
    cursor = edge.from;
  }
  return {
    segments,
    waypoints: [
      nodes.get(start)!,
      ...segments.map((edge) => nodes.get(edge.to)!),
    ],
    distanceMeters: segments.reduce(
      (sum, edge) => sum + edge.distanceMeters,
      0,
    ),
    estimatedSeconds: Math.ceil(
      segments.reduce(
        (sum, edge) =>
          sum +
          edge.travelSeconds * (ignoreConditions ? 1 : 1 + 4 * edge.congestion),
        0,
      ),
    ),
  };
}

// Sơ đồ bãi chỉ để mô phỏng chỉ đường: mỗi đơn vị toạ độ trên sơ đồ tương ứng khoảng 0,15 m, xe chạy khoảng 10 km/h.
export const SIM_METERS_PER_UNIT = 0.15;
export const SIM_SPEED_MPS = 2.8;

/**
 * Chỉ dẫn từng chặng theo toạ độ sơ đồ: gộp các đoạn thẳng liên tiếp, nói rõ rẽ trái / rẽ phải / quay đầu và khoảng cách mô phỏng.
 * Toạ độ SVG có trục y hướng xuống nên tích có hướng dương nghĩa là rẽ phải.
 */
export function turnByTurn(
  waypoints: RouteNode[],
  segments: Array<{ instruction?: string }>,
): { steps: string[]; meters: number } {
  type Run = { dx: number; dy: number; length: number; end: RouteNode };
  const runs: Run[] = [];
  let approach: string | undefined;
  for (let i = 0; i < segments.length; i++) {
    const a = waypoints[i], b = waypoints[i + 1];
    const dx = b.x - a.x, dy = b.y - a.y, length = Math.hypot(dx, dy);
    if (length < 0.5) { approach = segments[i].instruction ?? approach; continue; }
    const last = runs[runs.length - 1];
    const same = last && Math.abs(Math.atan2(last.dx * dy - last.dy * dx, last.dx * dx + last.dy * dy)) < (20 * Math.PI) / 180;
    if (same) { last.length += length; last.end = b; }
    else runs.push({ dx, dy, length, end: b });
  }
  const steps: string[] = [];
  let meters = 0;
  runs.forEach((run, i) => {
    const m = Math.max(1, Math.round(run.length * SIM_METERS_PER_UNIT));
    meters += m;
    let lead = 'Đi thẳng';
    if (i > 0) {
      const prev = runs[i - 1];
      const deg = (Math.atan2(prev.dx * run.dy - prev.dy * run.dx, prev.dx * run.dx + prev.dy * run.dy) * 180) / Math.PI;
      lead = Math.abs(deg) >= 150 ? 'Quay đầu' : deg > 0 ? 'Rẽ phải' : 'Rẽ trái';
    }
    steps.push(`${lead}, đi khoảng ${m} m đến ${run.end.label}.`);
  });
  if (approach) steps.push(approach);
  return { steps, meters };
}
