import { VesselEntity } from '../domain/vessel.entity';

export class VesselRepository {
  private items: VesselEntity[] = [];

  async findAll(): Promise<VesselEntity[]> {
    return [...this.items];
  }

  async findById(id: string): Promise<VesselEntity | null> {
    return this.items.find(i => i.id === id) || null;
  }

  async create(entity: Omit<VesselEntity, 'id' | 'createdAt'>): Promise<VesselEntity> {
    const item: VesselEntity = {
      ...entity,
      id: Math.random().toString(36).substring(2, 9),
      createdAt: new Date()
    };
    this.items.push(item);
    return item;
  }
}
