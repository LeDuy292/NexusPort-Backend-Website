export interface ReadyBookingContainer {
  id: string;
  containerNumber: string;
  status: string;
}

export interface ReadyBookingResource {
  id: string;
  name: string;
  status: string;
}

export interface ReadyBookingDto {
  id: string;
  bookingCode: string;
  bookingType: string;
  status: string;
  appointmentStart: Date;
  appointmentEnd: Date;
  driver: ReadyBookingResource | null;
  vehicle: ReadyBookingResource | null;
  containers: ReadyBookingContainer[];
}
