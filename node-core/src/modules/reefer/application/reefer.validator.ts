import { z } from 'zod';

const UUID_SCHEMA = z.string().uuid({ message: 'ID không đúng định dạng UUID hợp lệ.' });

export const createReeferRequestSchema = z.object({
  container_id: UUID_SCHEMA,
  target_temperature: z.number({ required_error: 'Vui lòng nhập nhiệt độ cài đặt mong muốn.' }),
  temperature_unit: z.enum(['C', 'F']).default('C'),
  temp_tolerance: z.number().min(0.1, 'Biên độ nhiệt độ tối thiểu là 0.1°C').max(10.0, 'Biên độ nhiệt độ tối đa là 10°C').default(2.0),
  cargo_description: z.string().optional(),
  special_instructions: z.string().optional(),
});

export const assignPlugSchema = z.object({
  container_id: UUID_SCHEMA,
  plug_id: UUID_SCHEMA,
  request_id: UUID_SCHEMA.optional(),
  target_temperature: z.number().optional(),
  temp_tolerance: z.number().optional(),
  cargo_description: z.string().optional(),
  note: z.string().optional(),
});

export const confirmPowerSchema = z.object({
  initial_temperature: z.number().optional(),
});

export const terminateSessionSchema = z.object({
  reason: z.string().min(3, 'Vui lòng cung cấp lý do kết thúc cấp điện (tối thiểu 3 ký tự).'),
});

export const transferPlugSchema = z.object({
  new_plug_id: UUID_SCHEMA,
  reason: z.string().min(3, 'Vui lòng cung cấp lý do chuyển ổ cắm (tối thiểu 3 ký tự).'),
});

export const recordTemperatureSchema = z.object({
  supply_temperature: z.number().optional(),
  return_temperature: z.number({ required_error: 'Vui lòng nhập nhiệt độ hồi (return temperature).' }),
  temperature_unit: z.enum(['C', 'F']).default('C'),
  ambient_temperature: z.number().optional(),
  data_source: z.enum(['MANUAL_INSPECTION', 'IOT_TELEMATICS', 'PLC_GATEWAY']).default('MANUAL_INSPECTION'),
  device_id: z.string().optional(),
  notes: z.string().optional(),
});

export const updatePlugStatusSchema = z.object({
  technical_status: z.enum(['HEALTHY', 'FAULTY', 'UNDER_MAINTENANCE', 'DECOMMISSIONED'], {
    required_error: 'Trạng thái kỹ thuật không hợp lệ.',
  }),
});

export const resolveAlertSchema = z.object({
  notes: z.string().min(3, 'Vui lòng nhập nội dung xử lý khắc phục cảnh báo.'),
});
