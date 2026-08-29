import { EquipmentEntity } from '../domain/equipment.entity';

export class EquipmentRepository {
  private items: EquipmentEntity[] = [];

  async findAll(): Promise<EquipmentEntity[]> {
    return [...this.items];
  }

  async findById(id: string): Promise<EquipmentEntity | null> {
    return this.items.find(i => i.id === id) || null;
  }

  async create(entity: Omit<EquipmentEntity, 'id' | 'createdAt'>): Promise<EquipmentEntity> {
    const item: EquipmentEntity = {
      ...entity,
      id: Math.random().toString(36).substring(2, 9),
      createdAt: new Date()
    };
    this.items.push(item);
    return item;
  }
}
