import { IdentityEntity } from '../domain/identity.entity';

export class IdentityRepository {
  private items: IdentityEntity[] = [];

  async findAll(): Promise<IdentityEntity[]> {
    return [...this.items];
  }

  async findById(id: string): Promise<IdentityEntity | null> {
    return this.items.find(i => i.id === id) || null;
  }

  async create(entity: Omit<IdentityEntity, 'id' | 'createdAt'>): Promise<IdentityEntity> {
    const item: IdentityEntity = {
      ...entity,
      id: Math.random().toString(36).substring(2, 9),
      createdAt: new Date()
    };
    this.items.push(item);
    return item;
  }
}
