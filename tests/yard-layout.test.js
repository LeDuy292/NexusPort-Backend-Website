'use strict';
const { referenceLayout: plan, buildLayoutSnapshot } = require('../src/modules/yard/yard-layout');

describe('Spreadsheet yard layout', () => {
  it('preserves the 66 areas, 376 identifiers and the existing single gate codes', () => {
    expect(plan.areas).toHaveLength(66);
    expect(plan.areas.flatMap(area => area.rows)).toHaveLength(376);
    expect(new Set(plan.areas.map(area => area.code)).size).toBe(66);
    expect(plan.gates.map(gate => [gate.code, gate.direction])).toEqual([['GATE-01', 'in'], ['GATE-02', 'out']]);
    expect(plan.areas.find(area => area.code === 'C12').rows).toEqual([5, 6, 7, 8, 9]);
    expect(plan.areas.find(area => area.code === 'B11').rows).toEqual([3, 4, 5, 6, 7, 8, 9, 10, 11]);
  });
  it('keeps unconfigured positions unknown and never treats source red as a restriction', () => {
    const snapshot = buildLayoutSnapshot([]);
    expect(snapshot.slots).toEqual([]);
    expect(snapshot.referenceLayout.status).toBe('proposed');
    expect(plan.areas.filter(area => area.sourceHighlight).map(area => area.code).sort()).toEqual(['A20', 'B16', 'B17', 'C6']);
    expect(plan.areas.some(area => area.restricted)).toBe(false);
  });
  it('does not drop legacy slots or guess block aliases', () => {
    const slot = { id: 'slot-1', blockId: 'block-a', block: 'A', bay: 1, row: 1, tier: 2, status: 'reserved' };
    const result = buildLayoutSnapshot([slot, { ...slot, id: 'sixth-row', row: 6 }, { ...slot, id: 'reefer', block: 'R' }, { ...slot, id: 'other', block: 'A01' }]);
    expect(result.slots).toHaveLength(4);
    expect(result.slots[0]).toMatchObject({ status: 'reserved', mapped: true, containerId: null });
    expect(result.unmappedSlotCount).toBe(3);
  });
  it('keeps the proposed traffic route and area rectangles separate', () => {
    for (const road of plan.roads) for (let i = 1; i < road.points.length; i++) {
      const [a, b] = [road.points[i-1], road.points[i]];
      expect(a[0] === b[0] || a[1] === b[1]).toBe(true);
      for (const area of plan.areas) {
        const throughX = a[0] === b[0] && a[0] > area.x && a[0] < area.x + area.width && Math.min(a[1],b[1]) < area.y+area.height && Math.max(a[1],b[1]) > area.y;
        const throughY = a[1] === b[1] && a[1] > area.y && a[1] < area.y + area.height && Math.min(a[0],b[0]) < area.x+area.width && Math.max(a[0],b[0]) > area.x;
        expect(throughX || throughY).toBe(false);
      }
    }
  });
});
