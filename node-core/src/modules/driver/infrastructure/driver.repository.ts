import { DriverEntity } from '../domain/driver.entity';

export class DriverRepository {
  private items: DriverEntity[] = [];

  async findAll(): Promise<DriverEntity[]> {
    return [...this.items];
  }

  async findById(id: string): Promise<DriverEntity | null> {
    return this.items.find(i => i.id === id) || null;
  }

  async create(entity: Omit<DriverEntity, 'id' | 'createdAt'>): Promise<DriverEntity> {
    const item: DriverEntity = {
      ...entity,
      id: Math.random().toString(36).substring(2, 9),
      createdAt: new Date()
    };
    this.items.push(item);
    return item;
  }
}
