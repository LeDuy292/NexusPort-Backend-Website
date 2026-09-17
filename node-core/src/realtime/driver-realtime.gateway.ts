import { Server as HttpServer } from 'http';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { Server } from 'socket.io';
import { z } from 'zod';
import { RabbitMQClient } from '../infrastructure/clients/rabbitmq';
import { logger } from '../shared/utils/logger';

const queueName = 'yard.operation.completed';
const dispatcherQueueName = 'dispatcher.status.updated';
export const yardOperationCompletedSchema = z.object({
  eventId: z.string().uuid(),
  operationId: z.string().uuid(),
  containerId: z.string().uuid(),
  driverId: z.string().uuid(),
  operationStatus: z.literal('Completed'),
  completedAt: z.string().datetime(),
});

type YardOperationCompletedEvent = z.infer<typeof yardOperationCompletedSchema>;

export const dispatcherStatusUpdatedSchema = z.object({
  eventId: z.string().uuid(),
  domain: z.enum(['booking', 'gate', 'vehicle', 'container', 'yard', 'equipment', 'driver', 'gate-out']),
  entityId: z.string().min(1),
  status: z.string().min(1),
  occurredAt: z.string().datetime(),
  bookingId: z.string().uuid().optional(),
  containerId: z.string().uuid().optional(),
  vehicleId: z.string().uuid().optional(),
  driverId: z.string().uuid().optional(),
  label: z.string().max(200).optional(),
  metadata: z.record(z.unknown()).optional(),
});

export type DispatcherStatusUpdatedEvent = z.infer<typeof dispatcherStatusUpdatedSchema>;

export class DriverRealtimeGateway {
  private readonly io: Server;

  constructor(server: HttpServer) {
    this.io = new Server(server, {
      cors: { origin: process.env.CORS_ORIGIN?.split(',') || true, methods: ['GET', 'POST'] },
    });
    this.io.use((socket, next) => {
      const token = socket.handshake.auth?.token;
      if (!token || typeof token !== 'string') return next(new Error('Authentication required.'));
      try {
        const decoded = jwt.verify(token, process.env.JWT_SECRET || 'NexusPort_Super_Secret_Key_For_Jwt_Authentication_2026!') as JwtPayload;
        const driverId = decoded.sub || decoded.id || decoded['http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier'];
        const role = decoded.role || decoded['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'];
        const normalizedRole = typeof role === 'string' ? role.toLowerCase() : '';
        if (!['driver', 'dispatcher', 'administrator'].includes(normalizedRole)) return next(new Error('Only driver or dispatcher sessions may use this realtime channel.'));
        if (typeof driverId !== 'string') return next(new Error('A driver identity is required.'));
        socket.data.driverId = driverId;
        socket.data.role = normalizedRole;
        next();
      } catch {
        next(new Error('Invalid or expired realtime token.'));
      }
    });
    this.io.on('connection', socket => {
      const driverId = socket.data.driverId as string;
      const role = socket.data.role as string;
      if (role === 'driver') {
        socket.join(`driver:${driverId}`);
        logger.info(`[Realtime] Driver ${driverId} connected.`);
      } else {
        socket.join('dispatcher:all');
        logger.info(`[Realtime] Dispatcher ${driverId} connected.`);
      }
      socket.on('disconnect', reason => logger.info(`[Realtime] Realtime client ${driverId} disconnected: ${reason}`));
    });
  }

  public async start(): Promise<void> {
    const broker = RabbitMQClient.getInstance();
    await broker.consume(queueName, async message => {
      const parsed = yardOperationCompletedSchema.safeParse(JSON.parse(message.content.toString('utf8')));
      if (!parsed.success) {
        logger.error('[Realtime] Rejected malformed yard completion event:', parsed.error.flatten());
        throw new Error('Malformed yard operation completion event.');
      }
      this.emitToDriver(parsed.data);
      this.emitToDispatcher({
        eventId: parsed.data.eventId,
        domain: 'yard',
        entityId: parsed.data.operationId,
        status: parsed.data.operationStatus,
        occurredAt: parsed.data.completedAt,
        containerId: parsed.data.containerId,
        driverId: parsed.data.driverId,
        label: `Yard operation ${parsed.data.operationId}`,
        metadata: { sourceEvent: queueName },
      });
    });
    await broker.consume(dispatcherQueueName, async message => {
      const parsed = dispatcherStatusUpdatedSchema.safeParse(JSON.parse(message.content.toString('utf8')));
      if (!parsed.success) {
        logger.error('[Realtime] Rejected malformed dispatcher status event:', parsed.error.flatten());
        throw new Error('Malformed dispatcher status event.');
      }
      this.emitToDispatcher(parsed.data);
    });
  }

  private emitToDriver(event: YardOperationCompletedEvent): void {
    this.io.to(`driver:${event.driverId}`).emit(queueName, event);
    logger.info(`[Realtime] Emitted ${queueName} ${event.eventId} to driver ${event.driverId}.`);
  }

  private emitToDispatcher(event: DispatcherStatusUpdatedEvent): void {
    this.io.to('dispatcher:all').emit(dispatcherQueueName, event);
    logger.info(`[Realtime] Emitted ${dispatcherQueueName} ${event.domain}:${event.entityId} to dispatcher dashboard.`);
  }
}
