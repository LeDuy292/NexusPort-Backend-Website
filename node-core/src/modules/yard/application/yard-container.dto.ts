export type ContainerReceptionCondition = 'sound' | 'damaged';

export interface ConfirmContainerReceptionDto {
  containerNumber: string;
  sealNumber: string;
  condition: ContainerReceptionCondition;
  conditionNotes?: string | null;
}

export interface YardSlotDto {
  slotId: string;
  blockId: string;
  blockCode: string;
  blockName: string;
  zone: string | null;
  bay: number;
  row: number;
  tier: number;
  status: 'empty' | 'reserved' | 'occupied' | 'maintenance';
  maxWeightKg: number | null;
  hasReeferPlug: boolean;
  reservedForContainer: boolean;
}

export interface YardActor {
  id: string;
  ipAddress?: string | null;
}
