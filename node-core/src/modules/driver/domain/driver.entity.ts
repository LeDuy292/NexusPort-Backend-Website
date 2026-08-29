export interface DriverEntity {
  id: string;
  name: string;
  status: string;
  description?: string;
  createdAt: Date;
  updatedAt?: Date;
}
