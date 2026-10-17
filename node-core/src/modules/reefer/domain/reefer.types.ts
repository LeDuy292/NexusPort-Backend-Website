export type ReeferRequestStatus =
  | 'PENDING_ASSIGNMENT'
  | 'ASSIGNED'
  | 'IN_PROGRESS'
  | 'COMPLETED'
  | 'CANCELLED'
  | 'REJECTED';

export type SocketUsageStatus = 'AVAILABLE' | 'RESERVED' | 'OCCUPIED';

export type SocketTechnicalStatus = 'HEALTHY' | 'FAULTY' | 'UNDER_MAINTENANCE' | 'DECOMMISSIONED';

export type PowerSessionStatus =
  | 'AWAITING_POWER'
  | 'ACTIVE'
  | 'SUSPENDED'
  | 'COMPLETED'
  | 'TRANSFERRED'
  | 'CANCELLED';

export type ActualPowerStatus = 'UNKNOWN' | 'POWERED_ON' | 'POWER_LOST' | 'POWERED_OFF';

export type TempDataSource = 'MANUAL_INSPECTION' | 'IOT_TELEMATICS' | 'PLC_GATEWAY';

export type AlertSeverity = 'INFO' | 'WARNING' | 'CRITICAL';

export type ReeferAlertStatus = 'TRIGGERED' | 'ACKNOWLEDGED' | 'IN_PROGRESS' | 'RESOLVED' | 'DISMISSED';

export interface ReeferPlug {
  id: string;
  slot_id?: string | null;
  plug_code: string;
  is_available: boolean;
  is_power_on: boolean;
  usage_status: SocketUsageStatus;
  technical_status: SocketTechnicalStatus;
  voltage_type: string;
  max_power_kw?: number | null;
  last_maintained_at?: Date | null;
  slot_code?: string | null;
  block_code?: string | null;
  active_session?: ReeferPowerSession | null;
  created_at: Date;
  updated_at: Date;
}

export interface ReeferPowerRequest {
  id: string;
  request_no: string;
  container_id: string;
  container_no?: string;
  target_temperature: number;
  temperature_unit: 'C' | 'F';
  temp_tolerance: number;
  cargo_description?: string | null;
  special_instructions?: string | null;
  status: ReeferRequestStatus;
  requested_by?: string | null;
  requester_name?: string | null;
  cancelled_reason?: string | null;
  created_at: Date;
  updated_at: Date;
}

export interface ReeferPowerSession {
  id: string;
  container_id: string;
  container_no?: string;
  plug_id: string;
  plug_code?: string;
  slot_id?: string | null;
  slot_code?: string | null;
  block_code?: string | null;
  request_id?: string | null;
  assigned_by?: string | null;
  assigned_by_name?: string | null;
  assigned_at: Date;
  power_confirmed_by?: string | null;
  power_confirmed_by_name?: string | null;
  started_at?: Date | null;
  ended_at?: Date | null;
  is_active: boolean;
  session_status: PowerSessionStatus;
  actual_power_status: ActualPowerStatus;
  last_checked_at?: Date | null;
  last_temperature?: number | null;
  target_temperature?: number | null;
  temp_tolerance?: number | null;
  billable_hours?: number;
  note?: string | null;
  termination_reason?: string | null;
  parent_session_id?: string | null;
  created_at: Date;
  updated_at: Date;
}

export interface ReeferTemperatureRecord {
  id: string;
  session_id: string;
  supply_temperature?: number | null;
  return_temperature: number;
  temperature_unit: 'C' | 'F';
  ambient_temperature?: number | null;
  data_source: TempDataSource;
  is_abnormal: boolean;
  recorded_by?: string | null;
  recorder_name?: string | null;
  device_id?: string | null;
  notes?: string | null;
  recorded_at: Date;
}

export interface ReeferAlert {
  id: string;
  alert_code: string;
  session_id?: string | null;
  plug_id?: string | null;
  container_id?: string | null;
  container_no?: string | null;
  plug_code?: string | null;
  alert_type: string;
  severity: AlertSeverity;
  status: ReeferAlertStatus;
  message: string;
  payload?: any;
  acknowledged_by?: string | null;
  acknowledged_by_name?: string | null;
  acknowledged_at?: Date | null;
  resolved_by?: string | null;
  resolved_by_name?: string | null;
  resolved_at?: Date | null;
  resolution_notes?: string | null;
  created_at: Date;
  updated_at: Date;
}

export interface ReeferOverviewStats {
  total_plugs: number;
  available_plugs: number;
  occupied_plugs: number;
  reserved_plugs: number;
  faulty_plugs: number;
  active_sessions: number;
  awaiting_power_sessions: number;
  pending_requests: number;
  active_alerts: number;
}
