export type ContainerIntakeSource = 'port_vessel' | 'transport_company';
export interface ContainerIntakeRow {
    containerNumber: string;
    containerTypeCode: string;
    sealNumber?: string | null;
    sourceType: 'transport_company';
    loadStatus: 'full' | 'empty' | 'unknown';
    cargoType: import('../domain/container.entity').CargoType;
    grossWeightKg?: number | null;
    requiredPowerKw?: number | null;
    customerName?: string | null;
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
    containerId: string;
    containerNumber: string;
    containerTypeCode: string;
    containerTypeDescription: string | null;
    sealNumber: string | null;
    loadStatus: 'full' | 'empty' | 'unknown';
    cargoType: string;
    cargoTypeName: string;
    grossWeightKg: number | null;
    requiredPowerKw: number | null;
    customerName: string | null;
    createdAt: Date;
    updatedAt: Date;
}
