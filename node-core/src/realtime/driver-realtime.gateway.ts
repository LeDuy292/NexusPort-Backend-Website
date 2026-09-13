import { Server as HttpServer } from 'http';
import jwt, { JwtPayload } from 'jsonwebtoken';
import { Server } from 'socket.io';
import { z } from 'zod';
import { RabbitMQClient } from '../infrastructure/clients/rabbitmq';
import { logger } from '../shared/utils/logger';

const queueName = 'yard.operation.completed';
export const yardOperationCompletedSchema = z.object({
  eventId: z.string().uuid(),
  operationId: z.string().uuid(),
  containerId: z.string().uuid(),
  driverId: z.string().uuid(),
  operationStatus: z.literal('Completed'),
  completedAt: z.string().datetime(),
});

type YardOperationCompletedEvent = z.infer<typeof yardOperationCompletedSchema>;

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
        if (role !== 'Driver' && role !== 'Administrator') return next(new Error('Only driver sessions may use this realtime channel.'));
        if (typeof driverId !== 'string') return next(new Error('A driver identity is required.'));
        socket.data.driverId = driverId;
        next();
      } catch {
        next(new Error('Invalid or expired realtime token.'));
      }
    });
    this.io.on('connection', socket => {
      const driverId = socket.data.driverId as string;
      socket.join(`driver:${driverId}`);
      logger.info(`[Realtime] Driver ${driverId} connected.`);
      socket.on('disconnect', reason => logger.info(`[Realtime] Driver ${driverId} disconnected: ${reason}`));
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
    });
  }

  private emitToDriver(event: YardOperationCompletedEvent): void {
    this.io.to(`driver:${event.driverId}`).emit(queueName, event);
    logger.info(`[Realtime] Emitted ${queueName} ${event.eventId} to driver ${event.driverId}.`);
  }
}
