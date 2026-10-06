import { ValidationError } from '../../../shared/errors/app-error';
import { YardContainerRepository } from '../infrastructure/yard-container.repository';
import { YardActor } from './yard-container.dto';
import {
  assignYardSlotSchema, confirmContainerReceptionSchema, yardUuidSchema,
} from './yard-container.validator';

const validationError = (message: string, issues: { path: PropertyKey[]; message: string }[]) => {
  const details = issues.reduce<Record<string, string[]>>((result, issue) => {
    const field = issue.path.join('.') || 'request';
    result[field] = [...(result[field] ?? []), issue.message];
    return result;
  }, {});
  return new ValidationError(message, details);
};

export class YardContainerService {
  constructor(private readonly repository = new YardContainerRepository()) {}

  async listAvailableSlots(containerId: string) {
    const parsedId = yardUuidSchema.safeParse(containerId);
    if (!parsedId.success) throw validationError('Container ID không hợp lệ.', parsedId.error.issues);
    return this.repository.listAvailableSlots(parsedId.data);
  }

  async confirmReception(containerId: string, body: unknown, actor: YardActor) {
    const parsedId = yardUuidSchema.safeParse(containerId);
    if (!parsedId.success) throw validationError('Container ID không hợp lệ.', parsedId.error.issues);
    const parsed = confirmContainerReceptionSchema.safeParse(body);
    if (!parsed.success) throw validationError('Thông tin tiếp nhận Container không hợp lệ.', parsed.error.issues);
    return this.repository.confirmReception(parsedId.data, parsed.data, actor);
  }

  async reserveSlot(containerId: string, body: unknown, actor: YardActor) {
    return this.assign(containerId, body, actor, 'reserve');
  }

  async placeContainer(containerId: string, body: unknown, actor: YardActor) {
    return this.assign(containerId, body, actor, 'place');
  }

  private async assign(containerId: string, body: unknown, actor: YardActor, action: 'reserve' | 'place') {
    const parsedId = yardUuidSchema.safeParse(containerId);
    if (!parsedId.success) throw validationError('Container ID không hợp lệ.', parsedId.error.issues);
    const parsed = assignYardSlotSchema.safeParse(body);
    if (!parsed.success) throw validationError('Vị trí lưu Container không hợp lệ.', parsed.error.issues);
    return action === 'reserve'
      ? this.repository.reserveSlot(parsedId.data, parsed.data.slotId, actor)
      : this.repository.placeContainer(parsedId.data, parsed.data.slotId, actor);
  }
}
