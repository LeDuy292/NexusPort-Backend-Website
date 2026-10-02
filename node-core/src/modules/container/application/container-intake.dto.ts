import { CargoType } from '../domain/container.entity';

export type ContainerIntakeSource = 'port_vessel' | 'transport_company';
export type ContainerMovementType = 'vessel_discharge' | 'truck_dropoff' | 'pickup_request';

export interface ContainerIntakeRow {
  containerNumber: string;
  containerTypeCode: string;
  sourceType: ContainerIntakeSource;
  movementType: ContainerMovementType;
  sourceReference?: string | null;
  sealNumber?: string | null;
  loadStatus?: 'full' | 'empty' | 'unknown';
  cargoType?: CargoType;
  grossWeightKg?: number | null;
  vesselCallCode?: string | null;
  expectedArrivalAt?: string | null;
  expectedAvailableAt?: string | null;
  requestedPickupDate?: string | null;
  blBookingNumber?: string | null;
  customerName?: string | null;
  transportCompanyName?: string | null;
}

export interface ContainerIntakeResult {
  rowNumber: number;
  containerNumber: string | null;
  status: 'created_master' | 'created_visit' | 'updated_visit' | 'duplicate' | 'rejected';
  containerId?: string;
  visitId?: string;
  errors: string[];
}

export interface ContainerImportBatchSummary {
  id: string;
  sourceType: ContainerIntakeSource;
  fileName: string;
  importStatus: 'processing' | 'completed' | 'partial' | 'failed';
  totalRows: number;
  successRows: number;
  duplicateRows: number;
  failedRows: number;
  createdAt: Date;
  completedAt: Date | null;
}

export interface TransportContainerDeclaration {
  id: string;
  visitReference: string;
  sourceReference: string | null;
  containerId: string;
  containerNumber: string;
  containerTypeCode: string;
  movementType: ContainerMovementType;
  dataStatus: 'pending_verification' | 'verified' | 'rejected';
  sealNumber: string | null;
  loadStatus: 'full' | 'empty' | 'unknown';
  cargoType: CargoType;
  grossWeightKg: number | null;
  requestedPickupDate: string | null;
  blBookingNumber: string | null;
  customerName: string | null;
  transportCompanyName: string | null;
  createdAt: Date;
  updatedAt: Date;
}
