import { BookingEntity } from '../domain/booking.entity';

export class BookingRepository {
  private items: BookingEntity[] = [];

  async findAll(): Promise<BookingEntity[]> {
    return [...this.items];
  }

  async findById(id: string): Promise<BookingEntity | null> {
    return this.items.find(i => i.id === id) || null;
  }

  async create(entity: Omit<BookingEntity, 'id' | 'createdAt'>): Promise<BookingEntity> {
    const item: BookingEntity = {
      ...entity,
      id: Math.random().toString(36).substring(2, 9),
      createdAt: new Date()
    };
    this.items.push(item);
    return item;
  }
}
