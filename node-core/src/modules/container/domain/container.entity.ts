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
export type ContainerVisitStatus = 'planned' | 'active' | 'completed' | 'canceled';
export type ContainerLoadStatus = 'full' | 'empty' | 'unknown';
export type EirActivity = 'in' | 'out';
export type EirStatus = 'draft' | 'issued' | 'voided';

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
  changedBy: string | null;
  changedByName: string | null;
  changedAt: Date;
}

export interface ContainerEirSeal {
  id: string;
  sequenceNo: number;
  sealNumber: string | null;
  rawValue: string | null;
  sealCondition: 'intact' | 'broken' | 'missing' | 'mismatch' | 'unknown';
  notes: string | null;
}

export interface ContainerEirDamage {
  id: string;
  conditionCode: string | null;
  componentCode: string | null;
  damageType: string | null;
  description: string;
  lengthCm: number | null;
  widthCm: number | null;
  heightCm: number | null;
  severity: string | null;
  observedAt: Date;
}

export interface ContainerEirMedia {
  id: string;
  subjectType: string;
  mediaKind: string;
  captureStage: string;
  movementDirection: EirActivity | null;
  viewLabel: string | null;
  mediaUrl: string | null;
  storageProvider: string | null;
  bucketName: string | null;
  storageKey: string | null;
  mimeType: string | null;
  capturedAt: Date;
  detectedValue: string | null;
  recognitionConfidence: number | null;
  isPrimary: boolean;
}

export interface ContainerEirTransaction {
  id: string;
  referenceNumber: string;
  activity: EirActivity;
  status: EirStatus;
  terminalCode: string;
  terminalName: string;
  operatorCode: string | null;
  operatorName: string | null;
  customerName: string | null;
  locationCode: string | null;
  gateLabel: string | null;
  laneCode: string | null;
  validTo: Date | null;
  checkInAt: Date;
  checkOutAt: Date | null;
  blBookingNumber: string | null;
  loadStatus: ContainerLoadStatus;
  isoCode: string | null;
  containerTypeDescription: string | null;
  sizeFeet: number | null;
  cargoTypeDescription: string | null;
  grossWeightKg: number | null;
  temperatureC: number | null;
  ventilationDescription: string | null;
  imdgClass: string | null;
  soundDamageCode: string | null;
  remark: string | null;
  vesselName: string | null;
  voyageIn: string | null;
  voyageOut: string | null;
  transportCompanyName: string | null;
  plateNumber: string | null;
  emptyReturnPlace: string | null;
  issuedAt: Date;
  seals: ContainerEirSeal[];
  damages: ContainerEirDamage[];
  media: ContainerEirMedia[];
}

export interface ContainerVisit {
  id: string;
  visitReference: string;
  bookingId: string | null;
  vesselCallId: string | null;
  status: ContainerVisitStatus;
  loadStatus: ContainerLoadStatus;
  cargoType: CargoType | null;
  grossWeightKg: number | null;
  blBookingNumber: string | null;
  customerName: string | null;
  transportCompanyName: string | null;
  validTo: Date | null;
  startedAt: Date | null;
  completedAt: Date | null;
  createdAt: Date;
  eirs: ContainerEirTransaction[];
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
  visits: ContainerVisit[];
}
