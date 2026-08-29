import { Response } from 'express';
import { ApiResponse } from '../types';

export const sendSuccess = <T>(res: Response, data: T, statusCode = 200) => {
  const response: ApiResponse<T> = {
    success: true,
    data,
    timestamp: new Date().toISOString(),
  };
  return res.status(statusCode).json(response);
};

export const sendCreated = <T>(res: Response, data: T) => {
  return sendSuccess(res, data, 201);
};

export const sendError = (res: Response, message: string, statusCode = 400, code = 'ERROR', details?: unknown) => {
  const response: ApiResponse = {
    success: false,
    error: {
      code,
      message,
      details,
    },
    timestamp: new Date().toISOString(),
  };
  return res.status(statusCode).json(response);
};
