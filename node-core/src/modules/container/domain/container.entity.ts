export const CONTAINER_STATUSES = [
  'expected', 'discharged', 'in_yard', 'reserved', 'moving',
  'gate_in', 'gate_out', 'loaded', 'damaged', 'canceled',
] as const;

/** The business lifecycle exposed by the Container Status API. */
export enum ContainerStatus {
  Registered = 'registered',
  Booked = 'booked',
  GateIn = 'gate_in',
  InYard = 'in_yard',
  ReadyForGateOut = 'ready_for_gate_out',
  GateOut = 'gate_out',
}

export const CARGO_TYPES = [
  'general', 'reefer', 'dangerous', 'perishable', 'oversized', 'overweight',
] as const;

export const CONTAINER_SIZES = ['ft20', 'ft40', 'ft45'] as const;
export const CONTAINER_CATEGORIES = ['dry', 'reefer', 'tank', 'open_top', 'flat_rack'] as const;

export type PersistedContainerStatus = (typeof CONTAINER_STATUSES)[number];
export type CargoType = (typeof CARGO_TYPES)[number];
export type ContainerSize = (typeof CONTAINER_SIZES)[number];
export type ContainerCategory = (typeof CONTAINER_CATEGORIES)[number];

export interface ContainerEntity {
  id: string;
  carrierId: string | null;
  containerTypeId: string;
  vesselCallId: string | null;
  containerNumber: string;
  sealNumber: string | null;
  cargoType: CargoType;
  status: PersistedContainerStatus;
  grossWeightKg: number | null;
  isReefer: boolean;
  isDangerous: boolean;
  isPerishable: boolean;
  isOversized: boolean;
  expectedGateOutAt: Date | null;
  arrivedAt: Date | null;
  leftAt: Date | null;
  createdAt: Date;
  updatedAt: Date;
}

export interface ContainerCurrentStatus {
  containerId: string;
  status: ContainerStatus;
  persistedStatus: PersistedContainerStatus;
  updatedAt: Date;
}

export interface ContainerStatusHistoryEntry {
  id: string;
  containerId: string;
  fromStatus: ContainerStatus;
  toStatus: ContainerStatus;
  changedBy: string;
  changedAt: Date;
}

export interface ContainerTypeEntity {
  id: string;
  code: string;
  size: ContainerSize;
  category: ContainerCategory;
  description: string | null;
  tareWeightKg: number | null;
  maxGrossWeightKg: number | null;
}

export interface ContainerBookingSummary {
  id: string;
  bookingCode: string;
  bookingType: string;
  status: string;
  appointmentStart: Date;
  appointmentEnd: Date;
}

export interface ContainerPositionSummary {
  slotId: string;
  blockCode: string;
  bay: number;
  row: number;
  tier: number;
  placedAt: Date;
}

export interface ContainerDetailEntity extends ContainerEntity {
  containerType: ContainerTypeEntity;
  carrierName: string | null;
  vesselCallCode: string | null;
  bookings: ContainerBookingSummary[];
  currentPosition: ContainerPositionSummary | null;
}
