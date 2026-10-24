export interface GateVerificationRecordEntity {
  id: string;
  verificationCode: string;
  gateCode: string;
  laneCode?: string;
  verificationType: string;
  verificationStatus: string; // 'PASS' | 'FAIL' | 'MANUAL_REVIEW'
  failureReason?: string;
  verificationTime: Date;

  detectedPlate: string;
  plateConfidence?: number;
  cameraId?: string;
  ocrRawData?: string;

  bookingId?: string;
  bookingNumber?: string;
  vehicleId?: string;
  vehiclePlate?: string;
  driverId?: string;
  driverName?: string;

  vehiclePlateImageUrl?: string;
  overviewImageUrl?: string;

  notes?: string;
  processedBy?: string;
  createdAt: Date;
  updatedAt?: Date;
}
