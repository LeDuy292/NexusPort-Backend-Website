'use strict';

require('../src/config/env');
const yardService = require('../src/modules/yard/yard.service');

describe('Yard Slot & Container History Feature', () => {
  describe('parseSlotCode', () => {
    it('parses standard dot-separated slot code (e.g. C01.06.01.1)', () => {
      const parsed = yardService.parseSlotCode('C01.06.01.1');
      expect(parsed).toEqual({
        block: 'C01',
        bay: 6,
        row: 1,
        tier: 1,
      });
    });

    it('parses dash-separated slot code with block prefix (e.g. BLOCK A-01-02-3)', () => {
      const parsed = yardService.parseSlotCode('BLOCK A-01-02-3');
      expect(parsed).toEqual({
        block: 'A',
        bay: 1,
        row: 2,
        tier: 3,
      });
    });

    it('returns null for pure container numbers or invalid inputs', () => {
      expect(yardService.parseSlotCode('HLCU7719204')).toBeNull();
      expect(yardService.parseSlotCode('')).toBeNull();
      expect(yardService.parseSlotCode(null)).toBeNull();
    });
  });

  describe('getSlotOccupancyHistory', () => {
    it('finds slot occupancy history by container number (Timeline)', async () => {
      const result = await yardService.getSlotOccupancyHistory({
        containerNo: 'HLCU7719204',
        order: 'DESC',
      });

      expect(result).toBeDefined();
      expect(Array.isArray(result.items)).toBe(true);
      expect(result.items.length).toBeGreaterThan(0);

      const firstItem = result.items[0];
      expect(firstItem.container.containerNo).toBe('HLCU7719204');
      expect(firstItem.slot).toHaveProperty('slotCode');
      expect(firstItem.slot).toHaveProperty('blockCode');
      expect(firstItem.slot).toHaveProperty('bay');
      expect(firstItem.slot).toHaveProperty('row');
      expect(firstItem.slot).toHaveProperty('tier');
      expect(firstItem).toHaveProperty('placedAt');
      expect(firstItem).toHaveProperty('duration');
      expect(firstItem).toHaveProperty('placedBy');
      expect(firstItem.placedBy).toHaveProperty('fullName');
      expect(firstItem.placedBy).toHaveProperty('role');

      // Verify pagination structure
      expect(result.pagination).toMatchObject({
        page: 1,
        limit: 10,
        hasNext: expect.any(Boolean),
        hasPrev: false,
      });
      expect(result.pagination.totalItems).toBeGreaterThanOrEqual(1);
    });

    it('finds container history by slot code', async () => {
      const result = await yardService.getSlotOccupancyHistory({
        slotCode: 'C01.12.01.1',
      });

      expect(result).toBeDefined();
      expect(Array.isArray(result.items)).toBe(true);
      if (result.items.length > 0) {
        const item = result.items[0];
        expect(item.slot.blockCode).toBe('C01');
        expect(item.slot.bay).toBe(12);
        expect(item.slot.row).toBe(1);
        expect(item.slot.tier).toBe(1);
      }
    });

    it('supports pagination with custom limit and page', async () => {
      const result = await yardService.getSlotOccupancyHistory({
        limit: 2,
        page: 1,
      });

      expect(result.pagination.limit).toBe(2);
      expect(result.pagination.page).toBe(1);
      expect(result.items.length).toBeLessThanOrEqual(2);
    });
  });

  describe('exportSlotOccupancyHistoryCsv', () => {
    it('exports CSV formatted with UTF-8 BOM and required column headers', async () => {
      const csv = await yardService.exportSlotOccupancyHistoryCsv({
        containerNo: 'HLCU7719204',
      });

      expect(typeof csv).toBe('string');
      // Starts with UTF-8 BOM
      expect(csv.charCodeAt(0)).toBe(0xFEFF);

      // Contains essential headers
      expect(csv).toContain('STT');
      expect(csv).toContain('Mã Lượt Lưu');
      expect(csv).toContain('Số Container');
      expect(csv).toContain('Vị Trí Slot');
      expect(csv).toContain('Thời Gian Vào (Bắt đầu)');
      expect(csv).toContain('Thời Gian Rời (Kết thúc)');
      expect(csv).toContain('Thời Lượng Lưu');
      expect(csv).toContain('Người Thực Hiện (Họ tên)');

      // Contains the container number in the rows
      expect(csv).toContain('HLCU7719204');
    });
  });
});

