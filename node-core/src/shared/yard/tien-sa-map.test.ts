import { buildTienSaMap, tienSaDestination } from "./tien-sa-map";
import {
  shortestRoute,
  RouteTarget,
} from "../../modules/driver/domain/smart-route";
import { SmartRouteService } from "../../modules/driver/application/smart-route.service";
import { DriverRouteRow } from "../../modules/driver/infrastructure/driver-route.repository";
import plan from "./tien-sa-plan.json";
const row = {
  gateCode: "GATE-01",
  gateTransactionId: "visit",
  containerId: "container",
  bookingId: "booking",
  blockCode: "C03",
} as DriverRouteRow;
const target: RouteTarget = {
  type: "slot",
  location: "C03-21-4-3",
  slotId: null,
  block: "C03",
  zone: "C",
  bay: 21,
  row: 4,
  tier: 3,
  source: "operation",
};
test("all sectors have inbound and gate-out paths", () => {
  const map = buildTienSaMap();
  for (const block of plan.areas) {
    const stop = tienSaDestination(
      { ...target, block: block.code, bay: 1 },
      map,
    )!;
    expect(stop).toBeTruthy();
    expect(shortestRoute(map, "gate:GATE-01", stop.id)).not.toBeNull();
    expect(shortestRoute(map, stop.id, "gate:GATE-02")).not.toBeNull();
  }
  expect(tienSaDestination({ ...target, bay: 99 }, map)).toBeNull();
  expect(tienSaDestination({ ...target, row: 7 }, map)).toBeNull();
  expect(tienSaDestination({ ...target, row: 1.5 }, map)).toBeNull();
  expect(tienSaDestination({ ...target, tier: NaN }, map)).toBeNull();
});
test("simple route preserves assignment, follows C to rear and never invents metres or ETA", async () => {
  const service = new SmartRouteService({
    load: async () => ({ map: null, errorCode: "YARD_MAP_NOT_CONFIGURED" }),
  });
  const inbound = await service.create(row, target);
  expect(inbound.status).toBe("available");
  expect(inbound.distanceMeters).toBeNull();
  expect(inbound.estimatedSeconds).toBeNull();
  const outbound = await service.create(
    row,
    {
      ...target,
      type: "area",
      location: "GATE-02",
      source: "gate",
      slotId: null,
      block: null,
      bay: null,
      row: null,
      tier: null,
    },
    inbound.destinationNode!.id,
  );
  expect(outbound.status).toBe("available");
  expect(outbound.waypoints.some((p) => p.y === 1518)).toBe(true);
  expect(outbound.destinationNode!.gateCode).toBe("GATE-02");
});
test("unresolved restrictions and maintenance do not receive a fabricated route", async () => {
  const map = buildTienSaMap();
  map.version = "older-map";
  map.edges[0].restricted = true;
  const service = new SmartRouteService({ load: async () => ({ map }) });
  expect((await service.create(row, target)).status).toBe("unavailable");
  const clear = new SmartRouteService({ load: async () => ({ map: null }) });
  expect(
    (await clear.create({ ...row, slotStatus: "maintenance" }, target))
      .reasonCode,
  ).toBe("DESTINATION_RESTRICTED");
  expect((await clear.create(row, null)).status).toBe("pending_destination");
});

test("area-only assignments retain the new map on the outbound journey", async () => {
  const service = new SmartRouteService({
    load: async () => ({ map: null, errorCode: "YARD_MAP_NOT_CONFIGURED" }),
  });
  const areaRow = { ...row, blockCode: null, targetLocation: "C03" };
  const inbound = await service.create(areaRow, {
    ...target,
    type: "area",
    block: null,
    bay: null,
    row: null,
    tier: null,
    location: "C03",
  });
  expect(inbound.status).toBe("available");
  const outbound = await service.create(
    areaRow,
    {
      ...target,
      type: "area",
      block: null,
      bay: null,
      row: null,
      tier: null,
      location: "GATE-02",
      source: "gate",
    },
    inbound.destinationNode!.id,
  );
  expect(outbound.status).toBe("available");
  expect(outbound.mapVersion).toBe(plan.version);
});

test("a stale map requires dispatcher guidance", async () => {
  const stale = new SmartRouteService({
    load: async () => ({ map: null, errorCode: "YARD_MAP_EXPIRED" }),
  });
  expect((await stale.create(row, target)).reasonCode).toBe("YARD_MAP_EXPIRED");
});

test("E/F return along the same access aisle rather than an invented mountain-side road", async () => {
  const service = new SmartRouteService({ load: async () => ({ map: null }) });
  for (const area of plan.areas.filter((a) => ["E", "F"].includes(a.sector))) {
    const assignedRow = { ...row, blockCode: area.code };
    const inbound = await service.create(assignedRow, {
      ...target,
      block: area.code,
      bay: 1,
    });
    expect(inbound.status).toBe("available");
    expect(inbound.message).toContain("quay đầu");
    const outbound = await service.create(
      assignedRow,
      {
        ...target,
        type: "area",
        location: "GATE-02",
        source: "gate",
        block: null,
        bay: null,
        row: null,
        tier: null,
      },
      inbound.destinationNode!.id,
    );
    expect(outbound.status).toBe("available");
    expect(outbound.directions[0]).toContain("Quay đầu");
    const main = outbound.waypoints.findIndex((p) => p.y === 1210);
    expect(main).toBeGreaterThan(0);
    expect(
      outbound.waypoints
        .slice(0, main + 1)
        .every((p) => p.x === inbound.destinationNode!.x),
    ).toBe(true);
    expect(outbound.waypoints[main].y).toBeGreaterThan(
      inbound.destinationNode!.y,
    );
  }
});
