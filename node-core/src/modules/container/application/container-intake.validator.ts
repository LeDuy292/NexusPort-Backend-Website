import { z } from 'zod';
import { CARGO_TYPES } from '../domain/container.entity';
import { isValidIso6346, normalizeContainerNumber } from './container.validator';
const optionalText = z.preprocess(v => v === '' || v === undefined ? null : v, z.string().trim().max(250).nullable());
const optionalNumber = z.preprocess(v => v === '' || v === undefined || v === null ? null : v, z.coerce.number().finite().nonnegative().max(99999999.99).nullable());
export const containerIntakeSchema = z.object({
    containerNumber: z.string().transform(normalizeContainerNumber).refine(isValidIso6346, { message: 'Mã Container không đúng ISO 6346 hoặc sai số kiểm tra.' }),
    containerTypeCode: z.string().trim().toUpperCase().min(1).max(20),
    sealNumber: z.preprocess(v => typeof v === 'string' && !v.trim() ? null : v,
        z.string().trim().max(100).nullable().optional()),
    sourceType: z.literal('transport_company'),
    loadStatus: z.enum(['full', 'empty', 'unknown']).default('unknown'),
    cargoType: z.enum(CARGO_TYPES).default('general'),
    grossWeightKg: optionalNumber.optional(), requiredPowerKw: optionalNumber.optional(),
    customerName: optionalText.optional(),
}).strict().superRefine((v, ctx) => {
    if (v.cargoType === 'reefer' && !(Number(v.requiredPowerKw) >= 0.01))
        ctx.addIssue({ code: z.ZodIssueCode.custom, path: ['requiredPowerKw'], message: 'Nhập công suất điện yêu cầu từ 0,01 kW cho hàng đông lạnh.' });
    if (v.cargoType !== 'reefer' && v.requiredPowerKw != null)
        ctx.addIssue({ code: z.ZodIssueCode.custom, path: ['requiredPowerKw'], message: 'Chỉ hàng đông lạnh được khai báo nhu cầu điện.' });
});
export const importSourceSchema = z.literal('transport_company');
