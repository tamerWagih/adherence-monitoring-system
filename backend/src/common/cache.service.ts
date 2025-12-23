import { Injectable, Logger, Inject } from '@nestjs/common';
import Redis from 'ioredis';
import * as crypto from 'crypto';

/**
 * CacheService
 * 
 * Provides Redis-based caching for query results.
 * Implements fail-open behavior: if Redis fails, returns null (fallback to DB).
 * 
 * Cache Key Format:
 * - adherence:{service}:{endpoint}:{hash-of-params}
 * 
 * Examples:
 * - adherence:summaries:list:abc123def456
 * - adherence:realtime:dept:IT:xyz789
 * - adherence:reports:daily:2025-12-27:dept:IT
 */
@Injectable()
export class CacheService {
  private readonly logger = new Logger(CacheService.name);
  private readonly KEY_PREFIX = 'adherence';

  constructor(@Inject('REDIS_CLIENT') private readonly redis: Redis) {}

  /**
   * Generate cache key from service, endpoint, and params
   */
  private generateKey(
    service: string,
    endpoint: string,
    params: Record<string, any>,
  ): string {
    // Sort params for consistent key generation
    const sortedParams = Object.keys(params)
      .sort()
      .reduce((acc, key) => {
        acc[key] = params[key];
        return acc;
      }, {} as Record<string, any>);

    // Create hash of params
    const paramsStr = JSON.stringify(sortedParams);
    const hash = crypto.createHash('md5').update(paramsStr).digest('hex').substring(0, 12);

    return `${this.KEY_PREFIX}:${service}:${endpoint}:${hash}`;
  }

  /**
   * Get value from cache
   * Returns null if cache miss or Redis error (fail-open)
   */
  async get<T>(key: string): Promise<T | null> {
    try {
      // Check if Redis is connected
      if (this.redis.status !== 'ready' && this.redis.status !== 'connecting') {
        this.logger.debug(`Cache miss (Redis not ready): ${key}`);
        return null;
      }

      // Add timeout to prevent hanging (2 second timeout)
      const timeoutPromise = new Promise<never>((_, reject) => {
        setTimeout(() => reject(new Error('Redis GET timeout')), 2000);
      });

      const cached = await Promise.race([
        this.redis.get(key),
        timeoutPromise,
      ]).catch((err) => {
        this.logger.debug(`Cache GET error for ${key}: ${err.message}`);
        return null;
      });

      if (!cached) {
        return null;
      }

      try {
        return JSON.parse(cached) as T;
      } catch (parseError) {
        this.logger.warn(`Cache parse error for ${key}: ${parseError}`);
        return null;
      }
    } catch (error) {
      this.logger.debug(`Cache GET failed for ${key}: ${error instanceof Error ? error.message : String(error)}`);
      return null; // Fail-open: return null on error
    }
  }

  /**
   * Set value in cache with TTL
   * Silently fails if Redis error (fail-open)
   */
  async set(
    key: string,
    value: any,
    ttlSeconds: number,
  ): Promise<void> {
    try {
      // Check if Redis is connected
      if (this.redis.status !== 'ready' && this.redis.status !== 'connecting') {
        this.logger.debug(`Cache SET skipped (Redis not ready): ${key}`);
        return;
      }

      const serialized = JSON.stringify(value);

      // Add timeout to prevent hanging (2 second timeout)
      const timeoutPromise = new Promise<never>((_, reject) => {
        setTimeout(() => reject(new Error('Redis SET timeout')), 2000);
      });

      await Promise.race([
        this.redis.setex(key, ttlSeconds, serialized),
        timeoutPromise,
      ]).catch((err) => {
        this.logger.debug(`Cache SET error for ${key}: ${err.message}`);
        // Fail-open: silently ignore errors
      });
    } catch (error) {
      this.logger.debug(`Cache SET failed for ${key}: ${error instanceof Error ? error.message : String(error)}`);
      // Fail-open: silently ignore errors
    }
  }

  /**
   * Delete a specific cache key
   * Silently fails if Redis error (fail-open)
   */
  async delete(key: string): Promise<void> {
    try {
      if (this.redis.status !== 'ready' && this.redis.status !== 'connecting') {
        return;
      }

      const timeoutPromise = new Promise<never>((_, reject) => {
        setTimeout(() => reject(new Error('Redis DEL timeout')), 2000);
      });

      await Promise.race([
        this.redis.del(key),
        timeoutPromise,
      ]).catch((err) => {
        this.logger.debug(`Cache DELETE error for ${key}: ${err.message}`);
      });
    } catch (error) {
      this.logger.debug(`Cache DELETE failed for ${key}: ${error instanceof Error ? error.message : String(error)}`);
    }
  }

  /**
   * Invalidate all cache keys matching a pattern
   * Uses SCAN to find matching keys, then deletes them
   * Silently fails if Redis error (fail-open)
   */
  async invalidatePattern(pattern: string): Promise<number> {
    try {
      if (this.redis.status !== 'ready' && this.redis.status !== 'connecting') {
        return 0;
      }

      const fullPattern = `${this.KEY_PREFIX}:${pattern}`;
      const keys: string[] = [];
      let cursor = '0';

      // Use SCAN to find all matching keys (non-blocking)
      do {
        const timeoutPromise = new Promise<never>((_, reject) => {
          setTimeout(() => reject(new Error('Redis SCAN timeout')), 3000);
        });

        const result = await Promise.race([
          this.redis.scan(cursor, 'MATCH', fullPattern, 'COUNT', 100),
          timeoutPromise,
        ]).catch((err) => {
          this.logger.debug(`Cache SCAN error for pattern ${pattern}: ${err.message}`);
          return ['0', []] as [string, string[]];
        });

        cursor = result[0];
        keys.push(...result[1]);
      } while (cursor !== '0');

      if (keys.length === 0) {
        return 0;
      }

      // Delete all matching keys
      const timeoutPromise = new Promise<never>((_, reject) => {
        setTimeout(() => reject(new Error('Redis DEL timeout')), 5000);
      });

      const deleted = await Promise.race([
        this.redis.del(...keys),
        timeoutPromise,
      ]).catch((err) => {
        this.logger.debug(`Cache DEL pattern error for ${pattern}: ${err.message}`);
        return 0;
      });

      this.logger.log(`Cache invalidated ${deleted} keys matching pattern: ${pattern}`);
      return deleted;
    } catch (error) {
      this.logger.debug(`Cache invalidatePattern failed for ${pattern}: ${error instanceof Error ? error.message : String(error)}`);
      return 0;
    }
  }

  /**
   * Get or set cache value (helper method)
   * If cache miss, calls fetchFn and caches the result
   */
  async getOrSet<T>(
    service: string,
    endpoint: string,
    params: Record<string, any>,
    fetchFn: () => Promise<T>,
    ttlSeconds: number,
  ): Promise<T> {
    const key = this.generateKey(service, endpoint, params);

    // Try to get from cache
    const cached = await this.get<T>(key);
    if (cached !== null) {
      this.logger.debug(`Cache HIT: ${key}`);
      return cached;
    }

    // Cache miss - fetch from source
    this.logger.debug(`Cache MISS: ${key}`);
    const value = await fetchFn();

    // Cache the result (fire and forget)
    this.set(key, value, ttlSeconds).catch(() => {
      // Ignore cache set errors
    });

    return value;
  }
}

