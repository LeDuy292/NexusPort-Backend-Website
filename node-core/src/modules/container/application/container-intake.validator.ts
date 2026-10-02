import { z } from 'zod';
import { CARGO_TYPES } from '../domain/container.entity';
import { isValidIso6346, normalizeContainerNumber } from './container.validator';

const optionalText = (max: number) => z.preprocess(
  (value) => value === '' || value === undefined ? null : value,
  z.string().trim().max(max).nullable(),
);
const optionalDateTime = z.preprocess(
  (value) => value === '' || value === undefined ? null : value,
  z.string().datetime({ offset: true }).nullable(),
);
const optionalDate = z.preprocess(
  (value) => value === '' || value === undefined ? null : value,
  z.string().regex(/^\d{4}-\d{2}-\d{2}$/, 'Ngày phải có định dạng YYYY-MM-DD.').nullable(),
);
const optionalNumber = z.preprocess(
  (value) => value === '' || value === undefined || value === null ? null : Number(value),
  z.number().nonnegative().max(9999999999.99).nullable(),
);

export const containerIntakeSchema = z.object({
  containerNumber: z.string().transform(normalizeContainerNumber).refine(isValidIso6346, {
    message: 'Mã Container không đúng ISO 6346 hoặc sai số kiểm tra.',
  }),
  containerTypeCode: z.string().trim().toUpperCase().min(1).max(20),
  sourceType: z.enum(['port_vessel', 'transport_company']),
  movementType: z.enum(['vessel_discharge', 'truck_dropoff', 'pickup_request']),
  sourceReference: optionalText(150).optional(),
  sealNumber: optionalText(100).optional(),
  loadStatus: z.enum(['full', 'empty', 'unknown']).default('unknown'),
  cargoType: z.enum(CARGO_TYPES).default('general'),
  grossWeightKg: optionalNumber.optional(),
  vesselCallCode: optionalText(80).optional(),
  expectedArrivalAt: optionalDateTime.optional(),
  expectedAvailableAt: optionalDateTime.optional(),
  requestedServiceDate: optionalDate.optional(),
  requestedPickupDate: optionalDate.optional(),
  blBookingNumber: optionalText(150).optional(),
  customerName: optionalText(250).optional(),
  transportCompanyName: optionalText(250).optional(),
}).superRefine((value, context) => {
  if (value.sourceType === 'port_vessel') {
    if (value.movementType !== 'vessel_discharge') {
      context.addIssue({ code: z.ZodIssueCode.custom, path: ['movementType'], message: 'Nguồn cảng/tàu phải dùng loại lượt dỡ từ tàu.' });
    }
    if (!value.expectedArrivalAt) {
      context.addIssue({ code: z.ZodIssueCode.custom, path: ['expectedArrivalAt'], message: 'Nguồn cảng/tàu phải có thời gian dự kiến cập cảng.' });
    }
  }
  if (value.sourceType === 'transport_company') {
    if (!['pickup_request', 'truck_dropoff'].includes(value.movementType)) {
      context.addIssue({ code: z.ZodIssueCode.custom, path: ['movementType'], message: 'Loại lượt không phù hợp với công ty vận chuyển.' });
    }
    if (!value.transportCompanyName) {
      context.addIssue({ code: z.ZodIssueCode.custom, path: ['transportCompanyName'], message: 'Phải có tên công ty vận chuyển.' });
    }
    if (!value.requestedServiceDate) {
      context.addIssue({ code: z.ZodIssueCode.custom, path: ['requestedServiceDate'], message: 'Phải có ngày mong muốn thực hiện.' });
    }
  }
  if (value.expectedArrivalAt && value.expectedAvailableAt &&
      new Date(value.expectedAvailableAt) < new Date(value.expectedArrivalAt)) {
    context.addIssue({ code: z.ZodIssueCode.custom, path: ['expectedAvailableAt'], message: 'Thời gian dự kiến sẵn sàng không được trước thời gian cập cảng.' });
  }
});

export const importSourceSchema = z.enum(['port_vessel', 'transport_company']);
