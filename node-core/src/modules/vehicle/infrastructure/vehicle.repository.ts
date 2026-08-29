import { VehicleEntity } from '../domain/vehicle.entity';

export class VehicleRepository {
  private items: VehicleEntity[] = [];

  async findAll(): Promise<VehicleEntity[]> {
    return [...this.items];
  }

  async findById(id: string): Promise<VehicleEntity | null> {
    return this.items.find(i => i.id === id) || null;
  }

  async create(entity: Omit<VehicleEntity, 'id' | 'createdAt'>): Promise<VehicleEntity> {
    const item: VehicleEntity = {
      ...entity,
      id: Math.random().toString(36).substring(2, 9),
      createdAt: new Date()
    };
    this.items.push(item);
    return item;
  }
}
