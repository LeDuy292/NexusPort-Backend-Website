export interface VehicleEntity {
  id: string;
  name: string;
  status: string;
  description?: string;
  createdAt: Date;
  updatedAt?: Date;
}
