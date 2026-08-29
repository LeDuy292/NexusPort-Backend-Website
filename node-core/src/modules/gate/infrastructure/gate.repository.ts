import { GateEntity } from '../domain/gate.entity';

export class GateRepository {
  private items: GateEntity[] = [];

  async findAll(): Promise<GateEntity[]> {
    return [...this.items];
  }

  async findById(id: string): Promise<GateEntity | null> {
    return this.items.find(i => i.id === id) || null;
  }

  async create(entity: Omit<GateEntity, 'id' | 'createdAt'>): Promise<GateEntity> {
    const item: GateEntity = {
      ...entity,
      id: Math.random().toString(36).substring(2, 9),
      createdAt: new Date()
    };
    this.items.push(item);
    return item;
  }
}
