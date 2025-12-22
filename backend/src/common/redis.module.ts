import { Module, Global } from '@nestjs/common';
import { ConfigModule, ConfigService } from '@nestjs/config';
import Redis from 'ioredis';

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
            const delay = Math.min(times * 50, 2000);
            return delay;
          },
          maxRetriesPerRequest: 3,
          enableReadyCheck: true,
          lazyConnect: true,
        };

        const client = typeof connectionOptions === 'string'
          ? new Redis(connectionOptions, commonOptions)
          : new Redis({ ...connectionOptions, ...commonOptions });

        client.on('error', (err) => {
          console.error('Redis Client Error:', err);
        });

        client.on('connect', () => {
          console.log('Redis Client Connected');
        });

        return client;
      },
      inject: [ConfigService],
    },
  ],
  exports: ['REDIS_CLIENT'],
})
export class RedisModule {}

