import { Module, Global } from '@nestjs/common';
import { ConfigModule, ConfigService } from '@nestjs/config';
import Redis from 'ioredis';
import { CacheService } from './cache.service';

/**
 * RedisModule
 * 
 * Provides Redis connection for throttler storage and BullMQ queues.
 * Global module - available throughout the application.
 */
@Global()
@Module({
  imports: [ConfigModule],
  providers: [
    {
      provide: 'REDIS_CLIENT',
      useFactory: (configService: ConfigService) => {
        const redisHost = configService.get<string>('REDIS_HOST') || 'localhost';
        const redisPort = configService.get<number>('REDIS_PORT') || 6379;
        const redisPassword = configService.get<string>('REDIS_PASSWORD');
        const redisUrl = configService.get<string>('REDIS_URL');

        // Debug logging
        console.log('Redis Configuration:', {
          REDIS_HOST: redisHost,
          REDIS_PORT: redisPort,
          REDIS_PASSWORD: redisPassword ? '***' : 'not set',
          REDIS_URL: redisUrl || 'not set',
        });

        // Build connection options
        let connectionOptions: any;
        
        if (redisUrl) {
          // If REDIS_URL is provided, check if it includes password
          // Format: redis://[:password@]host[:port]
          // Check if URL has password (contains @ after redis://)
          const hasPasswordInUrl = redisUrl.match(/^redis:\/\/[^:]+:[^@]+@/);
          
          if (redisPassword && !hasPasswordInUrl) {
            // REDIS_URL doesn't include password, add it
            // Parse URL: redis://host:port -> redis://:password@host:port
            const urlMatch = redisUrl.match(/^redis:\/\/([^:]+)(?::(\d+))?$/);
            if (urlMatch) {
              const host = urlMatch[1];
              const port = urlMatch[2] || redisPort;
              // URL encode password in case it contains special characters
              connectionOptions = `redis://:${encodeURIComponent(redisPassword)}@${host}:${port}`;
            } else {
              // Fallback: use REDIS_URL as-is and add password as option
              connectionOptions = {
                host: redisHost,
                port: redisPort,
                password: redisPassword,
              };
            }
          } else {
            // REDIS_URL already includes password or no password needed
            connectionOptions = redisUrl;
          }
        } else {
          // Construct from individual settings
          connectionOptions = {
            host: redisHost,
            port: redisPort,
            ...(redisPassword && { password: redisPassword }),
          };
        }

        // Common options for all connections
        const commonOptions = {
          retryStrategy: (times: number) => {
            // Stop retrying after 3 attempts (return null)
            if (times > 3) {
              return null; // Stop retrying
            }
            const delay = Math.min(times * 50, 2000);
            return delay;
          },
          maxRetriesPerRequest: 1, // Retry once if command fails
          enableReadyCheck: false, // Disable ready check to avoid blocking
          lazyConnect: false, // Connect immediately (not lazy)
          connectTimeout: 5000, // 5 second connection timeout
          commandTimeout: 3000, // 3 second command timeout
          enableOfflineQueue: true, // Queue commands when disconnected
        };

        const client = typeof connectionOptions === 'string'
          ? new Redis(connectionOptions, commonOptions)
          : new Redis({ ...connectionOptions, ...commonOptions });

        client.on('error', (err) => {
          console.error('Redis Client Error:', err.message);
        });

        client.on('connect', () => {
          console.log(`Redis Client Connected to ${redisHost}:${redisPort}`);
        });

        client.on('ready', () => {
          console.log('Redis Client Ready');
        });

        client.on('close', () => {
          console.warn('Redis Client Connection Closed');
        });

        client.on('reconnecting', () => {
          console.log('Redis Client Reconnecting...');
        });

        return client;
      },
      inject: [ConfigService],
    },
    CacheService,
  ],
  exports: ['REDIS_CLIENT', CacheService],
})
export class RedisModule {}

