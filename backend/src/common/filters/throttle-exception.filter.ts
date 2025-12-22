import {
  ExceptionFilter,
  Catch,
  ArgumentsHost,
  HttpStatus,
} from '@nestjs/common';
import { Response } from 'express';
import { HttpException } from '@nestjs/common';

/**
 * ThrottleExceptionFilter
 * 
 * Exception filter to add Retry-After header to rate limit exceptions.
 * Ensures Retry-After header is always present in 429 responses.
 */
@Catch(HttpException)
export class ThrottleExceptionFilter implements ExceptionFilter {
  catch(exception: HttpException, host: ArgumentsHost) {
    const ctx = host.switchToHttp();
    const response = ctx.getResponse<Response>();
    const status = exception.getStatus();

    // Only handle 429 Too Many Requests
    if (status === HttpStatus.TOO_MANY_REQUESTS) {
      const exceptionResponse = exception.getResponse();
      const retryAfter =
        typeof exceptionResponse === 'object' && exceptionResponse !== null
          ? (exceptionResponse as any).retryAfter || 60
          : 60;

      response.setHeader('Retry-After', retryAfter);
    }

    // Let NestJS handle the exception normally
    response.status(status).json(exception.getResponse());
  }
}



