import { BookingEntity } from '../domain/booking.entity';

export interface CreateBookingDto {
  name: string;
  description?: string;
}

export interface BookingDto extends BookingEntity {}

export interface IBookingService {
  getAll(): Promise<BookingDto[]>;
  getById(id: string): Promise<BookingDto | null>;
  create(dto: CreateBookingDto): Promise<BookingDto>;
}
