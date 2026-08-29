export interface GateEntity {
  id: string;
  name: string;
  status: string;
  description?: string;
  createdAt: Date;
  updatedAt?: Date;
}
