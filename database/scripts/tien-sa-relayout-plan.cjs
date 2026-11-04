"use strict";
const { createHash } = require("node:crypto");
function stableId(value) {
  const h = createHash("sha256")
    .update("NexusPort Tiên Sa " + value)
    .digest("hex");
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-5${h.slice(13, 16)}-a${h.slice(17, 20)}-${h.slice(20, 32)}`;
}
const openBooking = (b) =>
  !["completed", "canceled", "cancelled", "rejected", "expired"].includes(
    b.status,
  );
const timestamp = (value) => new Date(value).toISOString();
function buildRelayoutPlan(state, design) {
  const containers = new Map(state.containers.map((c) => [c.id, c])),
    types = new Map(state.container_types.map((t) => [t.id, t]));
  const bookings = new Map(
    state.bookings.filter(openBooking).map((b) => [b.id, b]),
  );
  const entities = new Map(),
    physical = new Map(),
    activeVisits = new Map();
  for (const v of state.container_visits.filter(
    (v) => v.visit_status === "active",
  )) {
    if (activeVisits.has(v.container_id))
      throw Error("Ambiguous active visit: " + v.container_id);
    activeVisits.set(v.container_id, v.id);
  }
  function entity(key, containerId) {
    if (!entities.has(key)) {
      const c = containers.get(containerId),
        type = types.get(c?.container_type_id);
      const size = Number(String(type?.size).replace("ft", ""));
      if (!c || ![20, 40, 45].includes(size))
        throw Error("Unsupported/missing container size: " + containerId);
      entities.set(key, {
        key,
        containerId,
        containerNo: c.container_no,
        typeCode: type.code,
        size,
        needsPower: !!c.is_reefer,
        bookings: [],
        power: [],
        position: null,
        sort: "9999",
      });
    }
    return entities.get(key);
  }
  for (const p of state.container_positions.filter((p) => p.is_current)) {
    if (physical.has(p.container_id))
      throw Error("Duplicate physical placement: " + p.container_id);
    const e = entity(
      activeVisits.has(p.container_id)
        ? "visit:" + activeVisits.get(p.container_id)
        : "physical:" + p.container_id,
      p.container_id,
    );
    e.position = p;
    e.sort = timestamp(p.placed_at);
    physical.set(p.container_id, e);
  }
  for (const link of state.booking_containers) {
    const b = bookings.get(link.booking_id);
    if (!b) continue;
    const direction = link.direction || b.booking_type;
    if (!["pickup", "dropoff"].includes(direction))
      throw Error("Booking direction is not resolved: " + b.id);
    const e = link.container_visit_id
      ? entity("visit:" + link.container_visit_id, link.container_id)
      : direction === "pickup" && physical.has(link.container_id)
        ? physical.get(link.container_id)
        : entity(`booking:${b.id}:${link.container_id}`, link.container_id);
    e.bookings.push({ ...link, booking: b, direction });
    e.needsPower ||= !!b.is_reefer_power_required;
    e.sort =
      e.sort < timestamp(b.created_at) ? e.sort : timestamp(b.created_at);
  }
  for (const session of state.reefer_power_sessions.filter((s) =>
    ["ACTIVE", "AWAITING_POWER", "SUSPENDED"].includes(s.session_status),
  )) {
    const e =
      physical.get(session.container_id) ||
      entity(
        activeVisits.has(session.container_id)
          ? "visit:" + activeVisits.get(session.container_id)
          : "power:" + session.container_id,
        session.container_id,
      );
    e.power.push(session);
    e.needsPower = true;
  }
  const ordered = [...entities.values()].sort(
    (a, b) =>
      a.sort.localeCompare(b.sort) ||
      a.containerNo.localeCompare(b.containerNo) ||
      a.key.localeCompare(b.key),
  );
  const slots = [],
    bySlot = new Map(),
    allocated = new Map();
  const areas = design.areas.map((a) => ({
    ...a,
    id: stableId("block:" + a.code),
  }));
  function addSlot(area, start, row, tier, size, entity = null) {
    const span = size === 45 ? 4 : size === 40 ? 2 : 1,
      bay = start * 2 + (size === 45 ? 4 : size === 40 ? 2 : 1);
    const slot = {
      id: stableId(`slot:${area.code}:${bay}:${row}:${tier}`),
      blockId: area.id,
      block: area.code,
      bay,
      row,
      tier,
      moduleStart: start + 1,
      moduleSpan: span,
      containerLengthFt: size,
      status: entity?.position ? "occupied" : entity ? "reserved" : "empty",
    };
    if (start + span > area.planning.bayModules)
      throw Error("Slot outside block");
    slots.push(slot);
    bySlot.set(slot.id, slot);
    if (entity) {
      entity.slot = slot;
      allocated.set(entity.key, slot);
    }
    return span;
  }
  for (const sector of ["C", "D"]) {
    const queue = ordered.filter((e) => (e.needsPower ? "D" : "C") === sector);
    let next = 0;
    for (const area of areas.filter((a) => a.sector === sector))
      for (let tier = 1; tier <= area.planning.tiers; tier++)
        for (let row = 1; row <= area.planning.rows; row++) {
          let start = 0;
          while (next < queue.length) {
            const e = queue[next],
              span = e.size === 45 ? 4 : e.size === 40 ? 2 : 1;
            if (start + span > area.planning.bayModules) break;
            start += addSlot(area, start, row, tier, e.size, e);
            next++;
          }
          while (start < area.planning.bayModules) {
            const size = area.planning.bayModules - start >= 2 ? 40 : 20;
            start += addSlot(area, start, row, tier, size);
          }
        }
    if (next !== queue.length)
      throw Error("Not enough space in sector " + sector);
  }
  for (const area of areas.filter((a) => !["C", "D"].includes(a.sector)))
    for (let tier = 1; tier <= area.planning.tiers; tier++)
      for (let row = 1; row <= area.planning.rows; row++) {
        let start = 0;
        while (start < area.planning.bayModules)
          start += addSlot(
            area,
            start,
            row,
            tier,
            area.planning.bayModules - start >= 2 ? 40 : 20,
          );
      }
  const plugAssignments = [],
    usedPlugs = new Set(),
    poweredSlots = new Set();
  for (const e of ordered)
    for (const p of e.power) {
      if (usedPlugs.has(p.plug_id))
        throw Error("Plug has multiple active sessions");
      if (!state.reefer_plugs.some((plug) => plug.id === p.plug_id))
        throw Error("Active plug missing");
      usedPlugs.add(p.plug_id);
      poweredSlots.add(e.slot.id);
      plugAssignments.push({ plugId: p.plug_id, slotId: e.slot.id });
    }
  const freePlugs = state.reefer_plugs
    .filter((p) => !usedPlugs.has(p.id))
    .sort((a, b) =>
      String(a.plug_code || a.id).localeCompare(String(b.plug_code || b.id)),
    );
  const reeferTargets = [
    ...ordered
      .filter((e) => e.needsPower && !poweredSlots.has(e.slot.id))
      .map((e) => e.slot),
    ...slots.filter(
      (s) =>
        s.block.startsWith("D") &&
        !poweredSlots.has(s.id) &&
        s.status === "empty",
    ),
  ];
  const uniqueTargets = [
    ...new Map(reeferTargets.map((s) => [s.id, s])).values(),
  ];
  if (
    freePlugs.length <
    ordered.filter((e) => e.needsPower && !poweredSlots.has(e.slot.id)).length
  )
    throw Error("Not enough existing reefer plugs for assigned containers");
  for (let i = 0; i < freePlugs.length; i++) {
    if (!uniqueTargets[i])
      throw Error("Not enough D positions for existing plugs");
    plugAssignments.push({
      plugId: freePlugs[i].id,
      slotId: uniqueTargets[i].id,
    });
    poweredSlots.add(uniqueTargets[i].id);
  }
  for (const s of slots) s.hasReeferPlug = poweredSlots.has(s.id);
  return {
    areas,
    slots,
    entities: ordered,
    plugAssignments,
    summary: {
      blocks: areas.length,
      slots: slots.length,
      physicalContainers: ordered.filter((e) => e.position).length,
      bookingContainers: state.booking_containers.filter((l) =>
        bookings.has(l.booking_id),
      ).length,
      openBookings: bookings.size,
      allocations: ordered.length,
      reeferPlugs: plugAssignments.length,
      powerSessionsPreserved: ordered.reduce((n, e) => n + e.power.length, 0),
    },
  };
}
module.exports = { stableId, openBooking, buildRelayoutPlan };
