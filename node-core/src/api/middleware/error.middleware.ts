import { Request, Response, NextFunction } from 'express';
import { AppError } from '../../shared/errors/app-error';
import { sendError } from '../../shared/utils/response';
import { logger } from '../../shared/utils/logger';
import multer from 'multer';

export const errorHandler = (err: Error, req: Request, res: Response, _next: NextFunction) => {
  if ((err as Error & { type?: string }).type === 'entity.too.large') {
    return sendError(res, 'Dữ liệu yêu cầu quá lớn. Vui lòng giảm số container hoặc độ dài mô tả hàng hóa.', 413, 'PAYLOAD_TOO_LARGE');
  }
  if ((err as Error & { code?: string }).code === '53300') {
    logger.warn(`Database connection capacity reached for ${req.method} ${req.path}.`);
    res.setHeader('Retry-After', '3');
    return sendError(res, 'Dịch vụ dữ liệu đang bận. Vui lòng thử lại sau ít giây.', 503, 'DB_CONNECTION_LIMIT');
  }
  logger.error(`Error processing ${req.method} ${req.path}:`, err);

  if (err instanceof AppError) {
    const details = 'errors' in err ? err.errors : undefined;
    return sendError(res, err.message, err.statusCode, err.errorCode, details);
  }

  if (err instanceof multer.MulterError) {
    return sendError(res, err.code === 'LIMIT_FILE_SIZE'
      ? 'File Excel không được vượt quá 5 MB.'
      : 'File import không hợp lệ.', 422, 'INVALID_IMPORT_FILE');
  }

  return sendError(res, 'Đã xảy ra lỗi hệ thống. Vui lòng thử lại sau.', 500, 'INTERNAL_SERVER_ERROR');
};

export const requestLogger = (req: Request, res: Response, next: NextFunction) => {
  const start = Date.now();
  res.on('finish', () => {
    const duration = Date.now() - start;
    logger.info(`${req.method} ${req.originalUrl} [${res.statusCode}] - ${duration}ms`);
  });
  next();
};

export const authMiddleware = (req: Request, res: Response, next: NextFunction) => {
  const authHeader = req.headers.authorization;
  if (!authHeader || !authHeader.startsWith('Bearer ')) {
    return next();
  }
  // Optional token verification hook
  next();
};
