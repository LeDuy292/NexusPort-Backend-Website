export interface BookingEntity {
  id: string;
  name: string;
  bookingNumber?: string;
  status: string;
  description?: string;
  vehiclePlate?: string;
  vehicleId?: string;
  driverName?: string;
  driverId?: string;
  validFrom?: Date;
  validTo?: Date;
  gateType?: string;
  createdAt: Date;
  updatedAt?: Date;
}
