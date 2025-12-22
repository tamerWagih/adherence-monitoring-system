import {
  Injectable,
  CanActivate,
  ExecutionContext,
  HttpException,
  HttpStatus,
  Inject,
} from '@nestjs/common';
import Redis from 'ioredis';

/**
 * WorkstationRateLimitGuard
 * 
 * Per-workstation rate limiting guard with Retry-After header support.
 * Limits each workstation to 10 requests per minute.
 * 
 * Uses Redis for distributed rate limiting across multiple backend instances.
 * 
 * Rate Limit: 10 requests per minute per workstation
 */
@Injectable()
export class WorkstationRateLimitGuard implements CanActivate {
  private readonly limit = 10; // requests per minute
  private readonly ttl = 60; // seconds (1 minute)

  constructor(@Inject('REDIS_CLIENT') private readonly redis: Redis) {}

  async canActivate(context: ExecutionContext): Promise<boolean> {
    const request = context.switchToHttp().getRequest();
    const response = context.switchToHttp().getResponse();

    // Get workstation ID from request (set by WorkstationAuthGuard)
    const workstationId = request.workstation?.workstationId;

    if (!workstationId) {
      // If workstation not authenticated, allow through (WorkstationAuthGuard will handle auth)
      return true;
    }

    // Key format: workstation:rate-limit:{workstationId}
    const key = `workstation:rate-limit:${workstationId}`;

    try {
      // Get current count
      const count = await this.redis.get(key);
      const currentCount = count ? parseInt(count, 10) : 0;

      if (currentCount >= this.limit) {
        // Rate limit exceeded - get remaining TTL
        const remainingTtl = await this.redis.ttl(key);
        const retryAfter = remainingTtl > 0 ? remainingTtl : this.ttl;

        // Set Retry-After header
        response.setHeader('Retry-After', retryAfter);

        throw new HttpException(
          {
            statusCode: HttpStatus.TOO_MANY_REQUESTS,
            message: `Rate limit exceeded. Maximum ${this.limit} requests per minute per workstation. Please retry after ${retryAfter} seconds.`,
            retryAfter,
          },
          HttpStatus.TOO_MANY_REQUESTS,
        );
      }

      // Increment counter and set TTL
      const pipeline = this.redis.pipeline();
      pipeline.incr(key);
      pipeline.expire(key, this.ttl);
      await pipeline.exec();

      return true;
    } catch (error) {
      // If Redis error, log and allow request (fail open)
      if (error instanceof HttpException) {
        throw error;
      }

      console.error('Redis rate limit error:', error);
      // Fail open - allow request if Redis is unavailable
      return true;
    }
  }
}

