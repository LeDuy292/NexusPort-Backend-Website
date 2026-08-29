export class AppError extends Error {
  public readonly statusCode: number;
  public readonly errorCode: string;
  public readonly isOperational: boolean;

  constructor(message: string, statusCode = 400, errorCode = 'BAD_REQUEST', isOperational = true) {
    super(message);
    this.statusCode = statusCode;
    this.errorCode = errorCode;
    this.isOperational = isOperational;

    Object.setPrototypeOf(this, new.target.prototype);
    if (typeof (Error as unknown as { captureStackTrace?: Function }).captureStackTrace === 'function') {
      (Error as unknown as { captureStackTrace: Function }).captureStackTrace(this, this.constructor);
    }
  }
}

export class NotFoundError extends AppError {
  constructor(resource: string, identifier?: string | number) {
    const msg = identifier ? `${resource} with ID '${identifier}' was not found.` : `${resource} was not found.`;
    super(msg, 404, 'NOT_FOUND');
  }
}

export class ValidationError extends AppError {
  public readonly errors?: Record<string, string[]>;

  constructor(message = 'Validation failed.', errors?: Record<string, string[]>) {
    super(message, 422, 'VALIDATION_ERROR');
    this.errors = errors;
  }
}

export class UnauthorizedError extends AppError {
  constructor(message = 'Unauthorized access.') {
    super(message, 401, 'UNAUTHORIZED');
  }
}

export class ForbiddenError extends AppError {
  constructor(message = 'Access forbidden.') {
    super(message, 403, 'FORBIDDEN');
  }
}
