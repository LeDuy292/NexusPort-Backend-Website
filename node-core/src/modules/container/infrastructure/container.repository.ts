import { ContainerEntity } from '../domain/container.entity';

export class ContainerRepository {
  private items: ContainerEntity[] = [];

  async findAll(): Promise<ContainerEntity[]> {
    return [...this.items];
  }

  async findById(id: string): Promise<ContainerEntity | null> {
    return this.items.find(i => i.id === id) || null;
  }

  async create(entity: Omit<ContainerEntity, 'id' | 'createdAt'>): Promise<ContainerEntity> {
    const item: ContainerEntity = {
      ...entity,
      id: Math.random().toString(36).substring(2, 9),
      createdAt: new Date()
    };
    this.items.push(item);
    return item;
  }
}
