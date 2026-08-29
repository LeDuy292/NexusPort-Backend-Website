import { YardEntity } from '../domain/yard.entity';

export class YardRepository {
  private items: YardEntity[] = [];

  async findAll(): Promise<YardEntity[]> {
    return [...this.items];
  }

  async findById(id: string): Promise<YardEntity | null> {
    return this.items.find(i => i.id === id) || null;
  }

  async create(entity: Omit<YardEntity, 'id' | 'createdAt'>): Promise<YardEntity> {
    const item: YardEntity = {
      ...entity,
      id: Math.random().toString(36).substring(2, 9),
      createdAt: new Date()
    };
    this.items.push(item);
    return item;
  }
}
