import { logger } from '../../shared/utils/logger';

export interface EmailPayload {
  to: string;
  subject: string;
  body: string;
}

export const sendEmail = async (payload: EmailPayload): Promise<void> => {
  logger.info(`[EmailService] Sending email to ${payload.to}: ${payload.subject}`);
};
