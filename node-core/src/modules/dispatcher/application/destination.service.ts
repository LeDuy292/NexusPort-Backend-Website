import { z } from 'zod';
import { ValidationError } from '../../../shared/errors/app-error';
import { DestinationRepository } from '../infrastructure/destination.repository';
import { DestinationAssignment, YardDestination, YARD_SLOT_STATUSES, YardSlotStatus } from './destination.dto';

const uuidSchema = z.string().uuid();

export class DestinationService {
  constructor(private readonly repository = new DestinationRepository()) {}

  async list(status?: string): Promise<YardDestination[]> {
    if (status && !YARD_SLOT_STATUSES.includes(status as YardSlotStatus)) {
      throw new ValidationError(`status phải là một trong: ${YARD_SLOT_STATUSES.join(', ')}.`);
    }
    return this.repository.findYardDestinations(status as YardSlotStatus | undefined);
  }

  async get(operationId: string): Promise<DestinationAssignment | null> {
    this.assertUuid(operationId, 'operationId');
    return this.repository.findAssignment(operationId);
  }

  async assign(
    operationId: string,
    body: unknown,
    actor: { id: string | null; name: string },
  ): Promise<DestinationAssignment> {
    this.assertUuid(operationId, 'operationId');
    const parsed = z.object({ slotId: uuidSchema }).strict().safeParse(body);
    if (!parsed.success) throw new ValidationError('slotId phải là UUID hợp lệ.');
    return this.repository.assign({
      operationId,
      slotId: parsed.data.slotId,
      dispatcherId: actor.id,
      dispatcherName: actor.name,
    });
  }

  private assertUuid(value: string, field: string): void {
    if (!uuidSchema.safeParse(value).success) throw new ValidationError(`${field} phải là UUID hợp lệ.`);
  }
}
