export const YARD_SLOT_STATUSES = ['empty', 'reserved', 'occupied', 'maintenance'] as const;

export type YardSlotStatus = typeof YARD_SLOT_STATUSES[number];

export interface YardDestination {
  slotId: string;
  blockId: string;
  block: string;
  blockName: string;
  zone: string | null;
  bay: number;
  row: number;
  tier: number;
  status: YardSlotStatus;
  maxWeightKg: number | null;
  hasReeferPlug: boolean;
}

export interface DestinationAssignment {
  operationId: string;
  taskCode: string;
  operationType: string;
  operationStatus: string;
  container: { id: string; number: string };
  driverId: string;
  destination: YardDestination;
  assignedAt: Date;
  assignedBy: string | null;
}

export interface AssignDestinationInput {
  operationId: string;
  slotId: string;
  dispatcherId: string | null;
  dispatcherName: string;
}
