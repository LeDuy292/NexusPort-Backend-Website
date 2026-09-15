import { z } from 'zod';
import { CARGO_TYPES, CONTAINER_CATEGORIES, CONTAINER_SIZES, CONTAINER_STATUSES, ContainerStatus } from '../domain/container.entity';

const ISO_6346_LETTER_VALUES: Record<string, number> = {
  A: 10, B: 12, C: 13, D: 14, E: 15, F: 16, G: 17, H: 18, I: 19,
  J: 20, K: 21, L: 23, M: 24, N: 25, O: 26, P: 27, Q: 28, R: 29,
  S: 30, T: 31, U: 32, V: 34, W: 35, X: 36, Y: 37, Z: 38,
};

export const normalizeContainerNumber = (value: string): string =>
  value.trim().toUpperCase().replace(/[\s-]+/g, '');

export const isValidIso6346 = (rawValue: string): boolean => {
  const value = normalizeContainerNumber(rawValue);
  if (!/^[A-Z]{3}[UJZ]\d{7}$/.test(value)) return false;
  const sum = [...value.slice(0, 10)].reduce((total, character, index) => {
    const numericValue = /\d/.test(character) ? Number(character) : ISO_6346_LETTER_VALUES[character];
    return total + numericValue * (2 ** index);
  }, 0);
  const remainder = sum % 11;
  return (remainder === 10 ? 0 : remainder) === Number(value[10]);
};

const nullableUuid = z.union([z.string().uuid(), z.null()]).optional();
const nullableDateTime = z.union([z.string().datetime({ offset: true }), z.null()]).optional();
const containerNumberSchema = z.string().transform(normalizeContainerNumber).refine(isValidIso6346, {
  message: 'Container ID must be a valid ISO 6346 code with a correct check digit.',
});

export const createContainerSchema = z.object({
  containerNumber: containerNumberSchema,
  containerTypeId: z.string().uuid(),
  sealNumber: z.string().trim().min(1).max(50),
  carrierId: nullableUuid,
  vesselCallId: nullableUuid,
  cargoType: z.enum(CARGO_TYPES).default('general'),
  status: z.enum(CONTAINER_STATUSES).default('expected'),
  grossWeightKg: z.number().nonnegative().max(9999999999.99).nullable().optional(),
  expectedGateOutAt: nullableDateTime,
});

export const updateContainerSchema = createContainerSchema.partial().extend({
  arrivedAt: nullableDateTime,
  leftAt: nullableDateTime,
}).refine((value) => Object.keys(value).length > 0, { message: 'At least one field must be provided.' });

const booleanQuery = z.preprocess((value) => value === true || value === 'true', z.boolean());

export const containerSearchSchema = z.object({
  page: z.coerce.number().int().positive().default(1),
  limit: z.coerce.number().int().positive().max(100).default(20),
  search: z.string().trim().max(100).optional(),
  status: z.enum(CONTAINER_STATUSES).optional(),
  containerTypeId: z.string().uuid().optional(),
  size: z.enum(CONTAINER_SIZES).optional(),
  category: z.enum(CONTAINER_CATEGORIES).optional(),
  cargoType: z.enum(CARGO_TYPES).optional(),
  carrierId: z.string().uuid().optional(),
  includeCanceled: booleanQuery.default(false),
  sortBy: z.enum(['containerNumber', 'status', 'createdAt', 'updatedAt']).default('createdAt'),
  sortOrder: z.enum(['asc', 'desc']).default('desc'),
});

export const idSchema = z.string().uuid();

export const transitionContainerStatusSchema = z.object({
  status: z.nativeEnum(ContainerStatus),
}).strict();
