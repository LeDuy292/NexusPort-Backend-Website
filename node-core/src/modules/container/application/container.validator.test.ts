import {
  createContainerSchema, isValidIso6346, normalizeContainerNumber, transitionContainerStatusSchema,
} from './container.validator';

describe('ISO 6346 container validation', () => {
  it('accepts valid container IDs and normalizes spacing/case', () => {
    expect(isValidIso6346('CSQU3054383')).toBe(true);
    expect(normalizeContainerNumber(' csqu 305438-3 ')).toBe('CSQU3054383');
  });

  it('rejects an invalid check digit and malformed IDs', () => {
    expect(isValidIso6346('CSQU3054384')).toBe(false);
    expect(isValidIso6346('ABC123')).toBe(false);
  });

  it('requires container type and seal number when registering', () => {
    const result = createContainerSchema.safeParse({ containerNumber: 'CSQU3054383' });
    expect(result.success).toBe(false);
    if (!result.success) {
      expect(result.error.flatten().fieldErrors.containerTypeId).toBeDefined();
      expect(result.error.flatten().fieldErrors.sealNumber).toBeDefined();
    }
  });
});

describe('container status transition validation', () => {
  it('accepts a lifecycle status and rejects legacy or unknown statuses', () => {
    expect(transitionContainerStatusSchema.safeParse({ status: 'ready_for_gate_out' }).success).toBe(true);
    expect(transitionContainerStatusSchema.safeParse({ status: 'moving' }).success).toBe(false);
    expect(transitionContainerStatusSchema.safeParse({ status: 'damaged' }).success).toBe(false);
  });
});
