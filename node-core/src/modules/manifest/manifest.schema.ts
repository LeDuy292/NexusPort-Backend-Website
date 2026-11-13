import { z } from 'zod';
import { isValidIso6346, normalizeContainerNumber } from '../container/application/container.validator';

const text = z.string().trim().min(1, 'Thông tin này không được để trống.');
export const manifestContainerSchema = z.object({
  containerNumber: text.transform(normalizeContainerNumber).refine(isValidIso6346, 'Số container sai chuẩn ISO 6346.'),
  // Equipment fields are overwritten from the registered physical identity.
  isoType: z.string().trim().toUpperCase().max(10).default(''),
  isoSizeType: z.string().max(10).nullable().optional(),
  containerId: z.string().uuid().optional(),
  sealNumber: text.max(50),
  grossWeightKg: z.number().finite().positive('Trọng lượng phải lớn hơn 0.').max(36000),
  commodity: text.max(2000),
  isReefer: z.boolean().default(false),
}).transform(c => ({ ...c, isReefer: c.isReefer || /RF|RH|R[01]/.test(c.isoType) }));

export const manifestSchema = z.object({
  requestKey: z.string().uuid(),
  revision: z.number().int().positive().optional(),
  voyageNumber: text.max(100),
  vesselName: text.max(250),
  direction: z.enum(['IMPORT', 'EXPORT']),
  isDraft: z.boolean().default(false),
  bills: z.array(z.object({
    blNumber: text.toUpperCase().max(100),
    shipper: text.max(500),
    consignee: text.max(500),
    containers: z.array(manifestContainerSchema).min(1, 'Thêm ít nhất một container cho vận đơn.').max(500),
  })).min(1, 'Thêm ít nhất một vận đơn.').max(100),
}).superRefine((data, ctx) => {
  const bills = new Set<string>();
  const containers = new Set<string>();
  data.bills.forEach((bill, i) => {
    if (bills.has(bill.blNumber)) ctx.addIssue({ code: 'custom', path: ['bills', i, 'blNumber'], message: 'Số vận đơn bị trùng trong bản khai.' });
    bills.add(bill.blNumber);
    bill.containers.forEach((c, j) => {
      if (containers.has(c.containerNumber)) ctx.addIssue({ code: 'custom', path: ['bills', i, 'containers', j, 'containerNumber'], message: 'Container chỉ được thuộc một vận đơn trong bản khai.' });
      containers.add(c.containerNumber);
    });
  });
});
export type ManifestInput = z.infer<typeof manifestSchema>;
