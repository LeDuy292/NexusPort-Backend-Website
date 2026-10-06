import { z } from 'zod';
import { isValidIso6346, normalizeContainerNumber } from '../../container/application/container.validator';

export const yardUuidSchema = z.string().uuid('ID phải là UUID hợp lệ.');

export const confirmContainerReceptionSchema = z.object({
  containerNumber: z.string().transform(normalizeContainerNumber).refine(isValidIso6346, {
    message: 'Mã Container không đúng ISO 6346 hoặc sai số kiểm tra.',
  }),
  sealNumber: z.string().trim().min(1, 'Số seal là bắt buộc.').max(100),
  condition: z.enum(['sound', 'damaged']),
  conditionNotes: z.preprocess(
    (value) => value === '' || value === undefined ? null : value,
    z.string().trim().max(1000).nullable(),
  ).optional(),
}).strict().superRefine((value, context) => {
  if (value.condition === 'damaged' && !value.conditionNotes) {
    context.addIssue({
      code: z.ZodIssueCode.custom,
      path: ['conditionNotes'],
      message: 'Phải mô tả tình trạng khi Container bị hư hỏng.',
    });
  }
});

export const assignYardSlotSchema = z.object({
  slotId: yardUuidSchema,
}).strict();
