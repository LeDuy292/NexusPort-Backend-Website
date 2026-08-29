import { DispatcherEntity } from '../domain/dispatcher.entity';

export class DispatcherRepository {
  private items: DispatcherEntity[] = [];

  async findAll(): Promise<DispatcherEntity[]> {
    return [...this.items];
  }

  async findById(id: string): Promise<DispatcherEntity | null> {
    return this.items.find(i => i.id === id) || null;
  }

  async create(entity: Omit<DispatcherEntity, 'id' | 'createdAt'>): Promise<DispatcherEntity> {
    const item: DispatcherEntity = {
      ...entity,
      id: Math.random().toString(36).substring(2, 9),
      createdAt: new Date()
    };
    this.items.push(item);
    return item;
  }
}
