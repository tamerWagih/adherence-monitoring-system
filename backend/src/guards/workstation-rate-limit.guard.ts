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
      // Check if Redis is connected
      if (this.redis.status !== 'ready' && this.redis.status !== 'connecting') {
        // Redis not connected - fail open (allow request)
        console.warn(`Rate limit guard: Redis not ready (status: ${this.redis.status}), allowing request`);
        return true;
      }

      // Add timeout to prevent hanging (2 second timeout for rate limit check)
      const timeoutPromise = new Promise<never>((_, reject) => {
        setTimeout(() => reject(new Error('Redis operation timeout')), 2000);
      });

      // Use INCR instead of GET+INCR to ensure atomicity
      // INCR returns the new count after incrementing
      const newCount = await Promise.race([
        this.redis.incr(key),
        timeoutPromise,
      ]).catch((err) => {
        console.warn(`Rate limit guard: Redis INCR timeout/error for ${key}: ${err.message}`);
        return null;
      }) as number | null;
      
      // If timeout or error, fail open
      if (newCount === null) {
        return true;
      }

      // Set TTL on first request (when count is 1)
      if (newCount === 1) {
        // Fire and forget - set TTL, don't wait
        this.redis.expire(key, this.ttl).catch(() => {
          // Ignore errors - TTL is best effort
        });
      }

      // Check if limit exceeded (after increment)
      if (newCount > this.limit) {
        // Rate limit exceeded - get remaining TTL
        const remainingTtl = await Promise.race([
          this.redis.ttl(key),
          timeoutPromise,
        ]).catch(() => this.ttl) as number;
        
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

      return true;
    } catch (error) {
      // If Redis error, log and allow request (fail open)
      if (error instanceof HttpException) {
        throw error;
      }

      // Fail open - allow request if Redis is unavailable or timeout
      console.warn(`Rate limit guard: Error for ${key}, allowing request: ${error.message}`);
      return true;
    }
  }
}

