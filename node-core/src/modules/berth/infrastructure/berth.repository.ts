import { BerthEntity } from '../domain/berth.entity';

export class BerthRepository {
  private items: BerthEntity[] = [];

  async findAll(): Promise<BerthEntity[]> {
    return [...this.items];
  }

  async findById(id: string): Promise<BerthEntity | null> {
    return this.items.find(i => i.id === id) || null;
  }

  async create(entity: Omit<BerthEntity, 'id' | 'createdAt'>): Promise<BerthEntity> {
    const item: BerthEntity = {
      ...entity,
      id: Math.random().toString(36).substring(2, 9),
      createdAt: new Date()
    };
    this.items.push(item);
    return item;
  }
}
